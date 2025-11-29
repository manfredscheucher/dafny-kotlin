using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.CommandLine;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DafnyCore.Options;

namespace Microsoft.Dafny.Compilers;

public class KotlinBackend : ExecutableBackend {

  public static readonly Option<bool> LegacyDataConstructors = new("--legacy-data-constructors",
    "Enables legacy data constructor generation for Kotlin (default: false)");

  static KotlinBackend() {
    DafnyOptions.RegisterLegacyUi(LegacyDataConstructors, DafnyOptions.ParseBoolean, "Compilation options", legacyName: "legacyDataConstructors", defaultValue: false);
    OptionRegistry.RegisterOption(LegacyDataConstructors, OptionScope.Cli);
  }

  public override IEnumerable<Option> SupportedOptions => new List<Option> { LegacyDataConstructors };

  public override IReadOnlySet<string> SupportedExtensions => new HashSet<string> { ".kt" };

  public override string TargetName => "Kotlin";
  public override bool IsStable => false;
  public override string TargetExtension => "kt";

  public override string TargetBaseDir(string dafnyProgramName) =>
    $"{Path.GetFileNameWithoutExtension(dafnyProgramName)}-kotlin";

  public override string TargetBasename(string dafnyProgramName) => "Main";

  public override bool SupportsInMemoryCompilation => false;
  public override bool TextualTargetIsExecutable => true;

  public override IReadOnlySet<string> SupportedNativeTypes =>
    new HashSet<string> { "byte", "sbyte", "ushort", "short", "uint", "int", "number", "ulong", "long" };

  protected override SinglePassCodeGenerator CreateCodeGenerator() {
    return new KotlinCodeGenerator(Options, Reporter);
  }

  public override void CleanSourceDirectory(string sourceDirectory) {
    var buildDirectory = Path.Combine(sourceDirectory, "build");
    var gradleDirectory = Path.Combine(sourceDirectory, ".gradle");
    try {
      if (Directory.Exists(buildDirectory)) {
        Directory.Delete(buildDirectory, true);
      }
      if (Directory.Exists(gradleDirectory)) {
        Directory.Delete(gradleDirectory, true);
      }
    } catch (DirectoryNotFoundException) {
    }
  }

  public override async Task<(bool Success, object CompilationResult)> CompileTargetProgram(
    string dafnyProgramName,
    string targetProgramText,
    string callToMain /*?*/, string targetFilename /*?*/,
    ReadOnlyCollection<string> otherFileNames, bool runAfterCompile, IDafnyOutputWriter outputWriter) {

    foreach (var otherFileName in otherFileNames) {
      if (Path.GetExtension(otherFileName) != ".kt") {
        await outputWriter.Status($"Unrecognized file as extra input for Kotlin compilation: {otherFileName}");
        return (false, null);
      }
      if (!await CopyExternLibraryIntoPlace(otherFileName, targetFilename, outputWriter)) {
        return (false, null);
      }
    }

    var targetDirectory = Path.GetDirectoryName(targetFilename);
    CreateKotlinProjectStructure(targetDirectory, Path.GetFileNameWithoutExtension(dafnyProgramName));

    // Just organize files, don't try to compile
    // User can manually build with: gradle -p <target-directory> build
    return (true, null);
  }

  private void ConvertClassesToObjects(string srcMainKotlin) {
    try {
      // Find all __default.kt files and convert class to object
      var defaultFiles = Directory.GetFiles(srcMainKotlin, "__default.kt", SearchOption.AllDirectories);
      foreach (var file in defaultFiles) {
        var content = File.ReadAllText(file);
        // Replace "class __default" with "object __default"
        content = System.Text.RegularExpressions.Regex.Replace(content, @"class\s+__default\b", "object __default");
        // Remove empty constructors from objects (objects don't need constructors)
        content = System.Text.RegularExpressions.Regex.Replace(content, @"constructor\s*\(\s*\)\s*\{\s*\}", "");
        File.WriteAllText(file, content);
      }
    } catch {
      // Silently ignore errors
    }
  }

