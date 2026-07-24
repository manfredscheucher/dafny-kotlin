using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.CommandLine;
using System.Diagnostics.Contracts;
using System.IO;
using System.Threading.Tasks;

namespace Microsoft.Dafny.Compilers;

// The C backend is intentionally kept as close to the C++ backend as possible.
// CCodeGenerator began as a copy of CppCodeGenerator (it inherits directly from
// SinglePassCodeGenerator, like the other generators) and only differs where C
// differs from C++ (compiler invocation, language standard, file extension, no
// closures/templates). See docs/DafnyRef/integration-c/IntegrationC.md.
public class CBackend : ExecutableBackend {

  // Whether this is the EXTENDED target (`c-extended`: GMP int/real + multisets)
  // or the minimal `c` target (like the C++ backend). Overridden by
  // CExtendedBackend. Passed to the shared CCodeGenerator so one generator serves
  // both targets — no code fork. (Function values / lambdas are unsupported by
  // BOTH targets: C has no expression-level lambdas.)
  protected virtual bool Extended => false;

  protected override SinglePassCodeGenerator CreateCodeGenerator() {
    return new CCodeGenerator(Options, Reporter, OtherFileNames, Extended);
  }

  private string ComputeExeName(string targetFilename) {
    // Idiomatic binary name: "prog.exe" on Windows, extension-less "prog" on
    // macOS/Linux (a ".exe" there is un-idiomatic). Avoid colliding with the
    // "prog" source basename by keeping the .exe only where it belongs.
    var full = Path.GetFullPath(targetFilename);
    return System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
             System.Runtime.InteropServices.OSPlatform.Windows)
      ? Path.ChangeExtension(full, "exe")
      : Path.ChangeExtension(full, null);   // drop the .c extension -> bare name
  }

  public override async Task<(bool Success, object CompilationResult)> CompileTargetProgram(string dafnyProgramName,
    string targetProgramText,
    string callToMain /*?*/, string targetFilename /*?*/, ReadOnlyCollection<string> otherFileNames,
    bool runAfterCompile, IDafnyOutputWriter outputWriter) {
    var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
    Contract.Assert(assemblyLocation != null);
    var codebase = Path.GetDirectoryName(assemblyLocation);
    Contract.Assert(codebase != null);
    var gccArgs = new List<string> {
      "-Wall",
      "-Wextra",
      "-Wpedantic",
      "-Wno-unused-variable",
      "-Wno-unused-label",
      "-Wno-unused-but-set-variable",
      "-Wno-unknown-warning-option",
      "-g",
      "-std=c11",
      "-I", codebase,
    };
    // GMP (arbitrary-precision int/real) is only needed by the EXTENDED target;
    // the minimal `c` target does not use it, so don't force it to be installed.
    // Don't hardcode a package manager layout: honour DAFNY_C_GMP_PREFIX if set,
    // else fall back to the common macOS Homebrew prefix (harmless if absent).
    if (Extended) {
      // Enable the GMP-backed int/real parts of the runtime header (guarded by
      // #ifdef DAFNY_C_EXTENDED); the minimal `c` target compiles GMP-free.
      gccArgs.Add("-DDAFNY_C_EXTENDED");
      var gmpPrefix = Environment.GetEnvironmentVariable("DAFNY_C_GMP_PREFIX");
      if (string.IsNullOrEmpty(gmpPrefix) && Directory.Exists("/opt/homebrew")) {
        gmpPrefix = "/opt/homebrew";
      }
      if (!string.IsNullOrEmpty(gmpPrefix)) {
        gccArgs.Add($"-I{gmpPrefix}/include");
        gccArgs.Add($"-L{gmpPrefix}/lib");
      }
    }
    gccArgs.Add("-o");
    gccArgs.Add(ComputeExeName(targetFilename));
    gccArgs.Add(targetFilename);
    if (Extended) {
      gccArgs.Add("-lgmp");
    }
    var psi = PrepareProcessStartInfo("gcc", gccArgs);
    await using var statusWriter = outputWriter.StatusWriter();
    return (0 == await RunProcess(psi, statusWriter, statusWriter, "Error while compiling C files."), null);
  }

  public override async Task<bool> RunTargetProgram(string dafnyProgramName, string targetProgramText,
    string callToMain, /*?*/
    string targetFilename, ReadOnlyCollection<string> otherFileNames,
    object compilationResult, IDafnyOutputWriter outputWriter) {
    var psi = PrepareProcessStartInfo(ComputeExeName(targetFilename), Options.MainArgs);

    await using var sw = outputWriter.StatusWriter();
    await using var ew = outputWriter.ErrorWriter();
    return 0 == await RunProcess(psi, sw, ew);
  }

  public override Command GetCommand() {
    var cmd = base.GetCommand();
    cmd.Description = $@"Translate Dafny sources to {TargetName} source and build files.

This back-end is experimental. The `c` target is minimal and shares the C++
back-end's limitations: no support for unbounded integers (`int`), exact reals
(`real`), or multisets. For those, use the `c-extended` target, which implements
them (GMP-backed int/real, hash-table multisets). Both targets reject function
values / lambdas (C has no expression-level lambdas), traits, co-inductive types,
and other advanced features.";
    return cmd;
  }

  public override IReadOnlySet<string> SupportedExtensions => new HashSet<string> { ".h" };

  public override string TargetName => "C";
  public override bool IsStable => false;
  public override string TargetExtension => "c";
  // TargetId defaults to TargetExtension ("c"), like CppBackend — no override needed.

  public override bool SupportsInMemoryCompilation => false;

  public override bool TextualTargetIsExecutable => false;

  public CBackend(DafnyOptions options) : base(options) {
  }
}
