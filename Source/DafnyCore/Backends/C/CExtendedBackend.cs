using System.Collections.Generic;

namespace Microsoft.Dafny.Compilers;

// The EXTENDED C target (`c-extended`). Identical to the minimal `c` target
// (CBackend) — same code generator, same gcc invocation, same runtime — except it
// enables the features the C++ backend leaves out by design: unbounded `int` and
// exact-rational `real` (via GMP), and multisets. (Function values / lambdas stay
// unsupported in both C targets — C has no expression-level lambdas.) It is NOT a
// code fork: it reuses CBackend entirely and only flips the `Extended` flag (which
// selects the UnsupportedFeatures set in the shared CCodeGenerator) and the
// target id/name.
public class CExtendedBackend : CBackend {

  protected override bool Extended => true;

  public override string TargetName => "C-extended";
  public override string TargetId => "c-extended";

  public CExtendedBackend(DafnyOptions options) : base(options) {
  }
}