  private void ConvertJavaToKotlin(string targetDirectory) {
    try {
      // Find all Java files and convert them to Kotlin
      var javaFiles = Directory.GetFiles(targetDirectory, "*.java", SearchOption.AllDirectories);
      foreach (var javaFile in javaFiles) {
        var content = File.ReadAllText(javaFile);

        // Convert Java syntax to Kotlin
        content = ConvertJavaSyntaxToKotlin(content);

        // Rename file from .java to .kt
        var ktFile = Path.ChangeExtension(javaFile, ".kt");
        File.WriteAllText(ktFile, content);
        File.Delete(javaFile);
      }

      // Also fix any Kotlin files that may have been generated with Java syntax
      var ktFiles = Directory.GetFiles(targetDirectory, "*.kt", SearchOption.AllDirectories);
      foreach (var ktFile in ktFiles) {
        var content = File.ReadAllText(ktFile);
        var converted = ConvertJavaSyntaxToKotlin(content);
        if (converted != content) {
          File.WriteAllText(ktFile, converted);
        }
      }
    } catch {
      // Silently ignore conversion errors
    }
  }

  private string ConvertJavaSyntaxToKotlin(string javaCode) {
    var result = javaCode;

    // First, fix obvious Java syntax patterns early
    // Fix void keyword in method declarations - convert to fun (must happen before package/semicolon fixes)
    result = System.Text.RegularExpressions.Regex.Replace(result, @"\bvoid\s+(\w+)\s*\(", "fun $1(");

    // Remove Java package statement semicolon
    result = result.Replace("package _System;", "package _System");
    result = result.Replace("package _System\r", "package _System");

    // Remove trailing semicolons from statements (but be careful not to break strings)
    // This regex matches ); at the end of statements
    result = System.Text.RegularExpressions.Regex.Replace(result, @"\)\s*;(\s*\n)", ")$1");

    // Fix wildcard generics: <? : Type> -> <Type>
    result = System.Text.RegularExpressions.Regex.Replace(result, @"<\?\s*:\s+", "<");

    // Fix parameter type syntax for cases like: "fun foo(Type paramName)"
    // Changed to: "fun foo(paramName: Type)"
    // This handles nested generic types like dafny.DafnySequence<dafny.DafnySequence<dafny.CodePoint>>
    result = System.Text.RegularExpressions.Regex.Replace(result,
      @"(\()([\w.]+<[^()]*>)\s+(\w+)(\))",
      "$1$3: $2$4");

    // Additional fixes for Java syntax
    // Convert "public static void" to "fun"
    result = System.Text.RegularExpressions.Regex.Replace(result, @"\bpublic\s+static\s+void\s+", "fun ");

    // Remove remaining "public " keywords
    result = System.Text.RegularExpressions.Regex.Replace(result, @"\bpublic\s+", "");

    // Remove "static" keyword
    result = System.Text.RegularExpressions.Regex.Replace(result, @"\bstatic\s+", "");

    // Remove "final" keyword
    result = System.Text.RegularExpressions.Regex.Replace(result, @"\bfinal\s+", "");

    // Remove unnecessary "new" keyword
    result = System.Text.RegularExpressions.Regex.Replace(result, @"\bnew\s+", "");

    // Convert java.lang types
    result = result.Replace("java.lang.String", "String");
    result = result.Replace("java.lang.Object", "Any");
    result = result.Replace("java.lang.Throwable", "Throwable");

    // Convert System.out.print to print
    result = result.Replace("System.out.print(", "print(");
    result = result.Replace("System.out.println(", "println(");

    // Convert Java casting to Kotlin "as"
    result = System.Text.RegularExpressions.Regex.Replace(result, @"\((\w+(\[\])?)\)\s*(\w+)", "($3 as $1)");

    // Remove type casting parentheses for simple casts
    result = System.Text.RegularExpressions.Regex.Replace(result, @"\(\s*String\s*\)", "");

    // Fix duplicate return type annotations: "fun foo(): String: String" -> "fun foo(): String"
    result = System.Text.RegularExpressions.Regex.Replace(result, @":\s*String\s*:\s*String", ": String");

    // Add return type annotation for String-returning toString (only if not already there)
    result = System.Text.RegularExpressions.Regex.Replace(result, @"override\s+fun\s+toString\(\)(?!\s*:)", "override fun toString(): String");

    // Ensure functions with no body get an empty body (for Main function without implementation)
    result = System.Text.RegularExpressions.Regex.Replace(result, @"(fun\s+\w+\([^)]*\))\s*\n\s*\{", "$1 {\n");
    result = System.Text.RegularExpressions.Regex.Replace(result, @"(fun\s+\w+\([^)]*\))\s*$", "$1 {}", System.Text.RegularExpressions.RegexOptions.Multiline);

    // Fix ArrayList to mutableListOf
    result = result.Replace("ArrayList()", "mutableListOf()");
    result = System.Text.RegularExpressions.Regex.Replace(result, @"ArrayList<", "MutableList<");

    // Remove trailing semicolons and braces
    result = result.Replace("};", "}");

    // Convert Java lambda syntax to Kotlin
    result = System.Text.RegularExpressions.Regex.Replace(result, @"\(\)\s*->\s*\{", "{ ");
    result = System.Text.RegularExpressions.Regex.Replace(result, @"\}\s*\);", "}");

    // Add imports for dafny package if needed
    if (result.Contains("dafny.") && !result.Contains("import dafny.")) {
      var lines = result.Split('\n');
      var packageLineIndex = -1;
      for (int i = 0; i < lines.Length; i++) {
        if (lines[i].StartsWith("package ")) {
          packageLineIndex = i;
          break;
        }
      }
      if (packageLineIndex >= 0) {
        // Insert import after package declaration
        var insertPos = result.IndexOf(lines[packageLineIndex]) + lines[packageLineIndex].Length;
        result = result.Insert(insertPos + 1, "import dafny.*\n\n");
      }
    }

    return result;
  }

