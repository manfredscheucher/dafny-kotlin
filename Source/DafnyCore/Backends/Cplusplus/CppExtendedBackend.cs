using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.CommandLine;
using System.Diagnostics.Contracts;
using System.IO;
using System.Threading.Tasks;

namespace Microsoft.Dafny.Compilers;

// The EXTENDED C++ target (`c++-extended`). Identical to the minimal `c++`
// target (CppBackend) — same runtime, same overall structure — except it enables
// the features the C++ backend leaves out by design: unbounded `int` (GMP
// mpz_class) and exact-rational `real` (a custom DafnyReal: an unreduced num/den
// pair of mpz_class, mirroring C#'s BigRational so real-printing matches other
// backends — plain mpq_class would reduce and diverge), multisets
// (std::unordered_multiset) and function values / lambdas (std::function). This
// makes it a full Dafny target like Java/Rust/Go/C#.
//
// It is NOT a code fork: it reuses CppBackend and swaps in
// CppExtendedCodeGenerator (a subclass of CppCodeGenerator that only overrides
// the reject sites), and adds the GMP include/link flags to the g++ invocation.
public class CppExtendedBackend : CppBackend {

  protected override SinglePassCodeGenerator CreateCodeGenerator() {
    return new CppExtendedCodeGenerator(Options, Reporter, OtherFileNames);
  }

  private string ComputeExeName(string targetFilename) {
    return Path.ChangeExtension(Path.GetFullPath(targetFilename), "exe");
  }

  public override async Task<(bool Success, object CompilationResult)> CompileTargetProgram(string dafnyProgramName,
    string targetProgramText,
    string callToMain /*?*/, string targetFilename /*?*/, ReadOnlyCollection<string> otherFileNames,
    bool runAfterCompile, IDafnyOutputWriter outputWriter) {
    var assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
    Contract.Assert(assemblyLocation != null);
    var codebase = Path.GetDirectoryName(assemblyLocation);
    Contract.Assert(codebase != null);
    var gxxArgs = new List<string> {
      "-Wall",
      "-Wextra",
      "-Wpedantic",
      "-Wno-unused-variable",
      "-Wno-deprecated-copy",
      "-Wno-unused-label",
      "-Wno-unused-but-set-variable",
      "-Wno-unknown-warning-option",
      "-g",
      "-std=c++17",
      "-I", codebase,
    };
    // GMP (arbitrary-precision int via mpz_class; real via DafnyReal, a num/den
    // pair of mpz_class). Don't hardcode a
    // package-manager layout: honour DAFNY_CPP_GMP_PREFIX if set, else fall back to
    // the common macOS Homebrew prefix (harmless if absent; on Linux GMP is
    // normally already on the default search path).
    var gmpPrefix = Environment.GetEnvironmentVariable("DAFNY_CPP_GMP_PREFIX");
    if (string.IsNullOrEmpty(gmpPrefix) && Directory.Exists("/opt/homebrew")) {
      gmpPrefix = "/opt/homebrew";
    }
    if (!string.IsNullOrEmpty(gmpPrefix)) {
      gxxArgs.Add($"-I{gmpPrefix}/include");
      gxxArgs.Add($"-L{gmpPrefix}/lib");
    }
    gxxArgs.Add("-o");
    gxxArgs.Add(ComputeExeName(targetFilename));
    gxxArgs.Add(targetFilename);
    gxxArgs.Add("-lgmpxx");
    gxxArgs.Add("-lgmp");
    var psi = PrepareProcessStartInfo("g++", gxxArgs);
    await using var statusWriter = outputWriter.StatusWriter();
    return (0 == await RunProcess(psi, statusWriter, statusWriter, "Error while compiling C++ files."), null);
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

This back-end is the C++ back-end extended with the features the minimal `c++`
target rejects by design: unbounded integers (`int`, via GMP mpz_class) and
exact reals (`real`, via a custom DafnyReal num/den pair of mpz_class),
multisets (std::unordered_multiset) and function
values / lambdas (std::function). It still shares the C++ back-end's other
limitations (traits, co-inductive types, and so on). Compiling requires GMP
(link with -lgmpxx -lgmp).";
    return cmd;
  }

  public override string TargetName => "C++-extended";
  public override string TargetId => "c++-extended";

  // Experimental: hides the translate/build subcommand from --help and keeps the
  // target out of the default every-compiler integration suite (which would then
  // require GMP on every runner). CppBackend is stable; this override is needed.
  public override bool IsStable => false;

  public CppExtendedBackend(DafnyOptions options) : base(options) {
  }
}