  private void CreateKotlinProjectStructure(string targetDirectory, string baseName) {
    try {
      // Convert generated Kotlin files to fix remaining Java syntax issues
      ConvertJavaToKotlin(targetDirectory);

      // Create source directory structure
      var srcMainKotlin = Path.Combine(targetDirectory, "src", "main", "kotlin");
      Directory.CreateDirectory(srcMainKotlin);

      var srcMainResources = Path.Combine(targetDirectory, "src", "main", "resources");
      Directory.CreateDirectory(srcMainResources);

      // Copy DafnyRuntime Kotlin file to src/main/kotlin/dafny/
      try {
        var runtimeFile = Path.Combine(Path.GetDirectoryName(typeof(KotlinBackend).Assembly.Location) ?? "", "..", "..", "..", "DafnyRuntime", "DafnyRuntimeKotlin", "src", "main", "kotlin", "dafny", "DafnyRuntimeKotlin.kt");
        var runtimeFile2 = "/Users/manfred/github/dafny-kotlin/Source/DafnyRuntime/DafnyRuntimeKotlin/src/main/kotlin/dafny/DafnyRuntimeKotlin.kt";

        var actualRuntimeFile = File.Exists(runtimeFile) ? runtimeFile : runtimeFile2;
        if (File.Exists(actualRuntimeFile)) {
          var dafnyDir = Path.Combine(srcMainKotlin, "dafny");
          Directory.CreateDirectory(dafnyDir);
          var targetRuntime = Path.Combine(dafnyDir, "DafnyRuntime.kt");
          File.Copy(actualRuntimeFile, targetRuntime, true);
        }
      } catch {
        // Silently ignore if runtime file cannot be copied
      }

      // Move .kt files to src/main/kotlin
      if (Directory.Exists(targetDirectory)) {
        var kotlinFiles = Directory.GetFiles(targetDirectory, "*.kt");
        foreach (var ktFile in kotlinFiles) {
          var fileName = Path.GetFileName(ktFile);
          var targetPath = Path.Combine(srcMainKotlin, fileName);
          if (File.Exists(targetPath)) {
            File.Delete(targetPath);
          }
          File.Move(ktFile, targetPath);
        }

        // Move subdirectories (like _System) to src/main/kotlin
        var subdirs = Directory.GetDirectories(targetDirectory);
        foreach (var subdir in subdirs) {
          var dirName = Path.GetFileName(subdir);
          if (dirName != "src" && dirName != "build" && dirName != ".gradle" && !dirName.StartsWith(".")) {
            var targetPath = Path.Combine(srcMainKotlin, dirName);
            if (Directory.Exists(targetPath)) {
              Directory.Delete(targetPath, true);
            }
            Directory.Move(subdir, targetPath);
          }
        }
      }

      // Convert class declarations to object (singletons) for default classes
      ConvertClassesToObjects(srcMainKotlin);

      // Create build.gradle.kts
      var gradleContent = GetGradleTemplate();
      var buildGradleFile = Path.Combine(targetDirectory, "build.gradle.kts");
      File.WriteAllText(buildGradleFile, gradleContent);

      // Create settings.gradle.kts
      var settingsContent = $"rootProject.name = \"{baseName}-kt\"\n";
      var settingsFile = Path.Combine(targetDirectory, "settings.gradle.kts");
      File.WriteAllText(settingsFile, settingsContent);

      // Create .gitignore
      var gitignoreContent = "build/\n.gradle/\n.gradle\n*.jar\nout/\n.idea/\n*.iml\n";
      var gitignoreFile = Path.Combine(targetDirectory, ".gitignore");
      File.WriteAllText(gitignoreFile, gitignoreContent);
    } catch {
      // Silently ignore errors in gradle structure creation
    }
  }

  private string GetGradleTemplate() {
    return @"plugins {
    kotlin(""jvm"") version ""2.1.0""
    application
}

group = ""dafny""
version = ""1.0""

repositories {
    mavenCentral()
    mavenLocal()
}

dependencies {
    testImplementation(kotlin(""test""))
}

tasks.test {
    useJUnitPlatform()
}

kotlin {
    jvmToolchain(21)
}

application {
    mainClass.set(""MainKt"")
}
";
  }

  private async Task<bool> CopyExternLibraryIntoPlace(string externFilename, string mainProgram, IDafnyOutputWriter outputWriter) {
    var mainDir = Path.GetDirectoryName(mainProgram);
    Contract.Assert(mainDir != null);
    var srcDir = Path.Combine(mainDir, "src", "main", "kotlin");
    var tgtFilename = Path.Combine(srcDir, Path.GetFileName(externFilename));
    Directory.CreateDirectory(srcDir);
    FileInfo file = new FileInfo(externFilename);
    file.CopyTo(tgtFilename, true);
    if (Options.Verbose) {
      await outputWriter.Status($"Additional input {externFilename} copied to {tgtFilename}");
    }
    return true;
  }

  public override async Task<bool> RunTargetProgram(
    string dafnyProgramName, string targetProgramText,
    string callToMain,
    string targetFilename /*?*/,
    ReadOnlyCollection<string> otherFileNames, object compilationResult,
    IDafnyOutputWriter outputWriter) {

    var targetDirectory = Path.GetDirectoryName(targetFilename);
    await outputWriter.Status($"To run your Kotlin program, execute: gradle -p {targetDirectory} run");
    return true;
  }

  public KotlinBackend(DafnyOptions options) : base(options) {
  }
}
