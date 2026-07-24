//-----------------------------------------------------------------------------
//
// Copyright by the contributors to the Dafny Project
// SPDX-License-Identifier: MIT
//
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Diagnostics.Contracts;
using System.Collections.ObjectModel;
using System.IO;
using JetBrains.Annotations;

namespace Microsoft.Dafny.Compilers {
  // The C code generator started as a verbatim copy of CppCodeGenerator and is
  // kept as close to it as possible. Each method that differs for C (no
  // namespaces, no templates, no std::/shared_ptr, no C++ runtime types) is
  // adapted in place; everything else mirrors the C++ generator so the two stay
  // easy to diff. See docs/DafnyRef/integration-c/IntegrationC.md.
  class CCodeGenerator : SinglePassCodeGenerator {

    private readonly ReadOnlyCollection<string> headers;

    // Two targets share this one generator (no code fork):
    //   * `C` (extended == false): minimal, like the C++ backend — Dafny `int`/`real`
    //     (unbounded / exact rational) and multisets are REJECTED.
    //   * `C-extended` (extended == true): those features are implemented
    //     (GMP-backed int/real, hash-table multisets).
    // Function values / lambdas are REJECTED by BOTH targets (C has no
    // expression-level lambdas). The only difference between the two targets is
    // which Features land in UnsupportedFeatures below.
    private readonly bool extended;

    public CCodeGenerator(DafnyOptions options, ErrorReporter reporter, ReadOnlyCollection<string> headers, bool extended = false) : base(options, reporter) {
      this.headers = headers;
      this.extended = extended;
    }

    // In the minimal `c` target, actively reject an extended-only feature at the
    // point where its (GMP / closure / hash-table) C is about to be emitted. Merely
    // listing it in UnsupportedFeatures is not enough — that set is declarative; the
    // real gate is an UnsupportedFeatureException at the emission site (this is how
    // the C++ backend rejects int/real too). No-op in `c-extended`.
    private void RejectIfMinimal(Feature feature, IOrigin tok) {
      if (!extended) {
        throw new UnsupportedFeatureException(tok ?? Token.NoToken, feature);
      }
    }

    // Features the EXTENDED target adds on top of the minimal (C++-like) set. In the
    // minimal `C` target these are rejected cleanly (as the C++ backend does); in
    // `C-extended` they are implemented. (`ArrayLength` needs no separate emit-site
    // guard: `a.Length` has type `int`, so the unbounded-integer rejection covers
    // it in the minimal target — it is listed here only so the feature table shows
    // it as extended-only.)
    private static readonly Feature[] ExtendedOnlyFeatures = {
      Feature.UnboundedIntegers,
      Feature.RealNumbers,
      Feature.Multisets,
      Feature.ArrayLength,
    };

    public override IReadOnlySet<Feature> UnsupportedFeatures {
      get {
        var features = new HashSet<Feature>(BaseUnsupportedFeatures);
        if (!extended) {
          features.UnionWith(ExtendedOnlyFeatures);   // minimal `C`: reject the extended features too
        }
        return features;
      }
    }

    // Features BOTH targets reject (obscure / need RTTI / genuinely unimplemented),
    // matching the C++ backend's own unsupported set.
    private static readonly Feature[] BaseUnsupportedFeatures = {
      // Function values / lambdas: C has no expression-level lambdas. Both C
      // targets reject them (like the C++ backend), which also keeps the emitted
      // code standard C11 and avoids the generic-`match` lowering that would
      // otherwise reach the base's closure emission. See IntegrationC.
      Feature.FunctionValues,
      Feature.CollectionsOfTraits,
      Feature.Codatatypes,
      Feature.ExternalClasses,
      Feature.Traits,
      Feature.Iterators,
      Feature.NonNativeNewtypes,
      Feature.RuntimeTypeDescriptors,
      Feature.MultiDimensionalArrays,
      Feature.Quantifiers,
      Feature.NewObject,
      Feature.BitvectorRotateFunctions,
      Feature.NonSequentializableForallStatements,
      Feature.Ordinals,
      Feature.MapItems,
      Feature.LetSuchThatExpressions,
      Feature.TypeTests,
      Feature.SubsetTypeTests,
      Feature.SequenceDisplaysOfCharacters,
      Feature.MapComprehensions,
      Feature.ExactBoundedPool,
      Feature.RunAllTests,
      Feature.MethodSynthesis,
      Feature.UnicodeChars,
      Feature.ConvertingValuesToStrings,
      Feature.BuiltinsInRuntime,
      Feature.RuntimeCoverageReport,
      Feature.StandardLibraries,
      Feature.StandardLibrariesActionsExterns,
      // Tuples (and multiple-return-value methods, which are lowered to tuples)
      // are not yet implemented in the C backend: the shared lowering emits C++
      // `Tuple<...>` template types and `.get<i>()` accessors that have no C
      // equivalent. Declared unsupported so they are rejected cleanly instead of
      // leaking invalid C++.
      Feature.TupleInitialization,
      Feature.TuplesWiderThan20
    };


    // ----- Monomorphisation state -------------------------------------------
    //
    // C has no templates. A generic method/function is NOT emitted parametrically;
    // instead one concrete copy is emitted per concrete instantiation that is
    // actually used at a call site. This requires the following state:
    //
    //  * activeSubst: while a concrete copy is being emitted, this maps each of
    //    the member's formal type parameters to its concrete actual. TypeName /
    //    TypeInitializationValue consult it so that residual TypeParameter types
    //    (e.g. `T`) resolve to the concrete type (e.g. `bool`) and so their
    //    default values become concrete (e.g. `false`).
    //  * activeMangleSuffix: the suffix appended to the definition name so it
    //    matches the mangled call site (e.g. `_bool`).
    //  * pendingGenericMethods / pendingGenericFunctions: the generic members
    //    seen during the single pass, together with the writers into which their
    //    concrete copies must later be emitted.
    //  * genericInstantiations: for each generic member, the set of concrete
    //    actual-type lists discovered at call sites (keyed by mangled suffix so
    //    each distinct instantiation is emitted exactly once).
    private Dictionary<TypeParameter, Type> activeSubst = null;
    private string activeMangleSuffix = null;

    private class PendingMethod {
      public MethodOrConstructor Member;
      public ConcreteSyntaxTree DeclWriter;
      public ConcreteSyntaxTree DefWriter;
      public bool LookasideBody;
    }
    private class PendingFunction {
      public Function Member;
      public string ClassName;
      public ConcreteSyntaxTree DeclWriter;
      public ConcreteSyntaxTree DefWriter;
      public bool LookasideBody;
    }
    private readonly List<PendingMethod> pendingGenericMethods = [];
    private readonly List<PendingFunction> pendingGenericFunctions = [];
    // member -> (mangled suffix -> concrete actual type list)
    private readonly Dictionary<MemberDecl, Dictionary<string, List<Type>>> genericInstantiations = new();
    // Persistent dedup across the (possibly repeated) EmitConcreteInstantiations
    // invocations in EmitFooter.
    private readonly HashSet<string> emittedGenericMethods = new();
    private readonly HashSet<string> emittedGenericFunctions = new();

    // ----- Generic reference-class monomorphisation -------------------------
    //
    // A generic reference class `class Box<T> { ... }` has no C-templates analog,
    // so — exactly like a generic datatype — it is monomorphised: one concrete
    // heap struct + one concrete set of member functions is emitted per concrete
    // instantiation actually used (Box<bool>, Box<int32>, ...).
    //
    //  * On the single parametric pass, CreateClass DEFERS a generic ref class:
    //    it records the class and the REAL target writers here and hands the base
    //    a scratch ClassWriter, so the base's parametric member emission (which
    //    would contain the invalid formal type parameter `T`) is discarded.
    //  * Each concrete use site (var b: Box<bool>, new Box<bool>(...)) flows
    //    through TypeName, which registers the instantiation in
    //    classInstantiations (mangled name -> class + concrete actuals) and
    //    returns the mangled struct name `_module_Box_bool`.
    //  * EmitFooter drains classInstantiations: for each, it installs the class's
    //    type-parameter substitution (activeSubst) plus the matching mangle
    //    context (activeClassDecl / activeClassSuffix, consulted by RefClassName),
    //    emits the concrete struct, and re-drives the base member compilation into
    //    the captured real writers. Because RefClassName then returns the mangled
    //    name, the emitted method/constructor names (`_module_Box_bool___ctor`,
    //    `_module_Box_bool_Get`) match the mangled companion call sites.
    private class PendingClass {
      public TopLevelDeclWithMembers Cls;
      public ConcreteSyntaxTree DeclWriter;   // header region for member prototypes
      public ConcreteSyntaxTree DefWriter;    // source region for member definitions
    }
    private readonly List<PendingClass> pendingGenericClasses = [];
    // mangled struct name -> (class decl, concrete actual type args)
    private readonly Dictionary<string, (TopLevelDeclWithMembers Cls, List<Type> Actuals)> classInstantiations = new();
    // While a concrete class instantiation is being emitted: the class being
    // instantiated and the mangle suffix (e.g. "_bool") RefClassName appends.
    private TopLevelDecl activeClassDecl = null;
    private string activeClassSuffix = null;
    // The whole program, captured in EmitHeader, so EmitFooter can re-drive the
    // base's member compilation (CompileClassMembers) for each class instantiation.
    private Program program_ = null;

    private static bool IsGeneric(MemberDecl m) =>
      m is MethodOrFunction mf && mf.TypeArgs != null && mf.TypeArgs.Count > 0;

    // Record that "member" is used at a call site with the given concrete
    // "actuals" (one per formal member type parameter). De-duplicated by suffix.
    private void RegisterInstantiation(MemberDecl member, List<Type> actuals) {
      if (actuals == null || actuals.Count == 0) {
        return;
      }
      // Normalise the actuals so the mangled name and the substitution map are
      // built from fully-resolved concrete types.
      var concrete = actuals.ConvertAll(t => t.NormalizeExpand());
      // Ignore instantiations that are not yet concrete. This happens on the
      // first pass when a generic body (whose emission is deferred) calls another
      // generic member passing along its own formal type parameters. Those calls
      // are re-registered with concrete actuals when the enclosing generic member
      // is re-emitted (with a substitution map installed).
      if (concrete.Exists(ContainsTypeParameter)) {
        return;
      }
      var suffix = MangleTypeArgs(concrete);
      if (!genericInstantiations.TryGetValue(member, out var bySuffix)) {
        bySuffix = new Dictionary<string, List<Type>>();
        genericInstantiations[member] = bySuffix;
      }
      bySuffix.TryAdd(suffix, concrete);
    }

    // Resolve a type through the currently-installed substitution map, if any,
    // so residual formal type parameters become their concrete actuals.
    private Type ApplyActiveSubst(Type type) {
      return activeSubst == null ? type : type.Subst(activeSubst);
    }

    // True if the (already normalised) type still mentions a formal type
    // parameter anywhere, i.e. it is not yet a concrete instantiation.
    private static bool ContainsTypeParameter(Type type) {
      var t = type.NormalizeExpand();
      if (t.IsTypeParameter) {
        return true;
      }
      return t.TypeArgs.Exists(ContainsTypeParameter);
    }

    // Register a call to a (possibly generic) method. The concrete actuals for
    // the method's own type parameters are TypeApplicationJustMember; they are
    // resolved through any currently-active substitution (so a generic body that
    // calls another generic contributes concrete instantiations).
    protected override void TrCallStmt(CallStmt s, string receiverReplacement, ConcreteSyntaxTree wr,
      ConcreteSyntaxTree wStmts, ConcreteSyntaxTree wStmtsAfterCall) {
      if (IsGeneric(s.Method)) {
        RegisterInstantiation(s.Method,
          s.MethodSelect.TypeApplicationJustMember.ConvertAll(ApplyActiveSubst));
      }
      base.TrCallStmt(s, receiverReplacement, wr, wStmts, wStmtsAfterCall);
    }

    // Register a call to a (possibly generic) function.
    protected override void CompileFunctionCallExpr(FunctionCallExpr e, ConcreteSyntaxTree wr, bool inLetExprBody,
      ConcreteSyntaxTree wStmts, FCE_Arg_Translator tr, bool alreadyCoerced = false) {
      if (IsGeneric(e.Function)) {
        RegisterInstantiation(e.Function,
          e.TypeApplication_JustFunction.ConvertAll(ApplyActiveSubst));
      }
      base.CompileFunctionCallExpr(e, wr, inLetExprBody, wStmts, tr, alreadyCoerced);
    }

    // Emit every concrete copy of every generic member collected during the
    // single pass. Because emitting a concrete body can itself discover new
    // instantiations (a generic member calling another generic member), the
    // pending lists are drained on a worklist until no new work remains.
    //
    // For each (member, concrete-actuals) pair we install a substitution map and
    // the matching mangled suffix, then re-drive the body emission through the
    // same TrStmtList / CompileReturnBody machinery the base uses. This yields a
    // fully concrete definition (concrete types, concrete defaults) whose name
    // matches the mangled call site.
    private void EmitConcreteInstantiations() {
      // Snapshot the discovered generic members so we can iterate deterministically.
      var methods = new List<PendingMethod>(pendingGenericMethods);
      var functions = new List<PendingFunction>(pendingGenericFunctions);

      // Track what we have already emitted so re-discovering the same
      // instantiation (via transitive calls) does not emit it twice. These are
      // instance fields so dedup persists across the multiple invocations of this
      // method in EmitFooter (e.g. re-run after class instantiations register
      // more generic-member uses).
      var emittedMethods = emittedGenericMethods;
      var emittedFunctions = emittedGenericFunctions;

      bool madeProgress = true;
      while (madeProgress) {
        madeProgress = false;

        foreach (var pm in methods) {
          if (!genericInstantiations.TryGetValue(pm.Member, out var bySuffix)) {
            continue;
          }
          // Copy to allow the collection to grow while we iterate.
          foreach (var kv in new List<KeyValuePair<string, List<Type>>>(bySuffix)) {
            var key = pm.Member.FullName + kv.Key;
            if (!emittedMethods.Add(key)) {
              continue;
            }
            madeProgress = true;
            EmitConcreteMethod(pm, kv.Key, kv.Value);
          }
        }

        foreach (var pf in functions) {
          if (!genericInstantiations.TryGetValue(pf.Member, out var bySuffix)) {
            continue;
          }
          foreach (var kv in new List<KeyValuePair<string, List<Type>>>(bySuffix)) {
            var key = pf.Member.FullName + kv.Key;
            if (!emittedFunctions.Add(key)) {
              continue;
            }
            madeProgress = true;
            EmitConcreteFunction(pf, kv.Key, kv.Value);
          }
        }
      }
    }

    private void EmitConcreteMethod(PendingMethod pm, string suffix, List<Type> actuals) {
      var m = pm.Member;
      var savedSubst = activeSubst;
      var savedSuffix = activeMangleSuffix;
      var savedEnclosing = enclosingMethod;
      try {
        activeSubst = BuildSubst(m.TypeArgs, actuals);
        activeMangleSuffix = suffix;

        // Re-drive the equivalent of the base CompileMethod body emission, but
        // into the real writers with the substitution installed. CreateMethod
        // now emits real output (activeSubst != null) rather than deferring.
        var w = CreateMethod(m, null, true, pm.DeclWriter, pm.DefWriter, pm.LookasideBody);
        if (w == null) {
          return;
        }
        if (m.IsTailRecursive) {
          w = EmitTailCallStructure(m, w);
        }
        var useReturnStyleOuts = UseReturnStyleOuts(m, m.Outs.Count(p => !p.IsGhost));
        foreach (var p in m.Outs) {
          if (!p.IsGhost) {
            DeclareLocalOutVar(IdName(p), p.Type, p.Origin, PlaceboValue(p.Type, w, p.Origin, true), useReturnStyleOuts, w);
          }
        }
        w = EmitMethodReturns(m, w);

        enclosingMethod = m;
        if (m.Body is DividedBlockStmt dividedBlockStmt) {
          TrDividedBlockStmt((Constructor)m, dividedBlockStmt, w);
        } else {
          TrStmtList(m.Body.Body, w);
        }
      } finally {
        enclosingMethod = savedEnclosing;
        activeSubst = savedSubst;
        activeMangleSuffix = savedSuffix;
      }
    }

    private void EmitConcreteFunction(PendingFunction pf, string suffix, List<Type> actuals) {
      var f = pf.Member;
      var savedSubst = activeSubst;
      var savedSuffix = activeMangleSuffix;
      var savedEnclosing = enclosingFunction;
      try {
        activeSubst = BuildSubst(f.TypeArgs, actuals);
        activeMangleSuffix = suffix;

        var w = CreateFunction(pf.ClassName, f.EnclosingClass.TypeArgs, IdName(f), null,
          f.Ins, f.ResultType, f.Origin, f.IsStatic, true, f, pf.DeclWriter, pf.DefWriter, pf.LookasideBody);
        if (w == null) {
          return;
        }
        if (f.IsTailRecursive) {
          w = EmitTailCallStructure(f, w);
        }
        enclosingFunction = f;
        // The base-class CompileReturnBody (which lowers if/match function bodies
        // and tail-recursive accumulators) is private and not reachable here, so
        // this monomorphised path emits the body directly with EmitReturnExpr.
        // That is correct for plain and if-then-else EXPRESSION bodies, but a
        // `match` body is lowered by the base into an IIFE (a C#/JS closure:
        // `return function () { ... }`) that is not valid C. Rather than emit that
        // broken code, reject a generic function with a match body cleanly.
        // (Non-generic functions with match bodies go through the base's own
        // CompileReturnBody and work; only this monomorphised copy is affected.)
        if (f.Body is MatchExpr or NestedMatchExpr) {
          throw new UnsupportedFeatureException(f.Origin, Feature.FunctionValues,
            "a generic function with a `match` expression body is not supported by the C backend " +
            "(the monomorphised copy cannot reach the base's match-lowering, which would otherwise " +
            "emit an invalid C#/JS-style closure); rewrite the body as if-then-else, or extract a " +
            "non-generic helper (a non-generic function with a match body works)");
        }
        EmitReturnExpr(f.Body, f.OriginalResultTypeWithRenamings(), false, w);
      } finally {
        enclosingFunction = savedEnclosing;
        activeSubst = savedSubst;
        activeMangleSuffix = savedSuffix;
      }
    }

    private static Dictionary<TypeParameter, Type> BuildSubst(List<TypeParameter> formals, List<Type> actuals) {
      var subst = new Dictionary<TypeParameter, Type>();
      Contract.Assert(formals.Count == actuals.Count);
      for (var i = 0; i < formals.Count; i++) {
        subst[formals[i]] = actuals[i];
      }
      return subst;
    }

    /*
     * Unlike other Dafny and Dafny's other backends, C++ cares about
     * the order in which types are declared.  To make this more likely
     * to succeed, we emit type information as gradually as possible
     * in hopes that definitions are in place when needed.
     */

    // Forward declarations of class and struct names
    private ConcreteSyntaxTree modDeclsWr = null;
    private ConcreteSyntaxTree modDeclWr = null;
    // Dafny datatype declarations
    private ConcreteSyntaxTree dtDeclsWr = null;
    // Dafny class declarations
    private ConcreteSyntaxTree classDeclsWr = null;
    private ConcreteSyntaxTree classDeclWr = null;
    // Reference-class struct definitions (typedef struct NAME { fields } NAME;).
    // Rendered near the top of the header, BEFORE any module/method declaration
    // that mentions NAME* (a class becomes a heap struct + flat free functions;
    // an instance member takes an explicit leading NAME* this parameter).
    private ConcreteSyntaxTree classStructsWr = null;

    // ----- Sequence monomorphisation ---------------------------------------
    // C has no templates, so seq<T> is monomorphised the same way as generic
    // members: one concrete C struct + set of helper functions per element type
    // actually used. seqDeclsWr (near the top of the header) receives the
    // DAFNY_SEQ_DECL(...) lines; seqDefsWr (in the source file) receives the
    // DAFNY_SEQ_DEFINE(...) lines. seqElementTypes de-duplicates by the mangled
    // element-type suffix (e.g. "char", "int32") so each type is emitted once.
    private ConcreteSyntaxTree seqDeclsWr = null;
    private ConcreteSyntaxTree seqDefsWr = null;
    private readonly Dictionary<string, Type> seqElementTypes = new();

    // ----- Array monomorphisation ------------------------------------------
    // A 1-D array<T> is monomorphised the same way as seq<T>: one concrete
    // struct DafnyArray_<elem> + allocator per element type actually used.
    // arrayDeclsWr (in the header) receives DAFNY_ARRAY_DECL(...) lines;
    // arrayDefsWr (in the source) receives DAFNY_ARRAY_DEFINE(...) lines.
    // arrayElementTypes de-duplicates by the mangled element-type suffix.
    private ConcreteSyntaxTree arrayDeclsWr = null;
    private ConcreteSyntaxTree arrayDefsWr = null;
    private readonly Dictionary<string, Type> arrayElementTypes = new();

    // Arrow (function value) types are unsupported by the C backend (C has no
    // expression-level lambdas). Every arrow-typed value, lambda, application,
    // conversion or default is rejected with Feature.FunctionValues at its emit
    // site (EmitExpr, EmitApplyExpr, EmitSeqConstructionExpr, EmitConversionExpr,
    // TypeName, FullTypeName, TypeInitializationValue).

    // Register an array<elem> use so the concrete struct + allocator get
    // emitted. Returns the mangled element-type suffix (NAME in DafnyArray_NAME).
    private string RegisterArrayElementType(Type elemType) {
      var resolved = ApplyActiveSubst(elemType).NormalizeExpand();
      var suffix = MangleType(resolved);
      // Skip the discarded generic-template walk (see RegisterSeqElementType).
      if (!ContainsTypeParameter(resolved)) {
        arrayElementTypes.TryAdd(suffix, resolved);
      }
      return suffix;
    }

    // Emit one DAFNY_ARRAY_DECL / DAFNY_ARRAY_DEFINE per registered element type.
    // Called from EmitFooter, after the whole program has been walked. The
    // element DEFAULT value matches Dafny/C# zero-initialisation of a fresh
    // array (0 / false / null / default(struct)).
    private void EmitArrayInstantiations() {
      bool madeProgress = true;
      var emitted = new HashSet<string>();
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, Type>>(arrayElementTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          var elemName = TypeName(kv.Value, null, Token.NoToken, null, false);
          var elemDefault = DefaultValue(kv.Value, arrayDefsWr, Token.NoToken);
          arrayDeclsWr.WriteLine("DAFNY_ARRAY_DECL({0}, {1})", kv.Key, elemName);
          arrayDefsWr.WriteLine("DAFNY_ARRAY_DEFINE({0}, {1}, {2})", kv.Key, elemName, elemDefault);
        }
      }
    }

    // Register a seq<elem> use so the concrete struct + helpers get emitted.
    // Returns the mangled element-type suffix (the "NAME" in DafnySequence_NAME).
    private string RegisterSeqElementType(Type elemType) {
      var resolved = ApplyActiveSubst(elemType).NormalizeExpand();
      var suffix = MangleType(resolved);
      // Do not schedule an instantiation whose element type is still a bare
      // formal type parameter (`T`). That only happens on the discarded generic
      // template walk (activeSubst == null): the base still renders the
      // parametric body/signature into a scratch buffer, and `seq<T>` there would
      // otherwise register `__T` — which has no concrete struct or value hash/eq,
      // and would later make EmitSeqInstantiations throw. The real, concrete
      // element type is registered when the concrete instantiation is emitted
      // (activeSubst != null). The returned name only feeds the scratch buffer.
      if (!ContainsTypeParameter(resolved)) {
        seqElementTypes.TryAdd(suffix, resolved);
      }
      return suffix;
    }

    // Emit one DAFNY_SEQ_DECL / DAFNY_SEQ_DEFINE per registered element type.
    // Called from EmitFooter, after the whole program (and all concrete generic
    // instantiations) has been walked, so every seq element type is known. The
    // decls/defs render at their forked positions (header top / source top).
    private void EmitSeqInstantiations() {
      // Copy: registering a seq element type can itself add more (a concrete
      // generic body emitted by EmitConcreteInstantiations may use new seqs).
      bool madeProgress = true;
      var emitted = new HashSet<string>();
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, Type>>(seqElementTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          // The "char" sequence (strings) is pre-instantiated in the runtime
          // header; do not emit a conflicting second definition.
          if (kv.Key == "char") {
            continue;
          }
          var elemName = TypeName(kv.Value, null, Token.NoToken, null, false);
          var (elemHash, elemEq) = ValueHashEq(kv.Value);
          seqDeclsWr.WriteLine("DAFNY_SEQ_DECL({0}, {1})", kv.Key, elemName);
          seqDefsWr.WriteLine("DAFNY_SEQ_DEFINE({0}, {1}, {2}, {3})", kv.Key, elemName, elemHash, elemEq);
        }
      }
    }

    // ----- Sequence printing ------------------------------------------------
    // `print s` for a seq<E> (other than seq<char> strings, which have their own
    // dedicated dafny_print_seq_char) is compiled to a call to a generated
    // dafny_print_seq_<NAME>(DafnySequence_<NAME> s) helper that renders the
    // Dafny textual form "[e0, e1, ...]", printing each element via its own
    // per-type printer. Helpers are registered on demand (nested seqs register
    // their inner printer recursively) and emitted from EmitFooter.
    // seqPrintTypes maps the mangled element suffix -> element type.
    private ConcreteSyntaxTree seqPrintDeclsWr = null;
    private ConcreteSyntaxTree seqPrintDefsWr = null;
    private readonly Dictionary<string, Type> seqPrintTypes = new();

    // Register a `print` of seq<elem> and return the mangled element suffix (the
    // NAME in dafny_print_seq_NAME). Also registers the seq struct itself.
    private string RegisterSeqPrinter(Type elemType) {
      var resolved = ApplyActiveSubst(elemType).NormalizeExpand();
      RegisterSeqElementType(resolved);
      var suffix = MangleType(resolved);
      // seq<char> (a string) is printed by the runtime's dafny_print_seq_char;
      // never generate a helper for it (and never register one recursively).
      // Also skip a still-generic element type from the discarded template walk
      // (see RegisterSeqElementType) — the concrete printer is registered later.
      if (suffix != "char" && !ContainsTypeParameter(resolved)) {
        seqPrintTypes.TryAdd(suffix, resolved);
      }
      return suffix;
    }

    // Return the C statement that prints a single element value-expression `e` of
    // type `elem`, registering any helper it needs. Throws if the element type is
    // not printable (e.g. a set/map/datatype element).
    private string ElementPrintStmt(Type elem, string e) {
      var t = ApplyActiveSubst(elem).NormalizeExpand();
      var native = AsNativeType(t);
      if (t.IsCharType) {
        // A char element prints the character itself (no quotes), via the runtime
        // helper — matching how the other backends print a `char`.
        return string.Format("dafny_print_char({0});", e);
      }
      if ((t.IsNumericBased(Type.NumericPersuasion.Int) || t is BigOrdinalType) && native == null) {
        // Extended: GMP-backed DafnyInt via the dedicated printer. Minimal: an
        // `int` here is a native size_t (cardinality), printed via _Generic.
        return string.Format(extended ? "dafny_print_int({0});" : "dafny_print({0});", e);
      }
      if (t.IsNumericBased(Type.NumericPersuasion.Real)) {
        return string.Format("dafny_print_real({0});", e);
      }
      if (t.IsBoolType || native != null) {
        return string.Format("dafny_print({0});", e);
      }
      var seq = t.NormalizeToAncestorType().AsSeqType;
      if (seq != null) {
        if (seq.Arg.NormalizeExpand().IsCharType) {
          // A string element prints verbatim (no quotes/brackets), like C#.
          return string.Format("dafny_print_seq_char({0});", e);
        }
        var inner = RegisterSeqPrinter(seq.Arg);
        return string.Format("dafny_print_seq_{0}({1});", inner, e);
      }
      if (t is UserDefinedType tup && tup.ResolvedClass is TupleTypeDecl) {
        var suffix = RegisterTuplePrinter(NonGhostTupleArgs(tup));
        return string.Format("dafny_print_tuple_{0}({1});", suffix, e);
      }
      if (t is UserDefinedType dtType && dtType.ResolvedClass is DatatypeDecl dtDecl && dtDecl is not TupleTypeDecl) {
        var instName = RegisterDatatypePrinter(dtType);
        return string.Format("dafny_print_dt_{0}({1});", instName, e);
      }
      throw new UnsupportedFeatureException(Token.NoToken, Feature.ConvertingValuesToStrings,
        "printing a sequence whose elements are of type '" +
        TypeName(t, null, Token.NoToken, null, false) + "' is not supported by the C backend");
    }

    // ----- Tuple printing ---------------------------------------------------
    // `print t` for a tuple `(T0, T1, ...)` is compiled to a generated
    // dafny_print_tuple_<suffix>(DafnyTuple_<suffix> _t) helper that renders the
    // Dafny textual form "(e0, e1, ...)", printing each field via its own
    // per-type printer (reusing ElementPrintStmt). Registered on demand (nested
    // tuples register their inner printer recursively) and emitted from
    // EmitFooter. tuplePrintTypes maps the mangled suffix -> element types.
    private ConcreteSyntaxTree tuplePrintDeclsWr = null;
    private ConcreteSyntaxTree tuplePrintDefsWr = null;
    private readonly Dictionary<string, List<Type>> tuplePrintTypes = new();

    // Register a `print` of a tuple type and return the mangled suffix (the NAME
    // in dafny_print_tuple_NAME / DafnyTuple_NAME). Also registers the struct.
    private string RegisterTuplePrinter(List<Type> elemTypes) {
      RegisterTupleReturn(elemTypes);  // schedules the struct typedef
      var resolved = elemTypes.ConvertAll(t => ApplyActiveSubst(t).NormalizeExpand());
      var suffix = string.Join("_", resolved.ConvertAll(MangleType));
      if (!resolved.Exists(ContainsTypeParameter)) {
        tuplePrintTypes.TryAdd(suffix, resolved);
      }
      return suffix;
    }

    // Tuple value equality (parallel to the printer). A tuple is a struct, so C ==
    // is invalid; emit a per-shape dafny_tuple_eq_<suffix> comparing each field by
    // its own value equality. tupleEqTypes maps the mangled suffix -> element types.
    private ConcreteSyntaxTree tupleEqDeclsWr = null;
    private ConcreteSyntaxTree tupleEqDefsWr = null;
    private readonly Dictionary<string, List<Type>> tupleEqTypes = new();

    private string RegisterTupleEq(List<Type> elemTypes) {
      RegisterTupleReturn(elemTypes);
      var resolved = elemTypes.ConvertAll(t => ApplyActiveSubst(t).NormalizeExpand());
      var suffix = string.Join("_", resolved.ConvertAll(MangleType));
      if (!resolved.Exists(ContainsTypeParameter)) {
        tupleEqTypes.TryAdd(suffix, resolved);
      }
      return suffix;
    }

    // If `t` is a tuple type, register its equality helper and return the mangled
    // suffix; otherwise null. (The empty/all-ghost tuple has no non-ghost fields
    // and compares equal trivially.)
    private string TupleEqSuffix(Type t) {
      var nt = ApplyActiveSubst(t).NormalizeExpand();
      if (nt is UserDefinedType udt && udt.ResolvedClass is TupleTypeDecl) {
        return RegisterTupleEq(NonGhostTupleArgs(udt));
      }
      return null;
    }

    private readonly HashSet<string> emittedTupleEq = new();

    private void EmitTupleEqInstantiations() {
      bool madeProgress = true;
      var emitted = emittedTupleEq;
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, List<Type>>>(tupleEqTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          var name = kv.Key;
          var fieldEqs = new List<string>();
          for (var i = 0; i < kv.Value.Count; i++) {
            fieldEqs.Add(FieldEqExpr(kv.Value[i], $"_a._{i}", $"_b._{i}"));
          }
          tupleEqDeclsWr.WriteLine("static bool dafny_tuple_eq_{0}(DafnyTuple_{0} _a, DafnyTuple_{0} _b);", name);
          var wf = tupleEqDefsWr.NewBlock(
            string.Format("static bool dafny_tuple_eq_{0}(DafnyTuple_{0} _a, DafnyTuple_{0} _b)", name));
          wf.WriteLine("return {0};", fieldEqs.Count == 0 ? "true" : string.Join(" && ", fieldEqs));
        }
      }
    }

    private void EmitTuplePrintInstantiations() {
      bool madeProgress = true;
      var emitted = new HashSet<string>();
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, List<Type>>>(tuplePrintTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          var name = kv.Key;
          // Compute each field's print statement first (may register more helpers).
          var fieldPrints = new List<string>();
          for (var i = 0; i < kv.Value.Count; i++) {
            fieldPrints.Add(ElementPrintStmt(kv.Value[i], string.Format("_t._{0}", i)));
          }
          tuplePrintDeclsWr.WriteLine("static void dafny_print_tuple_{0}(DafnyTuple_{0} _t);", name);
          var wf = tuplePrintDefsWr.NewBlock(
            string.Format("static void dafny_print_tuple_{0}(DafnyTuple_{0} _t)", name));
          // Always parenthesize: "(a, b)", "(30)", "()". This backend never erases
          // datatype/tuple wrappers (SupportsDatatypeWrapperErasure = false), so a
          // 1-tuple stays a tuple and prints as "(x)" — matching Dafny under
          // --optimize-erasable-datatype-wrapper:false. (With erasure ON, the
          // frontend rewrites a 1-tuple to its bare element before we ever see it.)
          wf.WriteLine("putchar('(');");
          for (var i = 0; i < fieldPrints.Count; i++) {
            if (i > 0) {
              wf.WriteLine("printf(\", \");");
            }
            wf.WriteLine(fieldPrints[i]);
          }
          wf.WriteLine("putchar(')');");
        }
      }
    }

    private void EmitSeqPrintInstantiations() {
      bool madeProgress = true;
      var emitted = new HashSet<string>();
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, Type>>(seqPrintTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          var name = kv.Key;                 // mangled element suffix
          var elemPrint = ElementPrintStmt(kv.Value, "_s.data[_i]");  // may register more
          seqPrintDeclsWr.WriteLine("static void dafny_print_seq_{0}(DafnySequence_{0} _s);", name);
          var wf = seqPrintDefsWr.NewBlock(
            string.Format("static void dafny_print_seq_{0}(DafnySequence_{0} _s)", name));
          wf.WriteLine("putchar('[');");
          var loop = wf.NewBlock("for (size_t _i = 0; _i < _s.len; _i++)");
          loop.WriteLine("if (_i > 0) { printf(\", \"); }");
          loop.WriteLine(elemPrint);
          wf.WriteLine("putchar(']');");
        }
      }
    }

    // ----- Set monomorphisation --------------------------------------------
    // set<T> is monomorphised exactly like seq<T>: one concrete C struct + set
    // of helper functions per element type. setDeclsWr (near the top of the
    // header) receives DAFNY_SET_DECL(...) lines; setDefsWr (in the source file)
    // receives DAFNY_SET_DEFINE(...) lines. setElementTypes de-duplicates by the
    // mangled element-type suffix.
    private ConcreteSyntaxTree setDeclsWr = null;
    private ConcreteSyntaxTree setDefsWr = null;
    private readonly Dictionary<string, Type> setElementTypes = new();

    // Register a set<elem> use. Returns the mangled element-type suffix (the
    // "NAME" in DafnySet_NAME).
    private string RegisterSetElementType(Type elemType) {
      var resolved = ApplyActiveSubst(elemType).NormalizeExpand();
      var suffix = MangleType(resolved);
      // Skip the discarded generic-template walk (see RegisterSeqElementType).
      if (!ContainsTypeParameter(resolved)) {
        setElementTypes.TryAdd(suffix, resolved);
      }
      return suffix;
    }

    // Persistent across calls: EmitFooter drains the set/map/multiset struct
    // emitters twice (once before, once after the print emitters, since a printed
    // element may introduce a new collection element type). Tracking emitted keys
    // in instance fields makes the second drain idempotent — it emits only the
    // freshly introduced types, never a duplicate DAFNY_*_DECL (which would be a
    // typedef redefinition and fail to compile).
    private readonly HashSet<string> emittedSetInsts = new();
    private readonly HashSet<string> emittedMultisetInsts = new();
    private readonly HashSet<string> emittedMapInsts = new();
    private readonly HashSet<string> emittedMsFromSet = new();

    private void EmitSetInstantiations() {
      bool madeProgress = true;
      var emitted = emittedSetInsts;
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, Type>>(setElementTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          var elemName = TypeName(kv.Value, null, Token.NoToken, null, false);
          var (elemHash, elemEq) = ValueHashEq(kv.Value);
          setDeclsWr.WriteLine("DAFNY_SET_DECL({0}, {1})", kv.Key, elemName);
          setDefsWr.WriteLine("DAFNY_SET_DEFINE({0}, {1}, {2}, {3})", kv.Key, elemName, elemHash, elemEq);
        }
      }
    }

    // ----- Multiset monomorphisation ---------------------------------------
    // multiset<T> is monomorphised exactly like set<T>: one concrete C struct +
    // set of helper functions per element type. multisetDeclsWr (in the header)
    // receives DAFNY_MULTISET_DECL(...) lines; multisetDefsWr (in the source
    // file) receives DAFNY_MULTISET_DEFINE(...) lines. multisetElementTypes
    // de-duplicates by the mangled element-type suffix.
    private ConcreteSyntaxTree multisetDeclsWr = null;
    private ConcreteSyntaxTree multisetDefsWr = null;
    private readonly Dictionary<string, Type> multisetElementTypes = new();

    // Register a multiset<elem> use. Returns the mangled element-type suffix
    // (the "NAME" in DafnyMultiset_NAME).
    private string RegisterMultisetElementType(Type elemType) {
      var resolved = ApplyActiveSubst(elemType).NormalizeExpand();
      var suffix = MangleType(resolved);
      // Skip the discarded generic-template walk (see RegisterSeqElementType).
      if (!ContainsTypeParameter(resolved)) {
        multisetElementTypes.TryAdd(suffix, resolved);
      }
      return suffix;
    }

    // Bridge functions building a multiset from a set (the multiset(set)
    // conversion). Keyed by "<multisetSuffix>__<setSuffix>" -> element type.
    private readonly Dictionary<string, (string MsSuffix, string SetSuffix, Type Elem)> multisetFromSet = new();

    private void RegisterMultisetFromSet(string msSuffix, string setSuffix, Type elem) {
      multisetFromSet.TryAdd(msSuffix + "__" + setSuffix, (msSuffix, setSuffix, elem));
    }

    private void EmitMultisetInstantiations() {
      bool madeProgress = true;
      var emitted = emittedMultisetInsts;
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, Type>>(multisetElementTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          var elemName = TypeName(kv.Value, null, Token.NoToken, null, false);
          var (elemHash, elemEq) = ValueHashEq(kv.Value);
          multisetDeclsWr.WriteLine("DAFNY_MULTISET_DECL({0}, {1})", kv.Key, elemName);
          multisetDefsWr.WriteLine("DAFNY_MULTISET_DEFINE({0}, {1}, {2}, {3})", kv.Key, elemName, elemHash, elemEq);
        }
      }
      // Emit set->multiset bridge functions. Both the DafnySet_<set> and
      // DafnyMultiset_<ms> structs are already declared in the header, so these
      // free functions in the source file compile fine. Guarded by emittedMsFromSet
      // so the second EmitFooter drain doesn't redefine a bridge.
      foreach (var kv in multisetFromSet) {
        if (!emittedMsFromSet.Add(kv.Key)) {
          continue;
        }
        var ms = kv.Value.MsSuffix;
        var st = kv.Value.SetSuffix;
        var elemName = TypeName(kv.Value.Elem, null, Token.NoToken, null, false);
        multisetDefsWr.WriteLine(
          "static DafnyMultiset_{0} dafny_multiset_{0}_from_set_{1}(DafnySet_{1} _s) {{ " +
          "DafnyMultiset_{0} _m = dafny_multiset_{0}_create(0, NULL); " +
          "for (size_t _j = 0; _j < _s.cap; _j++) {{ if (_s.used[_j]) {{ " +
          "{2} _e = _s.slots[_j]; _m = dafny_multiset_{0}_union(_m, dafny_multiset_{0}_create(1, ({2}[]){{ _e }})); }} }} " +
          "return _m; }}",
          ms, st, elemName);
      }
    }

    // ----- Map monomorphisation --------------------------------------------
    // map<K,V> is monomorphised one concrete C struct + helpers per (key,value)
    // type pair. mapTypes de-duplicates by the mangled "<key>_<val>" suffix.
    private ConcreteSyntaxTree mapDeclsWr = null;
    private ConcreteSyntaxTree mapDefsWr = null;
    private readonly Dictionary<string, (Type Key, Type Val)> mapTypes = new();

    // Register a map<key,val> use. Returns the mangled "<key>_<val>" suffix (the
    // "NAME" in DafnyMap_NAME).
    private string RegisterMapType(Type keyType, Type valType) {
      var rk = ApplyActiveSubst(keyType).NormalizeExpand();
      var rv = ApplyActiveSubst(valType).NormalizeExpand();
      var suffix = MangleType(rk) + "_" + MangleType(rv);
      // Skip the discarded generic-template walk (see RegisterSeqElementType).
      if (!ContainsTypeParameter(rk) && !ContainsTypeParameter(rv)) {
        mapTypes.TryAdd(suffix, (rk, rv));
      }
      return suffix;
    }

    // Register (and return the mangled suffix for) the map type behind an
    // expression's static type. Used by binary-op emission where only the
    // operand Type (a map<K,V>) is available.
    private string MapSuffix(Type mapType) {
      var mt = mapType.NormalizeToAncestorType().AsMapType;
      return RegisterMapType(mt.Domain, mt.Range);
    }

    private void EmitMapInstantiations() {
      bool madeProgress = true;
      var emitted = emittedMapInsts;
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, (Type, Type)>>(mapTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          var keyName = TypeName(kv.Value.Item1, null, Token.NoToken, null, false);
          var valName = TypeName(kv.Value.Item2, null, Token.NoToken, null, false);
          var (keyHash, keyEq) = ValueHashEq(kv.Value.Item1);
          var valEq = ValueEq(kv.Value.Item2);
          mapDeclsWr.WriteLine("DAFNY_MAP_DECL({0}, {1}, {2})", kv.Key, keyName, valName);
          mapDefsWr.WriteLine("DAFNY_MAP_DEFINE({0}, {1}, {2}, {3}, {4}, {5})", kv.Key, keyName, valName, keyHash, keyEq, valEq);
        }
      }
    }

    // ----- Set / map / multiset printing ------------------------------------
    // `print s` for a whole set/map/multiset value is compiled to a generated
    // helper that walks the runtime hash table and renders Dafny's textual form:
    //   set<T>       -> "{e0, e1, ...}"          (dafny_print_set_<NAME>)
    //   multiset<T>  -> "multiset{e0, e0, e1}"    (dafny_print_multiset_<NAME>,
    //                                              each element repeated by count)
    //   map<K,V>     -> "map[k0 := v0, k1 := v1]" (dafny_print_map_<K>_<V>)
    // Each element/key/value is printed via its own per-type printer
    // (ElementPrintStmt), so any element type the backend can print is supported;
    // an unprintable element type (e.g. a class reference) rejects cleanly.
    //
    // IMPORTANT — iteration order: the C# backend stores sets/maps/multisets in
    // ImmutableHashSet / ImmutableDictionary, whose enumeration order is an
    // internal hash-bucket order; the C runtime uses a different open-addressing
    // hash table. Neither is sorted, so for a collection with two or more
    // distinct elements the printed *order* generally differs between C# and C
    // (both are nondeterministic w.r.t. Dafny semantics — a set has no order).
    // The regression repros therefore either use singleton/empty collections
    // (whose textual form is order-independent) or compare order-insensitively.
    // Helpers are registered on demand and emitted from EmitFooter, after the
    // corresponding struct definitions.
    private ConcreteSyntaxTree setPrintDeclsWr = null;
    private ConcreteSyntaxTree setPrintDefsWr = null;
    private ConcreteSyntaxTree multisetPrintDeclsWr = null;
    private ConcreteSyntaxTree multisetPrintDefsWr = null;
    private ConcreteSyntaxTree mapPrintDeclsWr = null;
    private ConcreteSyntaxTree mapPrintDefsWr = null;
    private readonly Dictionary<string, Type> setPrintTypes = new();
    private readonly Dictionary<string, Type> multisetPrintTypes = new();
    private readonly Dictionary<string, (Type Key, Type Val)> mapPrintTypes = new();

    // Register a `print` of set<elem>; returns the mangled element suffix (the
    // NAME in dafny_print_set_NAME). Also registers the set struct itself.
    private string RegisterSetPrinter(Type elemType) {
      var suffix = RegisterSetElementType(elemType);
      setPrintTypes.TryAdd(suffix, ApplyActiveSubst(elemType).NormalizeExpand());
      return suffix;
    }

    // Register a `print` of multiset<elem>; returns the mangled element suffix.
    private string RegisterMultisetPrinter(Type elemType) {
      var suffix = RegisterMultisetElementType(elemType);
      multisetPrintTypes.TryAdd(suffix, ApplyActiveSubst(elemType).NormalizeExpand());
      return suffix;
    }

    // Register a `print` of map<key,val>; returns the mangled "<key>_<val>"
    // suffix. Also registers the map struct itself.
    private string RegisterMapPrinter(Type keyType, Type valType) {
      var suffix = RegisterMapType(keyType, valType);
      mapPrintTypes.TryAdd(suffix,
        (ApplyActiveSubst(keyType).NormalizeExpand(), ApplyActiveSubst(valType).NormalizeExpand()));
      return suffix;
    }

    private void EmitSetPrintInstantiations() {
      bool madeProgress = true;
      var emitted = new HashSet<string>();
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, Type>>(setPrintTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          var name = kv.Key;
          var elemPrint = ElementPrintStmt(kv.Value, "_s.slots[_i]");  // may register more
          setPrintDeclsWr.WriteLine("static void dafny_print_set_{0}(DafnySet_{0} _s);", name);
          var wf = setPrintDefsWr.NewBlock(
            string.Format("static void dafny_print_set_{0}(DafnySet_{0} _s)", name));
          wf.WriteLine("putchar('{');");
          wf.WriteLine("bool _first = true;");
          var loop = wf.NewBlock("for (size_t _i = 0; _i < _s.cap; _i++)");
          var body = loop.NewBlock("if (_s.used[_i])");
          body.WriteLine("if (!_first) { printf(\", \"); } _first = false;");
          body.WriteLine(elemPrint);
          wf.WriteLine("putchar('}');");
        }
      }
    }

    private void EmitMultisetPrintInstantiations() {
      bool madeProgress = true;
      var emitted = new HashSet<string>();
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, Type>>(multisetPrintTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          var name = kv.Key;
          var elemPrint = ElementPrintStmt(kv.Value, "_s.slots[_i]");  // may register more
          multisetPrintDeclsWr.WriteLine("static void dafny_print_multiset_{0}(DafnyMultiset_{0} _s);", name);
          var wf = multisetPrintDefsWr.NewBlock(
            string.Format("static void dafny_print_multiset_{0}(DafnyMultiset_{0} _s)", name));
          wf.WriteLine("printf(\"multiset{\");");
          wf.WriteLine("bool _first = true;");
          var loop = wf.NewBlock("for (size_t _i = 0; _i < _s.cap; _i++)");
          var body = loop.NewBlock("if (_s.used[_i])");
          // Print the element once per its multiplicity, like C#'s ToString.
          var rep = body.NewBlock("for (uint64_t _k = 0; _k < _s.counts[_i]; _k++)");
          rep.WriteLine("if (!_first) { printf(\", \"); } _first = false;");
          rep.WriteLine(elemPrint);
          wf.WriteLine("putchar('}');");
        }
      }
    }

    private void EmitMapPrintInstantiations() {
      bool madeProgress = true;
      var emitted = new HashSet<string>();
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, (Type, Type)>>(mapPrintTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          var name = kv.Key;
          var keyPrint = ElementPrintStmt(kv.Value.Item1, "_m.keys[_i]");  // may register more
          var valPrint = ElementPrintStmt(kv.Value.Item2, "_m.vals[_i]");
          mapPrintDeclsWr.WriteLine("static void dafny_print_map_{0}(DafnyMap_{0} _m);", name);
          var wf = mapPrintDefsWr.NewBlock(
            string.Format("static void dafny_print_map_{0}(DafnyMap_{0} _m)", name));
          wf.WriteLine("printf(\"map[\");");
          wf.WriteLine("bool _first = true;");
          var loop = wf.NewBlock("for (size_t _i = 0; _i < _m.cap; _i++)");
          var body = loop.NewBlock("if (_m.used[_i])");
          body.WriteLine("if (!_first) { printf(\", \"); } _first = false;");
          body.WriteLine(keyPrint);
          body.WriteLine("printf(\" := \");");
          body.WriteLine(valPrint);
          wf.WriteLine("putchar(']');");
        }
      }
    }

    // ----- Multiple-return (tuple) struct monomorphisation ------------------
    // A method `returns (a: T1, b: T2, ...)` with more than one non-ghost
    // out-parameter is compiled to return a small C struct with fields
    // ._0, ._1, ... — one distinct struct per used out-type combination, exactly
    // like seq/set/map are monomorphised per element type. The struct name is
    // DafnyTuple_<mangled(T1)>_<mangled(T2)>_... . tupleReturnDeclsWr (in the
    // header) receives the typedef; tupleReturnTypes de-duplicates by the mangled
    // "<T1>_<T2>_..." suffix. Definitions are inline in the typedef (a plain
    // struct needs no helper functions), so only a decls writer is needed.
    private ConcreteSyntaxTree tupleReturnDeclsWr = null;
    private readonly Dictionary<string, List<Type>> tupleReturnTypes = new();

    // Return the element types of a tuple type, dropping GHOST components. The C
    // struct (and its printer) only ever contain the non-ghost fields, so every
    // tuple-shaped path — struct naming, literal construction, default value,
    // element access, printing — must filter ghosts through here first.
    // Example: (int, ghost int, int) -> [int, int] -> DafnyTuple_DafnyInt_DafnyInt.
    private static List<Type> NonGhostTupleArgs(UserDefinedType tupleType) {
      var tupleDecl = (TupleTypeDecl)tupleType.ResolvedClass;
      var result = new List<Type>();
      for (var i = 0; i < tupleType.TypeArgs.Count; i++) {
        if (!tupleDecl.ArgumentGhostness[i]) {
          result.Add(tupleType.TypeArgs[i]);
        }
      }
      return result;
    }

    // Register a multiple-return out-type combination and return the C struct
    // name (e.g. "DafnyTuple_int_bool"). The field types are the resolved
    // out-parameter types.
    private string RegisterTupleReturn(List<Type> outTypes) {
      var resolved = outTypes.ConvertAll(t => ApplyActiveSubst(t).NormalizeExpand());
      var suffix = string.Join("_", resolved.ConvertAll(MangleType));
      var name = "DafnyTuple_" + suffix;
      // Do not register a combination that still mentions a formal type
      // parameter; the concrete version is registered when the enclosing generic
      // member is emitted with a substitution installed.
      if (!resolved.Exists(ContainsTypeParameter)) {
        tupleReturnTypes.TryAdd(suffix, resolved);
      }
      return name;
    }

    private ConcreteSyntaxTree tupleFwdDeclsWr = null;

    private void EmitTupleReturnInstantiations() {
      bool madeProgress = true;
      var emitted = new HashSet<string>();
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, List<Type>>>(tupleReturnTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          // Forward typedef (emitted up front, before collections) so a collection
          // whose value/element is this tuple can name it via a pointer field.
          tupleFwdDeclsWr.WriteLine("typedef struct DafnyTuple_{0} DafnyTuple_{0};", kv.Key);
          // Full definition (named struct, since a forward typedef exists).
          var ws = tupleReturnDeclsWr.NewBlock("struct DafnyTuple_" + kv.Key, ";");
          if (kv.Value.Count == 0) {
            // The unit type () -> an empty struct. Standard C forbids empty
            // structs, so give it one dummy field so it is a valid, sized type.
            ws.WriteLine("char _unit;");
          }
          for (var i = 0; i < kv.Value.Count; i++) {
            var ft = TypeName(kv.Value[i], null, Token.NoToken, null, false);
            ws.WriteLine("{0} _{1};", ft, i);
          }
        }
      }
    }

    // ----- Datatype monomorphisation ---------------------------------------
    // C has no templates, so a datatype instantiation (e.g. Option<bool>) is
    // monomorphised the same way as generic members and sequences: one concrete
    // C tagged-union struct + create/helper functions per (datatype, concrete
    // type-args) pair actually used. dtInstDeclsWr / dtInstDefsWr receive the
    // per-instantiation struct declarations and create-function definitions.
    // datatypeInstantiations de-duplicates by the mangled type name
    // (e.g. "_module_Option_bool") -> (datatype decl, concrete type-args).
    private ConcreteSyntaxTree dtInstDeclsWr = null;
    private ConcreteSyntaxTree dtInstDefsWr = null;
    private readonly Dictionary<string, (DatatypeDecl Dt, List<Type> Args)> datatypeInstantiations = new();
    // Base datatypes for which the shared tag enum has already been emitted.
    private readonly HashSet<string> emittedDatatypeTags = new();

    // ----- Datatype printing ------------------------------------------------
    // `print d` for a datatype value is compiled to a generated
    // dafny_print_dt_<instName>(<instName> _d) helper that renders Dafny's
    // textual form "<DatatypeName>.<Ctor>(f0, f1, ...)" (e.g. "Option.Some(5)",
    // "Color.Green", "List.Cons(1, List.Nil)"). It switches on the tag and prints
    // each field via its own per-type printer (reusing ElementPrintStmt).
    // Registered on demand (a nested/recursive datatype field registers its
    // printer recursively) and emitted from EmitFooter.
    // datatypePrintTypes maps the mangled instance name -> (datatype, actuals).
    private ConcreteSyntaxTree dtPrintDeclsWr = null;
    private ConcreteSyntaxTree dtPrintDefsWr = null;
    private readonly Dictionary<string, (DatatypeDecl Dt, List<Type> Args)> datatypePrintTypes = new();

    // Register a `print` of a datatype value and return the mangled instance
    // name (the NAME in dafny_print_dt_NAME). Also registers the struct.
    private string RegisterDatatypePrinter(UserDefinedType udt) {
      var name = RegisterDatatypeInstance(udt);
      if (udt.ResolvedClass is DatatypeDecl dt && dt is not TupleTypeDecl) {
        var args = udt.TypeArgs.ConvertAll(t => ApplyActiveSubst(t).NormalizeExpand());
        if (!args.Exists(ContainsTypeParameter)) {
          datatypePrintTypes.TryAdd(name, (dt, args));
        }
      }
      return name;
    }

    // Value equality for datatype values. A datatype is a tagged-union struct, so
    // C `==` is invalid; instead we emit a per-instance dafny_dt_eq_<instName>
    // function (bool eq(a, b)) that compares tags and then, per constructor, each
    // non-ghost field by its own value equality (recursing into nested/recursive
    // datatypes). Parallel to the printer infrastructure above.
    private ConcreteSyntaxTree dtEqDeclsWr = null;
    private ConcreteSyntaxTree dtEqDefsWr = null;
    private readonly Dictionary<string, (DatatypeDecl Dt, List<Type> Args)> datatypeEqTypes = new();

    // Register a datatype `==`/`!=` and return the mangled instance name (the NAME
    // in dafny_dt_eq_NAME). Also registers the struct.
    private string RegisterDatatypeEq(UserDefinedType udt) {
      var name = RegisterDatatypeInstance(udt);
      if (udt.ResolvedClass is DatatypeDecl dt && dt is not TupleTypeDecl) {
        var args = udt.TypeArgs.ConvertAll(t => ApplyActiveSubst(t).NormalizeExpand());
        if (!args.Exists(ContainsTypeParameter)) {
          datatypeEqTypes.TryAdd(name, (dt, args));
        }
      }
      return name;
    }

    // If `t` is a (non-tuple) datatype, register its value-equality helper and
    // return the mangled instance name; otherwise null. Used by ==/!= lowering.
    private string DatatypeEqInstance(Type t) {
      var nt = ApplyActiveSubst(t).NormalizeExpand();
      if (nt is UserDefinedType udt && udt.ResolvedClass is DatatypeDecl dt && dt is not TupleTypeDecl) {
        return RegisterDatatypeEq(udt);
      }
      return null;
    }

    // Emit one dafny_dt_eq_<instName> per registered datatype ==, draining the
    // worklist (a field eq may register a nested datatype's eq). Called from
    // EmitFooter alongside the printer emission.
    // Persistent across calls: a collection's _equals (via ValueEq) can register a
    // datatype/tuple eq AFTER this first runs, so EmitFooter calls it again after
    // the collection emitters. Tracking emitted keys in an instance field makes the
    // re-drain idempotent (emit only the newly registered ones, never a duplicate).
    private readonly HashSet<string> emittedDatatypeEq = new();

    private void EmitDatatypeEqInstantiations() {
      bool madeProgress = true;
      var emitted = emittedDatatypeEq;
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, (DatatypeDecl, List<Type>)>>(datatypeEqTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          EmitOneDatatypeEq(kv.Key, kv.Value.Item1, kv.Value.Item2);
        }
      }
    }

    // A boolean C expression testing value-equality of two field accesses of the
    // given Dafny type. Mirrors ValueEq but produces an expression, and supports
    // nested datatypes (via dafny_dt_eq_<inst>) and tuples.
    private string FieldEqExpr(Type fieldType, string a, string b) {
      var t = ApplyActiveSubst(fieldType).NormalizeExpand();
      if (IsGmpInt(t)) { return $"dafny_int_eq({a}, {b})"; }
      if (IsGmpReal(t)) { return $"dafny_real_eq({a}, {b})"; }
      var seq = t.NormalizeToAncestorType().AsSeqType;
      if (seq != null) {
        var suffix = RegisterSeqElementType(seq.Arg);
        return $"dafny_seq_{suffix}_equals({a}, {b})";
      }
      if (t.NormalizeToAncestorType() is SetType st) {
        var suffix = RegisterSetElementType(st.Arg);
        return $"dafny_set_{suffix}_equals({a}, {b})";
      }
      if (t.NormalizeToAncestorType() is MultiSetType mst) {
        var suffix = RegisterMultisetElementType(mst.Arg);
        return $"dafny_multiset_{suffix}_equals({a}, {b})";
      }
      if (t.NormalizeToAncestorType() is MapType mpt) {
        var suffix = RegisterMapType(mpt.Domain, mpt.Range);
        return $"dafny_map_{suffix}_equals({a}, {b})";
      }
      if (t is UserDefinedType udt && udt.ResolvedClass is TupleTypeDecl) {
        var suffix = RegisterTupleEq(NonGhostTupleArgs(udt));
        return $"dafny_tuple_eq_{suffix}({a}, {b})";
      }
      if (t is UserDefinedType dudt && dudt.ResolvedClass is DatatypeDecl dtDecl && dtDecl is not TupleTypeDecl) {
        var inst = RegisterDatatypeEq(dudt);
        return $"dafny_dt_eq_{inst}({a}, {b})";
      }
      // bool, char, native ints, reference pointers: C `==` is correct.
      return $"(({a}) == ({b}))";
    }

    private void EmitOneDatatypeEq(string instName, DatatypeDecl dt, List<Type> actuals) {
      var savedSubst = activeSubst;
      try {
        activeSubst = BuildSubst(dt.TypeArgs, actuals);
        var baseName = DatatypeBaseName(dt);
        var ctors = dt.Ctors.Where(ctor => !ctor.IsGhost).ToList();

        dtEqDeclsWr.WriteLine("static bool dafny_dt_eq_{0}({0} _a, {0} _b);", instName);
        var wf = dtEqDefsWr.NewBlock(
          string.Format("static bool dafny_dt_eq_{0}({0} _a, {0} _b)", instName));

        if (!dt.IsRecordType) {
          // Different active constructor -> not equal.
          wf.WriteLine("if (_a.tag != _b.tag) { return false; }");
        }
        foreach (var ctor in ctors) {
          var accessA = dt.IsRecordType ? "_a." : string.Format("_a.val.{0}.", ctor.GetCompileName(Options));
          var accessB = dt.IsRecordType ? "_b." : string.Format("_b.val.{0}.", ctor.GetCompileName(Options));

          var fieldEqs = new List<string>();
          var fi = 0;
          foreach (var arg in ctor.Formals) {
            if (arg.IsGhost) {
              continue;
            }
            var fn = FormalName(arg, fi);
            var boxed = IsBoxedField(instName, arg.Type);
            var da = (boxed ? "*" : "") + accessA + fn;
            var db = (boxed ? "*" : "") + accessB + fn;
            fieldEqs.Add(FieldEqExpr(arg.Type, da, db));
            fi++;
          }

          ConcreteSyntaxTree body;
          if (dt.IsRecordType) {
            body = wf;
          } else {
            body = wf.NewBlock(string.Format("if (_a.tag == {0}_TAG_{1})",
              baseName, ctor.GetCompileName(Options)));
          }
          if (fieldEqs.Count == 0) {
            body.WriteLine("return true;");   // nullary constructor: tag match suffices
          } else {
            body.WriteLine("return {0};", string.Join(" && ", fieldEqs));
          }
        }
        if (!dt.IsRecordType) {
          wf.WriteLine("return true;");   // unreachable (tags matched above), keeps C happy
        }
      } finally {
        activeSubst = savedSubst;
      }
    }

    // Emit one dafny_print_dt_<instName> helper per registered datatype value
    // print. Called from EmitFooter. Computing a field's print statement may
    // itself register more printers (nested/recursive datatypes, seqs), so the
    // worklist is drained until it stops growing.
    private void EmitDatatypePrintInstantiations() {
      bool madeProgress = true;
      var emitted = new HashSet<string>();
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, (DatatypeDecl, List<Type>)>>(datatypePrintTypes)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          madeProgress = true;
          EmitOneDatatypePrinter(kv.Key, kv.Value.Item1, kv.Value.Item2);
        }
      }
    }

    private void EmitOneDatatypePrinter(string instName, DatatypeDecl dt, List<Type> actuals) {
      var savedSubst = activeSubst;
      try {
        // Install the datatype's substitution so field types (which mention the
        // datatype's formal type parameters) resolve to their concrete actuals,
        // exactly as in EmitOneDatatypeInstance.
        activeSubst = BuildSubst(dt.TypeArgs, actuals);
        var baseName = DatatypeBaseName(dt);
        var ctors = dt.Ctors.Where(ctor => !ctor.IsGhost).ToList();
        // The display prefix is the datatype's Dafny name (e.g. "Option"), which
        // is what the C# backend prints ("Option.Some(5)"), NOT the mangled name.
        var displayName = dt.Name;

        dtPrintDeclsWr.WriteLine("static void dafny_print_dt_{0}({0} _d);", instName);
        var wf = dtPrintDefsWr.NewBlock(
          string.Format("static void dafny_print_dt_{0}({0} _d)", instName));

        foreach (var ctor in ctors) {
          var nonGhost = ctor.Formals.Where(f => !f.IsGhost).ToList();
          // Access to this constructor's fields: a record type is a flat struct
          // (_d.field); a tagged union is _d.val.Ctor.field.
          var accessPrefix = dt.IsRecordType
            ? "_d."
            : string.Format("_d.val.{0}.", ctor.GetCompileName(Options));

          // Compute each field's print statement first (may register more helpers).
          var fieldPrints = new List<string>();
          var fi = 0;
          foreach (var arg in ctor.Formals) {
            if (arg.IsGhost) {
              continue;
            }
            var fn = FormalName(arg, fi);
            // A self-referential (recursive) field is boxed behind a pointer;
            // dereference it to recover the value, like the destructor path.
            var boxed = IsBoxedField(instName, arg.Type);
            var access = (boxed ? "*" : "") + accessPrefix + fn;
            fieldPrints.Add(ElementPrintStmt(arg.Type, access));
            fi++;
          }

          ConcreteSyntaxTree body;
          if (dt.IsRecordType) {
            // Single constructor: no tag to test.
            body = wf;
          } else {
            body = wf.NewBlock(string.Format("if (_d.tag == {0}_TAG_{1})",
              baseName, ctor.GetCompileName(Options)));
          }
          body.WriteLine("printf(\"{0}.{1}\");", displayName, ctor.Name);
          if (fieldPrints.Count > 0) {
            body.WriteLine("putchar('(');");
            for (var i = 0; i < fieldPrints.Count; i++) {
              if (i > 0) {
                body.WriteLine("printf(\", \");");
              }
              body.WriteLine(fieldPrints[i]);
            }
            body.WriteLine("putchar(')');");
          }
          if (!dt.IsRecordType) {
            body.WriteLine("return;");
          }
        }
      } finally {
        activeSubst = savedSubst;
      }
    }

    // Base (type-arg-free) mangled name of a datatype, e.g. "_module_Option".
    // Used for the SHARED tag enum and tag constants, which do not depend on the
    // concrete type arguments (every instantiation has the same constructors in
    // the same order).
    private string DatatypeBaseName(DatatypeDecl dt) {
      var udt = UserDefinedType.FromTopLevelDecl(dt.Origin, dt);
      return IdProtect(FullTypeName(udt));
    }

    // Register a use of a concrete datatype instantiation so its tagged-union
    // struct + create functions get emitted. Returns the mangled instance name
    // (e.g. "_module_Option_bool").
    private string RegisterDatatypeInstance(UserDefinedType udt) {
      var baseName = IdProtect(FullTypeName(udt));
      if (udt.ResolvedClass is not DatatypeDecl dt || dt is TupleTypeDecl) {
        return baseName;
      }
      var args = udt.TypeArgs.ConvertAll(t => ApplyActiveSubst(t).NormalizeExpand());
      var name = baseName + MangleTypeArgs(args);
      // Do not register instantiations that still mention a formal type
      // parameter; those are re-registered concretely when the enclosing generic
      // member is emitted with a substitution installed.
      if (!args.Exists(ContainsTypeParameter)) {
        datatypeInstantiations.TryAdd(name, (dt, args));
      }
      return name;
    }

    // Emit one concrete tagged-union struct + create functions per registered
    // datatype instantiation. Called from EmitFooter, after the whole program
    // (and all concrete generic instantiations) has been walked.
    // Instances already emitted, tracked ACROSS calls: EmitDatatypeInstantiations
    // is invoked more than once (datatype-print registration can add new
    // instances after the first drain), and datatypeInstantiations is not
    // cleared, so a per-call guard would re-emit every struct -> duplicate
    // typedef. This set makes emission idempotent across calls.
    private readonly HashSet<string> emittedDatatypeInstances = new();

    private void EmitDatatypeInstantiations() {
      bool madeProgress = true;
      var emitted = emittedDatatypeInstances;
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, (DatatypeDecl, List<Type>)>>(datatypeInstantiations)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          // Monomorphising some generic-datatype families (e.g. a recursive type
          // instantiated at ever-larger type arguments) would produce infinitely
          // many distinct C structs, making this worklist never terminate. Cap it
          // and reject cleanly rather than hang.
          if (emitted.Count > MonomorphisationInstanceCap) {
            throw new UnsupportedFeatureException(Token.NoToken, Feature.SubsetTypeTests,
              "this program requires unboundedly many monomorphised datatype instantiations, which the C backend cannot compile");
          }
          madeProgress = true;
          EmitOneDatatypeInstance(kv.Key, kv.Value.Item1, kv.Value.Item2);
        }
      }
    }

    // Safety cap on the monomorphisation worklists (datatypes / reference
    // classes). A well-behaved program needs far fewer than this; exceeding it
    // means the instantiation set is growing without bound, so we reject cleanly
    // instead of looping forever.
    private const int MonomorphisationInstanceCap = 5000;

    // Emit the shared tag enum (once per base datatype) plus one concrete
    // tagged-union struct and its create functions for the given instantiation.
    private void EmitOneDatatypeInstance(string instName, DatatypeDecl dt, List<Type> actuals) {
      var savedSubst = activeSubst;
      try {
        // Install the datatype's type-parameter substitution so field types
        // (which may mention the datatype's formal type parameters) render as
        // their concrete actuals.
        activeSubst = BuildSubst(dt.TypeArgs, actuals);

        var baseName = DatatypeBaseName(dt);
        var ctors = dt.Ctors.Where(ctor => !ctor.IsGhost).ToList();

        // C requires a complete type for a BY-VALUE struct member. If a field is
        // another datatype held by value (not boxed behind a pointer), that inner
        // datatype's struct must be declared BEFORE this one. Emit those
        // dependencies first (depth-first), so nested datatypes like
        // `Outer(a: Inner)` don't reference an as-yet-undeclared `Inner`.
        // (Recursive/self-referential fields are boxed as pointers, for which a
        // forward typedef suffices, so they are not a by-value dependency.)
        foreach (var ctor in ctors) {
          foreach (var arg in ctor.Formals) {
            if (arg.IsGhost || IsBoxedField(instName, arg.Type)) {
              continue;
            }
            var ft = ApplyActiveSubst(arg.Type).NormalizeExpand();
            if (ft is UserDefinedType fudt && fudt.ResolvedClass is DatatypeDecl fdt && fdt is not TupleTypeDecl) {
              var depInst = RegisterDatatypeInstance(fudt);
              if (emittedDatatypeInstances.Add(depInst)) {
                var depArgs = fudt.TypeArgs.ConvertAll(t => ApplyActiveSubst(t).NormalizeExpand());
                EmitOneDatatypeInstance(depInst, fdt, depArgs);
              }
            }
          }
        }

        // A recursive instantiation has at least one field boxed as `instName*`,
        // which requires `instName` to be a visible type name INSIDE the struct
        // body. For that we emit a forward `typedef struct instName instName;` and
        // switch to the named `struct instName { ... };` form. Non-recursive
        // datatypes keep the original anonymous `typedef struct { ... } instName;`
        // so their generated C is unchanged.
        var isRecursiveInstance = ctors.Any(ctor =>
          ctor.Formals.Any(f => !f.IsGhost && IsBoxedField(instName, f.Type)));
        if (isRecursiveInstance) {
          dtInstDeclsWr.WriteLine("typedef struct {0} {0};", instName);
        }

        // Shared tag enum (emitted once per base datatype).
        if (emittedDatatypeTags.Add(baseName)) {
          dtInstDeclsWr.WriteLine("typedef enum {{ {0} }} {1}_TAG;",
            Util.Comma(ctors, ctor => baseName + "_TAG_" + ctor.GetCompileName(Options)), baseName);
        }

        if (dt.IsRecordType) {
          // Single constructor: a flat struct whose members are the fields, so
          // the base's `((v).field)` destructor access works directly.
          var ctor = ctors[0];
          var ws = isRecursiveInstance
            ? dtInstDeclsWr.NewBlock("struct " + instName, ";")
            : dtInstDeclsWr.NewBlock("typedef struct", " " + instName + ";");
          var fi = 0;
          var fields = new List<(string Type, string Name, bool Boxed)>();
          foreach (var arg in ctor.Formals) {
            if (!arg.IsGhost) {
              var ft = TypeName(ApplyActiveSubst(arg.Type), null, Token.NoToken);
              var fn = FormalName(arg, fi);
              var boxed = IsBoxedField(instName, arg.Type);
              ws.WriteLine("{0}{1} {2};", ft, boxed ? "*" : "", fn);
              fields.Add((ft, fn, boxed));
              fi++;
            }
          }
          if (fields.Count == 0) {
            ws.WriteLine("char _dummy;");
          }

          var wc = dtInstDefsWr.NewBlock(String.Format("static inline {0} {0}_create_{1}({2})",
            instName, ctor.GetCompileName(Options),
            fields.Count == 0 ? "void" : string.Join(", ", fields.ConvertAll(f => f.Type + " " + f.Name))));
          wc.WriteLine("{0} _r;", instName);
          foreach (var f in fields) {
            if (f.Boxed) {
              wc.WriteLine("_r.{0} = ({1}*)malloc(sizeof({1})); *_r.{0} = {0};", f.Name, f.Type);
            } else {
              wc.WriteLine("_r.{0} = {0};", f.Name);
            }
          }
          if (fields.Count == 0) {
            wc.WriteLine("_r._dummy = 0;");
          }
          wc.WriteLine("return _r;");
        } else {
          // Multiple constructors: a tagged union.
          var ws = isRecursiveInstance
            ? dtInstDeclsWr.NewBlock("struct " + instName, ";")
            : dtInstDeclsWr.NewBlock("typedef struct", " " + instName + ";");
          ws.WriteLine("{0}_TAG tag;", baseName);
          var wu = ws.NewBlock("union", " val;");
          wu.WriteLine("char _dummy;");
          foreach (var ctor in ctors) {
            var nonGhost = ctor.Formals.Where(f => !f.IsGhost).ToList();
            if (nonGhost.Count == 0) {
              continue; // fieldless constructor: no union member needed
            }
            var wm = wu.NewBlock("struct", " " + ctor.GetCompileName(Options) + ";");
            var fi = 0;
            foreach (var arg in ctor.Formals) {
              if (!arg.IsGhost) {
                var fieldTypeName = TypeName(ApplyActiveSubst(arg.Type), null, Token.NoToken);
                // Self-referential field: box behind a pointer so the tagged-union
                // struct does not contain itself by value (infinite size).
                var boxed = IsBoxedField(instName, arg.Type);
                wm.WriteLine("{0}{1} {2};", fieldTypeName, boxed ? "*" : "", FormalName(arg, fi));
                fi++;
              }
            }
          }

          foreach (var ctor in ctors) {
            var i = 0;
            var paramDecls = new List<string>();
            var assigns = new List<string>();
            foreach (var arg in ctor.Formals) {
              if (!arg.IsGhost) {
                var fn = FormalName(arg, i);
                var fieldTypeName = TypeName(ApplyActiveSubst(arg.Type), null, Token.NoToken);
                paramDecls.Add(fieldTypeName + " " + fn);
                if (IsBoxedField(instName, arg.Type)) {
                  // Heap-allocate a copy of the argument (arena/leak model) and
                  // store its address, so the recursive field is a pointer.
                  assigns.Add(String.Format(
                    "_r.val.{0}.{1} = ({2}*)malloc(sizeof({2})); *_r.val.{0}.{1} = {1};",
                    ctor.GetCompileName(Options), fn, fieldTypeName));
                } else {
                  assigns.Add(String.Format("_r.val.{0}.{1} = {1};", ctor.GetCompileName(Options), fn));
                }
                i++;
              }
            }
            var wc = dtInstDefsWr.NewBlock(String.Format("static inline {0} {0}_create_{1}({2})",
              instName, ctor.GetCompileName(Options),
              paramDecls.Count == 0 ? "void" : string.Join(", ", paramDecls)));
            wc.WriteLine("{0} _r;", instName);
            wc.WriteLine("_r.tag = {0}_TAG_{1};", baseName, ctor.GetCompileName(Options));
            foreach (var a in assigns) {
              wc.WriteLine(a);
            }
            wc.WriteLine("return _r;");
          }
        }
      } finally {
        activeSubst = savedSubst;
      }
    }

    // Register a use of a concrete instantiation of a generic reference class so
    // its heap struct + member functions get emitted. Returns the mangled struct
    // name (e.g. "_module_Box_bool"). Instantiations that still mention a formal
    // type parameter are not registered; they are re-registered concretely when
    // the enclosing generic member/class body is emitted with a substitution
    // installed.
    private string RegisterClassInstance(UserDefinedType udt) {
      var cl = udt.ResolvedClass;
      var baseName = RefClassName(cl);
      var args = udt.TypeArgs.ConvertAll(t => ApplyActiveSubst(t).NormalizeExpand());
      var name = baseName + MangleTypeArgs(args);
      if (cl is TopLevelDeclWithMembers twm && !args.Exists(ContainsTypeParameter)) {
        classInstantiations.TryAdd(name, (twm, args));
      }
      return name;
    }

    // Emit one concrete heap struct + one concrete member set per registered
    // generic-reference-class instantiation. Called from EmitFooter, after the
    // whole program has been walked. Re-driving a concrete member body can itself
    // discover new instantiations (e.g. a method returning Box<Box<bool>>), so
    // the worklist is drained until it stops growing.
    private void EmitClassInstantiations() {
      bool madeProgress = true;
      var emitted = new HashSet<string>();
      while (madeProgress) {
        madeProgress = false;
        foreach (var kv in new List<KeyValuePair<string, (TopLevelDeclWithMembers, List<Type>)>>(classInstantiations)) {
          if (!emitted.Add(kv.Key)) {
            continue;
          }
          if (emitted.Count > MonomorphisationInstanceCap) {
            throw new UnsupportedFeatureException(Token.NoToken, Feature.SubsetTypeTests,
              "this program requires unboundedly many monomorphised class instantiations, which the C backend cannot compile");
          }
          madeProgress = true;
          EmitOneClassInstance(kv.Key, kv.Value.Item1, kv.Value.Item2);
        }
      }
    }

    // Emit the concrete struct and re-drive the base member compilation for one
    // instantiation (instName, e.g. "_module_Box_bool") of a generic reference
    // class, with the class's type-parameter substitution installed so field,
    // parameter and return types render concretely and the member/struct names
    // carry the mangled suffix.
    private void EmitOneClassInstance(string instName, TopLevelDeclWithMembers cls, List<Type> actuals) {
      // Find the deferred writers captured for this class on the parametric pass.
      var pending = pendingGenericClasses.Find(pc => pc.Cls == cls);
      if (pending == null) {
        return;
      }

      var savedSubst = activeSubst;
      var savedClassDecl = activeClassDecl;
      var savedClassSuffix = activeClassSuffix;
      try {
        activeSubst = BuildSubst(cls.TypeArgs, actuals);
        activeClassDecl = cls;
        activeClassSuffix = MangleTypeArgs(actuals);

        // 1. Concrete heap struct: typedef struct NAME { <substituted fields> } ...;
        //    Emitted into the same region (classStructsWr) and with the same shape
        //    as a non-generic reference class (see CreateClass).
        this.classStructsWr.WriteLine("typedef struct {0} {0};", instName);
        var structBody = this.classStructsWr.NewBlock("struct " + instName, ";");
        var instanceFields = cls.Members
          .Where(m => m is Field f && !f.IsStatic && !f.IsGhost && m is not ConstantField)
          .Cast<Field>().ToList();
        if (instanceFields.Count == 0) {
          structBody.WriteLine("char _dummy;");
        }
        foreach (var f in instanceFields) {
          structBody.WriteLine("{0} {1};", TypeName(ApplyActiveSubst(f.Type), null, f.Origin), IdName(f));
        }

        // 2. Concrete member functions: re-drive the base's member compilation
        //    into the captured real writers with the substitution installed. The
        //    base calls back into CreateMethod/CreateFunction/DeclareField, which
        //    now render concretely (activeSubst != null) and with the mangled
        //    class name (RefClassName consults activeClassDecl/activeClassSuffix).
        //    The concrete struct + its fields were already emitted above, so the
        //    base's DeclareField calls are routed to a scratch writer (fieldWriter)
        //    and discarded, avoiding stray file-scope `type value;` declarations.
        var fieldScratch = new ConcreteSyntaxTree();
        var cw = new ClassWriter(clName(cls), this, pending.DeclWriter, pending.DefWriter,
          fieldScratch, pending.DefWriter);
        CompileClassMembersReflect(cls, cw);
      } finally {
        activeSubst = savedSubst;
        activeClassDecl = savedClassDecl;
        activeClassSuffix = savedClassSuffix;
      }
    }

    // Invoke the base class's (non-public) CompileClassMembers to re-drive member
    // emission for a class instantiation, reusing all base logic (constructor,
    // method, function, getter/setter dispatch) rather than re-implementing it.
    private void CompileClassMembersReflect(TopLevelDeclWithMembers cls, IClassWriter cw) {
      var mi = typeof(SinglePassCodeGenerator).GetMethod("CompileClassMembers",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Public);
      Contract.Assert(mi != null, "base CompileClassMembers not found");
      mi.Invoke(this, new object[] { program_, cls, cw });
    }

    const string DafnySetClass = "DafnySet";
    const string DafnyMultiSetClass = "DafnyMultiset";
    const string DafnySeqClass = "DafnySequence";
    const string DafnyMapClass = "DafnyMap";

    // C has no scopes: qualified names are flattened with "_" instead of "::".
    // (C++ used "::" here.) This covers the base class's use of these separators;
    // the hard-coded "Mod::Cl::member" name sites below are flattened too. It is
    // deliberately NOT applied to C++ template constructs (std::, get_default<>::
    // call(), Dt::create_), which are handled later by monomorphisation.
    public override string ModuleSeparator => "_";
    protected override string StaticClassAccessor => "_";
    protected override string InstanceClassAccessor => "->";

    // C has no methods: every member is a flat free function. An instance member
    // of a reference class is compiled as a static function whose FIRST parameter
    // is an explicit `NAME* this` (the "custom receiver" model, as used by e.g.
    // the Go/Rust backends). Returning true here makes the base emit the call as
    //   Companion StaticClassAccessor Name (receiver, args...)
    // which — with EmitTypeName_Companion => "NAME", StaticClassAccessor => "_"
    // and CompanionMemberIdName => "Name" — renders as NAME_Name(receiver, args).
    // Our CreateMethod/CreateFunction emit the matching `NAME* this` parameter.
    public override bool NeedsCustomReceiverNotTrait(MemberDecl member) {
      if (IsInstanceRefMember(member)) {
        return true;
      }
      return base.NeedsCustomReceiverNotTrait(member);
    }

    // Join name parts into a flattened C identifier, e.g. Scope("M","C","f")
    // -> "M_C_f". Mirrors what C++ wrote with "::".
    private string Scope(params string[] parts) => string.Join("_", parts);

    // Monomorphisation: C has no templates, so a generic definition is emitted
    // once per concrete instantiation, with the concrete type arguments mangled
    // into the name. The SAME suffix is appended at the definition and at every
    // call site (via EmitNameAndActualTypeArgs), which is what ties a call to
    // its concrete copy. Produce a deterministic, C-identifier-safe suffix from
    // the actual types, e.g. [bool] -> "_bool", [int64] -> "_int64".
    private string MangleType(Type t) {
      // Resolve through any installed substitution and fully normalise, so the
      // mangled suffix is identical whether it is built at a call site or when
      // registering an instantiation.
      var normalized = ApplyActiveSubst(t).NormalizeExpand();
      // The Dafny char type's C type name is "dafny_char" (a 32-bit code point),
      // but its monomorphisation SUFFIX must stay "char": the char sequence
      // (DafnySequence_char) and its helpers (dafny_seq_char_*, dafny_print_seq_char)
      // are PRE-INSTANTIATED in the runtime header under the "char" suffix, and
      // EmitSeqInstantiations skips re-emitting that suffix. Mangling char to
      // "dafny_char" instead would fork a second, incompatible instantiation.
      if (normalized is CharType) {
        return "char";
      }
      var name = TypeName(normalized, null, Token.NoToken);
      var sb = new System.Text.StringBuilder();
      foreach (var ch in name) {
        sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
      }
      return sb.ToString();
    }

    private string MangleTypeArgs(List<Type> typeArgs) {
      if (typeArgs == null || typeArgs.Count == 0) {
        return "";
      }
      return "_" + string.Join("_", typeArgs.ConvertAll(MangleType));
    }

    // Override the base name+type-args emission: instead of `name<T0, T1>`
    // (C++), emit the mangled flat name `name_T0_T1`. For non-generic calls the
    // suffix is empty, so output is unchanged.
    protected override void EmitNameAndActualTypeArgs(string protectedName, List<Type> typeArgs, IOrigin tok,
      Expression replacementReceiver, bool receiverAsArgument, ConcreteSyntaxTree wr) {
      wr.Write(protectedName + MangleTypeArgs(typeArgs));
    }

    protected override void EmitHeader(Program program, ConcreteSyntaxTree wr) {
      // This seems to be a good place to check for unsupported options
      if (UnicodeCharEnabled) {
        throw new UnsupportedFeatureException(program.GetStartOfFirstFileToken(), Feature.UnicodeChars);
      }
      // Captured so EmitFooter can re-drive base member compilation per generic
      // reference-class instantiation.
      this.program_ = program;

      wr.WriteLine("// Dafny program {0} compiled into C", program.Name);
      wr.WriteLine("#include \"DafnyRuntime.h\"");
      foreach (var header in this.headers) {
        wr.WriteLine("#include \"{0}\"", Path.GetFileName(header));
      }

      // C has no namespaces / no std::literals, so the C++ backend's
      // `using namespace std::literals;` line is omitted here.

      var filenameNoExtension = program.Name.Substring(0, program.Name.Length - 4);
      var headerFileName = $"{filenameNoExtension}.h";
      wr.WriteLine("#include \"{0}\"", Path.GetFileName(headerFileName));

      var headerFileWr = wr.NewFile(headerFileName);
      headerFileWr.WriteLine("// Dafny program {0} compiled into a C header file", program.Name);
      headerFileWr.WriteLine("#pragma once");
      headerFileWr.WriteLine("#include \"DafnyRuntime.h\"");

      // FORWARD typedefs for tuple structs, emitted FIRST — before any collection
      // struct. A collection stores its value/element by POINTER (e.g. map's
      // `VAL* vals`), so a `map<K, (a,b)>` only needs `DafnyTuple_...` forward-
      // declared here; its full `struct DafnyTuple_... { ... };` definition (which
      // may in turn embed a collection struct by value) comes later. This breaks
      // the mutual dependency between "tuple containing a collection" (needs the
      // collection struct) and "collection of tuples" (needs the tuple name).
      this.tupleFwdDeclsWr = headerFileWr.Fork();

      // Monomorphised sequence struct/helper declarations go here, before any
      // module declarations that reference DafnySequence_<elem> types.
      this.seqDeclsWr = headerFileWr.Fork();
      // Matching helper definitions go into the source file, before the modules.
      this.seqDefsWr = wr.Fork();

      // Sequence-print helper prototypes/definitions. Placed after the seq
      // decls/defs so they can reference the DafnySequence_<NAME> struct types
      // and the underlying element printers, but before module code that calls
      // them. (Kept as separate forks so a nested seq<seq<...>> printer, which is
      // registered while emitting the outer printer, still lands here.)
      this.seqPrintDeclsWr = headerFileWr.Fork();
      this.seqPrintDefsWr = wr.Fork();

      // Monomorphised datatype tagged-union struct declarations go into the
      // header after the sequence decls (a datatype field may be a seq), before
      // any module code that uses them. The matching create-function definitions
      // (which need the struct declarations visible via the header include) go
      // into the source file, before the modules.
      this.dtInstDeclsWr = headerFileWr.Fork();
      this.dtInstDefsWr = wr.Fork();

      // Monomorphised set<T> and map<K,V> struct/helper declarations. Placed
      // after the datatype decls so a set/map element/key/value may be a
      // datatype (or seq). Definitions go into the source file.
      this.setDeclsWr = headerFileWr.Fork();
      this.setDefsWr = wr.Fork();
      this.multisetDeclsWr = headerFileWr.Fork();
      this.multisetDefsWr = wr.Fork();
      this.mapDeclsWr = headerFileWr.Fork();
      this.mapDefsWr = wr.Fork();

      // Set/map/multiset whole-value print helpers. Placed AFTER the set/map/
      // multiset struct decls/defs so they can reference the DafnySet_/DafnyMap_/
      // DafnyMultiset_ struct types and the underlying element printers, but
      // before module code that calls them.
      this.setPrintDeclsWr = headerFileWr.Fork();
      this.setPrintDefsWr = wr.Fork();
      this.multisetPrintDeclsWr = headerFileWr.Fork();
      this.multisetPrintDefsWr = wr.Fork();
      this.mapPrintDeclsWr = headerFileWr.Fork();
      this.mapPrintDefsWr = wr.Fork();

      // Monomorphised 1-D array<T> struct/allocator declarations. Placed after
      // the seq/set/map decls so an array element may itself be a
      // seq/set/map/datatype. Definitions (which compute the per-type element
      // default) go into the source file, after those helpers so the default
      // expression can reference them.
      this.arrayDeclsWr = headerFileWr.Fork();
      this.arrayDefsWr = wr.Fork();

      // Reference-class struct definitions render here, after datatype/seq structs
      // (a field may be a datatype or seq) but before any module or method
      // declaration that refers to a class pointer type NAME*.
      this.classStructsWr = headerFileWr.Fork();

      // Multiple-return (tuple) struct typedefs render here, after all element
      // structs (a field may be a datatype/seq/set/map/class) but before any
      // method declaration whose return type is DafnyTuple_...  .
      this.tupleReturnDeclsWr = headerFileWr.Fork();

      // Tuple-print helper prototypes/definitions. Placed after the tuple struct
      // typedefs so they can reference DafnyTuple_<NAME> types and the element
      // printers, but before module code that calls them.
      this.tuplePrintDeclsWr = headerFileWr.Fork();
      this.tuplePrintDefsWr = wr.Fork();
      this.tupleEqDeclsWr = headerFileWr.Fork();
      this.tupleEqDefsWr = wr.Fork();

      // Datatype-print helper prototypes/definitions. Placed after the datatype
      // structs (and the seq/tuple printers a datatype field may recurse into),
      // but before module code that calls them. All prototypes are emitted ahead
      // of all definitions (separate decl/def forks), so a recursive datatype
      // (List.Cons calling dafny_print_dt_List on its tail) resolves correctly.
      this.dtPrintDeclsWr = headerFileWr.Fork();
      this.dtPrintDefsWr = wr.Fork();
      this.dtEqDeclsWr = headerFileWr.Fork();
      this.dtEqDefsWr = wr.Fork();

      this.modDeclsWr = headerFileWr.Fork();
      this.dtDeclsWr = headerFileWr.Fork();
      this.classDeclsWr = headerFileWr.Fork();

      if (Options.IncludeRuntime) {
        EmitCRuntimeSource(wr);
      }

    }

    // Emit the embedded C runtime (DafnyRuntimeC/DafnyRuntime.h) as separate
    // file(s). We cannot reuse the base EmitRuntimeSource("DafnyRuntimeC", ...):
    // it matches manifest resources by StartsWith("DafnyPipeline.DafnyRuntimeC"),
    // and "DafnyPipeline.DafnyRuntimeCpp.DafnyRuntime.h" ALSO starts with that
    // prefix. Both resources filter down to the same output name
    // "DafnyRuntime.h", so the build/run file collection
    // (SynchronousCliCompilation) throws "An item with the same key has already
    // been added. Key: DafnyRuntime.h". Matching on the exact prefix
    // "DafnyPipeline.DafnyRuntimeC." (trailing dot) excludes the Cpp runtime and
    // guarantees exactly one DafnyRuntime.h is emitted.
    private void EmitCRuntimeSource(ConcreteSyntaxTree wr) {
      var assembly = System.Reflection.Assembly.Load("DafnyPipeline");
      var header = "DafnyPipeline.DafnyRuntimeC.";
      foreach (var file in assembly.GetManifestResourceNames().Where(f => f.StartsWith(header))) {
        var parts = file.Split('.');
        var realName = FilterRuntimeSourcePathEmission(string.Join('/', parts.SkipLast(1).Skip(2)) + "." + parts.Last());
        var stream = assembly.GetManifestResourceStream(file);
        if (stream is null) {
          throw new Exception($"Cannot find embedded resource: {file}");
        }
        var rd = new StreamReader(stream);
        WriteFromStream(rd, wr.NewFile(realName).Append(new Verbatim()));
      }
    }
    protected override void EmitFooter(Program program, ConcreteSyntaxTree wr) {
      // C++ emitted per-datatype and per-class `get_default<>` template
      // specializations here. C has no templates and (in the non-generic subset)
      // no datatypes, and default values come from get_Default()/literals.
      //
      // Monomorphisation: by now the whole program has been walked, so every
      // concrete instantiation of every generic member has been registered at its
      // call sites. Emit one concrete C copy per instantiation (into the writers
      // captured on the first, deferred pass). The ConcreteSyntaxTree writers are
      // rendered only after this returns, so appending here places the concrete
      // definitions correctly inside their module/header blocks.
      EmitConcreteInstantiations();

      // Emit one concrete heap struct + member set per generic reference-class
      // instantiation. Re-driving a concrete member body may register new generic
      // members, datatypes, seqs, sets or maps, so this runs before those
      // emitters (and its own worklist drains internally).
      EmitClassInstantiations();

      // A class member body may have registered new generic member instantiations
      // (e.g. Box<bool>.Get calling a generic function), so drain those again.
      EmitConcreteInstantiations();

      // Emit monomorphised 1-D array<T> structs + allocators. Computing an
      // element's default value may register a new seq/set/map/datatype element
      // type, so this runs before those collection emitters below so their
      // worklists still drain any newly-introduced types.
      EmitArrayInstantiations();

      // Emit one concrete tagged-union struct + create functions per datatype
      // instantiation discovered (via TypeName / DeclareDatatype). This may
      // itself introduce new seq element types (a datatype field is a seq), so
      // it must run before EmitSeqInstantiations.
      EmitDatatypeInstantiations();

      // Emit datatype-print helpers. Rendering a field may register new datatype
      // instantiations (a recursive tail), nested tuple/seq printers and their
      // element types, all of which the emitters below (and a second
      // EmitDatatypeInstantiations pass) still drain. Runs before the tuple/seq
      // print emitters so any printers it registers are picked up there.
      EmitDatatypePrintInstantiations();
      // Datatype value-equality helpers (dafny_dt_eq_<inst>). A field eq may
      // register nested datatype/collection helpers, so this drains its worklist.
      EmitDatatypeEqInstantiations();
      // A datatype printer may have registered a fresh datatype instantiation
      // (e.g. Option<int> nested in a printed List<Option<int>>); drain those.
      EmitDatatypeInstantiations();

      // Emit tuple-print helpers first: rendering a tuple field may register new
      // (nested) tuple structs, seq/set/map element types and their printers,
      // which the emitters below still need to drain. The helper bodies land in
      // tuplePrintDeclsWr/DefsWr, which are forked AFTER the tuple struct typedefs
      // in the header, so source order stays valid.
      EmitTuplePrintInstantiations();
      EmitTupleEqInstantiations();

      // Emit monomorphised multiple-return (tuple) structs. A field may be a
      // seq/set/map/datatype, so this runs before those collection emitters so
      // any newly registered element types are still drained below.
      EmitTupleReturnInstantiations();

      // Emit monomorphised set and map structs + helpers. These run before
      // EmitSeqInstantiations because a set element or map key/value type may be
      // a seq, adding new seq element types on the way.
      EmitSetInstantiations();
      EmitMultisetInstantiations();
      EmitMapInstantiations();

      // Emit set/map/multiset whole-value print helpers. Rendering an element/
      // key/value may register a new seq/tuple/datatype printer AND a new
      // set/map/multiset/seq element type (e.g. printing a set<seq<int>> or a
      // set<set<int>>), so this runs before the seq-print/seq emitters below, and
      // we re-drain the set/map/multiset struct emitters afterwards to pick up any
      // freshly introduced collection element types. Their own worklists (and
      // these emitters') drain internally.
      EmitSetPrintInstantiations();
      EmitMultisetPrintInstantiations();
      EmitMapPrintInstantiations();
      EmitSetInstantiations();
      EmitMultisetInstantiations();
      EmitMapInstantiations();

      // Emit the sequence-print helpers. Registering a nested seq printer can
      // register new seq element types, so this runs before EmitSeqInstantiations
      // (whose worklist then drains any newly introduced element types).
      EmitSeqPrintInstantiations();

      // Now that every concrete generic body has been emitted (which may itself
      // introduce new seq element types), emit the monomorphised sequence
      // struct + helper declarations/definitions, one per element type used.
      EmitSeqInstantiations();

      // Re-drain the datatype/tuple value-equality emitters: a collection's
      // _equals (via ValueEq) or a nested field eq may have registered a fresh
      // datatype/tuple eq AFTER the first pass above. These emitters are
      // idempotent (persistent emitted-key sets), so this only emits the newly
      // introduced helpers — whose prototypes (in the header) are what the
      // collection _equals bodies reference.
      EmitDatatypeEqInstantiations();
      EmitTupleEqInstantiations();
    }

    public override void EmitCallToMain(Method mainMethod, string baseName, ConcreteSyntaxTree wr) {
      // Plain C entry point. C++ used try/catch(DafnyHaltException)/std::cout; C
      // has no exceptions, so just call Main and return 0.
      var mainName = Scope(mainMethod.EnclosingClass.EnclosingModuleDefinition.GetCompileName(Options),
        clName(mainMethod.EnclosingClass), mainMethod.Name);

      // Does Main declare a real command-line args parameter? Dafny synthesises
      // an unused `__noArgsParameter` in-param when Main is declared without one;
      // in that case Main's signature keeps the opaque `DafnySequence` placeholder
      // (see DeclareFormalString) and dafny_get_args' placeholder value matches.
      var argsFormal = mainMethod.Ins.Find(f => !f.IsGhost && FormalName(f, 0) != "__noArgsParameter");

      var w = wr.NewBlock("int main(int argc, char *argv[])");
      if (argsFormal == null) {
        // No args parameter: pass the opaque placeholder (runtime dafny_get_args).
        w.WriteLine("{0}(dafny_get_args(argc, argv));", mainName);
        w.WriteLine("return 0;");
        return;
      }

      // Main reads args: its parameter is the real monomorphised seq<seq<char>>.
      // Build that value from (argc, argv) so the call type-checks and a program
      // that inspects args sees the actual command-line arguments. Register both
      // the inner seq<char> (a string) and the outer seq<seq<char>> so their
      // structs + helpers are emitted by EmitSeqInstantiations.
      var argType = argsFormal.Type.NormalizeToAncestorType();  // seq<seq<char>>
      var elemType = argType.AsSeqType.Arg;                     // seq<char> (string)
      // The element-type suffix registered for the OUTER seq's create helper is
      // the mangled name of elemType (e.g. "DafnySequence_char"); that same
      // mangled name is also the C struct name of an elemType value. Registering
      // it here schedules DafnySequence_char's struct + helpers. Registering the
      // inner "char" element gives dafny_seq_char_create for each argv[i].
      var innerSuffix = RegisterSeqElementType(elemType.NormalizeToAncestorType().AsSeqType.Arg);  // "char"
      var elemSuffix = RegisterSeqElementType(elemType);        // "DafnySequence_char"
      RegisterSeqElementType(argType);                          // outer struct + helpers
      var elemStruct = elemSuffix;                              // DafnySequence_char
      var outerStruct = MangleType(argType);                    // DafnySequence_DafnySequence_char

      // Convert each C argv[i] into a Dafny string (DafnySequence_<inner>), collect
      // them into a scratch array, then wrap into the outer sequence value.
      w.WriteLine("{0}* _args = (argc == 0) ? NULL : ({0}*)malloc((size_t)argc * sizeof({0}));", elemStruct);
      var loop = w.NewBlock("for (int _i = 0; _i < argc; _i++)");
      // A Dafny char is dafny_char (a 32-bit code point), but argv[_i] is a C
      // char* (UTF-8 bytes). Widen each byte to a dafny_char so the element
      // types match (each byte becomes one code point; Main's args are unused in
      // the supported programs, so a full UTF-8 decode is unnecessary here).
      loop.WriteLine("size_t _n = strlen(argv[_i]);");
      loop.WriteLine("dafny_char* _cp = _n == 0 ? NULL : (dafny_char*)malloc(_n * sizeof(dafny_char));");
      loop.WriteLine("for (size_t _j = 0; _j < _n; _j++) { _cp[_j] = (dafny_char)(unsigned char)argv[_i][_j]; }");
      loop.WriteLine("_args[_i] = dafny_seq_{0}_create(_n, _cp);", innerSuffix);
      loop.WriteLine("free(_cp);");
      w.WriteLine("{0} _dafny_args = dafny_seq_{1}_create((size_t)argc, _args);", outerStruct, elemSuffix);
      w.WriteLine("free(_args);");
      w.WriteLine("{0}(_dafny_args);", mainName);
      w.WriteLine("return 0;");
    }

    protected override ConcreteSyntaxTree CreateStaticMain(IClassWriter cw, string argsParameterName) {
      var wr = (cw as ClassWriter).MethodWriter;
      return wr.NewBlock($"int main(DafnySequence {argsParameterName})");
    }

    protected override ConcreteSyntaxTree CreateModule(ModuleDefinition module, string moduleName, bool isDefault,
      ModuleDefinition externModule,
      string libraryName /*?*/, Attributes moduleAttributes, ConcreteSyntaxTree wr) {
      // C has no namespaces: emit each module's declarations flat (no enclosing
      // `namespace X { ... }` block), keeping the per-module writer-fork structure
      // the other methods rely on. (C++ wrapped these in `namespace X { ... }`.)
      var name = IdProtect(moduleName);
      var s = $"// module {name}";
      this.modDeclWr = this.modDeclsWr.NewBlock(s, "// end of module " + name + " declarations",
        BlockStyle.Newline, BlockStyle.Newline);
      this.classDeclWr = this.classDeclsWr.NewBlock(s, "// end of module " + name + " class declarations",
        BlockStyle.Newline, BlockStyle.Newline);
      return wr.NewBlock(s, "// end of module " + name,
        BlockStyle.Newline, BlockStyle.Newline);
    }

    private string TypeParameters(List<TypeParameter> targs) {
      Contract.Requires(Cce.NonNullElements(targs));
      Contract.Ensures(Contract.Result<string>() != null);
      if (targs != null) {
        return Util.Comma(targs, tp => "typename " + IdName(tp));
      } else {
        return "";
      }
    }

    private string DeclareTemplate(List<TypeParameter> typeArgs) {
      var targs = "";
      if (typeArgs != null && typeArgs.Count > 0) {
        targs = String.Format("template <{0}>", TypeParameters(typeArgs));
      }
      return targs;
    }

    private string DeclareTemplate(List<Type> typeArgs) {
      var targs = "";
      if (typeArgs != null && typeArgs.Count > 0) {
        targs = String.Format("template <{0}>", Util.Comma(typeArgs, t => "typename " + TypeName(t, null, null)));
      }
      return targs;
    }

    private string InstantiateTemplate(List<TypeParameter> typeArgs) {
      if (typeArgs != null) {
        var targs = "";
        if (typeArgs.Count > 0) {
          targs = String.Format("<{0}>", Util.Comma(typeArgs, ta => ta.GetCompileName(Options)));
        }
        return targs;
      } else {
        return "";
      }
    }

    private string InstantiateTemplate(List<Type> typeArgs) {
      if (typeArgs != null) {
        var targs = "";
        if (typeArgs.Count > 0) {
          targs = String.Format("<{0}>", Util.Comma(typeArgs, ta => TypeName(ta, null, Token.NoToken)));
        }

        return targs;
      } else {
        return "";
      }
    }

    protected override string GetHelperModuleName() => "_dafny";

    private string clName(TopLevelDecl cl) {
      var className = IdName(cl);
      if (cl is ClassDecl || cl is DefaultClassDecl) {
        return className;
      }
      return "class_" + className;
    }

    // A "reference class" is a user-declared class (not the synthetic __default
    // module class that just holds top-level statics). These are compiled to a
    // heap-allocated C struct (typedef struct NAME { fields } NAME;) plus flat
    // free functions; each instance (non-static) member takes an explicit leading
    // `NAME* this` parameter and each call passes the receiver as that argument.
    private static bool IsReferenceClass(TopLevelDecl cl) {
      return cl is ClassDecl && cl is not DefaultClassDecl && cl is not TraitDecl && cl is not ArrayClassDecl;
    }

    // The flat, fully qualified C struct/type name for a reference class, e.g.
    // _module_Box. This is the spelling used for the struct typedef and as the
    // pointer element type NAME*.
    private string RefClassName(TopLevelDecl cl) {
      var baseName = Scope(IdProtect(cl.EnclosingModuleDefinition.GetCompileName(Options)), clName(cl));
      // While emitting a concrete instantiation of a generic reference class, the
      // struct/type/method names carry the mangled type-argument suffix (e.g.
      // _module_Box_bool) so the definition names match the mangled call sites.
      if (activeClassDecl == cl && activeClassSuffix != null) {
        return baseName + activeClassSuffix;
      }
      return baseName;
    }

    // True when member `m` is an instance (non-static) method/constructor/function
    // of a reference class, i.e. a callable that needs an explicit leading
    // `NAME* this` parameter and the custom-receiver call form. Deliberately
    // EXCLUDES fields: an instance field is read/written as `this->field`, not as
    // a companion call.
    private bool IsInstanceRefMember(MemberDecl m) {
      return !m.IsStatic && IsReferenceClass(m.EnclosingClass)
        && (m is MethodOrConstructor || m is Function);
    }

    protected override IClassWriter CreateClass(string moduleName, bool isExtern, string/*?*/ fullPrintName, List<TypeParameter>/*?*/ typeParameters, TopLevelDecl cls, List<Type>/*?*/ superClasses, IOrigin tok, ConcreteSyntaxTree wr) {
      var className = clName(cls);
      if (isExtern) {
        throw new UnsupportedFeatureException(tok, Feature.ExternalClasses, String.Format("extern in class {0}", className));
      }
      if (superClasses != null && superClasses.Any(trait => !trait.IsObject)) {
        throw new UnsupportedFeatureException(tok, Feature.Traits);
      }

      // A GENERIC reference class has no C-template analog, so it is monomorphised
      // like a generic datatype: nothing is emitted on this parametric pass (whose
      // member bodies would contain the invalid formal type parameter `T`).
      // Instead the class + the real target writers are recorded; EmitFooter emits
      // one concrete struct + member set per instantiation discovered at use sites.
      // Only defer classes for which C code is actually generated (i.e. reference
      // classes); a generic default/trait class never reaches here in this subset.
      if (cls is TopLevelDeclWithMembers twmGen && IsReferenceClass(cls) && cls.TypeArgs.Count != 0) {
        var declRegion = this.classDeclWr.NewBlock("// class " + className, "// end of class " + className,
          BlockStyle.Newline, BlockStyle.Newline);
        pendingGenericClasses.Add(new PendingClass {
          Cls = twmGen, DeclWriter = declRegion, DefWriter = wr
        });
        // Hand the base scratch writers so its parametric member emission is
        // discarded. classStructsWr is NOT touched: the concrete structs are
        // emitted per instantiation in EmitClassInstantiations.
        var scratch = new ConcreteSyntaxTree();
        return new ClassWriter(className, this, scratch, scratch, scratch, scratch);
      }

      var classDefWriter = this.classDeclWr;

      // C has no classes. A class becomes just a name prefix on flat functions.
      // Instead of `class X { public: ... };`, emit the members' declarations
      // (function prototypes, produced flat by CreateMethod/CreateFunction) at
      // header top level.
      var methodDeclWriter = classDefWriter.NewBlock("// class " + className, "// end of class " + className,
        BlockStyle.Newline, BlockStyle.Newline);
      var methodDefWriter = wr;

      var fieldWriter = methodDeclWriter;

      // A user-declared (reference) class ALSO gets a heap struct holding its
      // instance fields:  typedef struct NAME { <fields> } NAME;  emitted into a
      // dedicated header region ahead of any method prototype that mentions NAME*.
      // Instance fields are routed (via DeclareField) into this struct body; static
      // consts stay flat. Objects are allocated with malloc (arena/leak model).
      if (IsReferenceClass(cls)) {
        var refName = RefClassName(cls);
        // Forward-declare the typedef first so a field of the class's own type
        // (a self-referential `NAME* next`) can name NAME inside the struct body.
        this.classStructsWr.WriteLine("typedef struct {0} {0};", refName);
        // struct NAME { <fields> };  (real { } braces, like the datatype structs).
        var structBody = this.classStructsWr.NewBlock("struct " + refName, ";");
        // C forbids an empty struct: if the class has no compiled instance fields,
        // emit a placeholder member so sizeof(NAME)/malloc still work.
        var hasInstanceField = cls is TopLevelDeclWithMembers twm &&
          twm.Members.Any(m => m is Field f && !f.IsStatic && !f.IsGhost && m is not ConstantField);
        if (!hasInstanceField) {
          structBody.WriteLine("char _dummy;");
        }
        fieldWriter = structBody;
      }

      return new ClassWriter(className, this, methodDeclWriter, methodDefWriter, fieldWriter, wr);
    }

    protected override bool SupportsProperties { get => false; }

    protected override IClassWriter CreateTrait(string name, bool isExtern, List<TypeParameter> typeParameters /*?*/,
      TraitDecl trait, List<Type> superClasses /*?*/, IOrigin tok, ConcreteSyntaxTree wr) {
      throw new UnsupportedFeatureException(tok, Feature.Traits);
    }

    protected override ConcreteSyntaxTree CreateIterator(IteratorDecl iter, ConcreteSyntaxTree wr) {
      throw new UnsupportedFeatureException(iter.Origin, Feature.Iterators);
    }

    protected bool IsRecursiveConstructor(DatatypeDecl dt, DatatypeCtor ctor) {
      foreach (var dtor in ctor.Destructors) {
        if (dtor.Type is UserDefinedType t) {
          if (t.ResolvedClass == dt) {
            return true;
          }
        }
      }
      return false;
    }

    protected bool IsRecursiveDatatype(DatatypeDecl dt) {
      foreach (var ctor in dt.Ctors) {
        if (IsRecursiveConstructor(dt, ctor)) {
          return true;
        }
      }
      return false;
    }

    // True if `dt` participates in recursion that passes through a DIFFERENT
    // datatype (mutual recursion), which the direct-self-reference boxing scheme
    // does not cover. We look for any datatype OTHER than `dt` reachable from `dt`
    // (through by-value datatype fields) that can in turn reach back to `dt`.
    private bool HasMutualRecursion(DatatypeDecl dt) {
      // Collect the set of datatypes reachable from a given start datatype through
      // datatype-typed constructor fields.
      HashSet<DatatypeDecl> Reachable(DatatypeDecl start) {
        var seen = new HashSet<DatatypeDecl>();
        var stack = new Stack<DatatypeDecl>();
        stack.Push(start);
        while (stack.Count > 0) {
          var cur = stack.Pop();
          foreach (var ctor in cur.Ctors) {
            foreach (var formal in ctor.Formals) {
              if (formal.IsGhost) {
                continue;
              }
              if (formal.Type.NormalizeExpand() is UserDefinedType ut &&
                  ut.ResolvedClass is DatatypeDecl fdt && fdt is not TupleTypeDecl) {
                if (seen.Add(fdt)) {
                  stack.Push(fdt);
                }
              }
            }
          }
        }
        return seen;
      }
      var reach = Reachable(dt);
      foreach (var other in reach) {
        if (other != dt && Reachable(other).Contains(dt)) {
          return true;
        }
      }
      return false;
    }

    // A constructor field is BOXED (emitted as a NAME* pointer, heap-allocated in
    // the create-function) when its concrete resolved type is exactly the same
    // datatype instantiation currently being emitted, i.e. a self-referential
    // occurrence that would otherwise make the tagged-union struct contain itself
    // by value (infinite size). Detection is by comparing the field type's mangled
    // instance name against the enclosing instance name, so it works uniformly for
    // non-generic (List) and monomorphised generic (Tree<bool>) datatypes.
    // NON-recursive fields (a bool, or a DIFFERENT datatype) are not boxed.
    private bool IsBoxedField(string enclosingInstName, Type fieldType) {
      var t = ApplyActiveSubst(fieldType).NormalizeExpand();
      if (t is UserDefinedType udt && udt.IsDatatype && udt.ResolvedClass is not TupleTypeDecl) {
        var fieldArgs = udt.TypeArgs.ConvertAll(a => ApplyActiveSubst(a).NormalizeExpand());
        var name = IdProtect(FullTypeName(udt)) + MangleTypeArgs(fieldArgs);
        return name == enclosingInstName;
      }
      return false;
    }

    // The instance name of the datatype a constructor belongs to, in the CURRENT
    // active substitution (so a boxed-field read at a use site can decide, using
    // the same rule as emission, whether the field was stored as a pointer).
    private string EnclosingDatatypeInstName(DatatypeCtor ctor) {
      var dt = ctor.EnclosingDatatype;
      var actuals = dt.TypeArgs.ConvertAll(tp =>
        ApplyActiveSubst((Type)new UserDefinedType(tp)).NormalizeExpand());
      return DatatypeBaseName(dt) + MangleTypeArgs(actuals);
    }

    // Uniform naming convention
    protected string DatatypeSubStructName(DatatypeCtor ctor, bool inclTemplateArgs = false) {
      string args = "";
      if (inclTemplateArgs) {
        args = InstantiateTemplate(ctor.EnclosingDatatype.TypeArgs);
      }
      return String.Format("{0}_{1}{2}", IdProtect(ctor.EnclosingDatatype.GetCompileName(Options)), ctor.GetCompileName(Options), args);
    }

    protected override bool DatatypeDeclarationAndMemberCompilationAreSeparate => false;
    // Like the C++ backend, this backend does not erase single-non-ghost-field
    // datatype/1-tuple wrappers. Consequence: `print Box(5)` yields "Box.Box(5)"
    // and a 1-tuple prints "(5)", vs C#'s erased bare "5" in its default mode.
    // Compare output against C# run with --optimize-erasable-datatype-wrapper:false.
    public override bool SupportsDatatypeWrapperErasure => false;

    protected override IClassWriter DeclareDatatype(DatatypeDecl dt, ConcreteSyntaxTree writer) {
      if (dt is TupleTypeDecl) {
        // Tuple types are declared once and for all in DafnyRuntime.h
        return null;
      }

      // Codatatypes (lazy/infinite) are unsupported (declared in UnsupportedFeatures).
      // Reject at declaration so we never reach the later destructor-lowering panic.
      if (dt is CoDatatypeDecl) {
        throw new UnsupportedFeatureException(dt.Origin, Feature.Codatatypes);
      }

      // C has no templates and no std::variant. A Dafny datatype is compiled to
      // a monomorphised C tagged union: one concrete struct per concrete
      // instantiation, emitted from the worklist in EmitDatatypeInstantiations.
      // Here we only SEED the worklist:
      //   * a non-generic datatype (e.g. Color, Pair) has exactly one
      //     instantiation (no type args), so register it directly so it is
      //     emitted even if it is never referenced through a generic use;
      //   * a generic datatype (e.g. Option<T>) is registered lazily at each
      //     concrete use site by TypeName -> RegisterDatatypeInstance, so nothing
      //     is seeded here (a parametric copy is not valid C).
      // The actual struct + create-function emission (tag enum, union members,
      // NAME_create_Ctor) all happens in EmitOneDatatypeInstance.
      // Recursive datatypes are supported by BOXING each self-referential field
      // behind a pointer (see IsBoxedField / EmitOneDatatypeInstance). We support
      // DIRECT self-recursion (a field whose type is this same datatype), which
      // covers the common cases (List, Tree). MUTUAL recursion between distinct
      // datatypes (A has a field of type B and B has a field of type A) is not yet
      // handled: those cross fields would still be by-value and produce an
      // infinite-size struct, so reject that case cleanly.
      if (HasMutualRecursion(dt)) {
        throw new UnsupportedFeatureException(dt.Origin, Feature.Codatatypes,
          "mutually-recursive datatypes are not yet supported by the C backend");
      }
      if (dt.TypeArgs.Count == 0) {
        RegisterDatatypeInstance(UserDefinedType.FromTopLevelDecl(dt.Origin, dt));
      }
      return null;
    }

    protected override IClassWriter DeclareNewtype(NewtypeDecl nt, ConcreteSyntaxTree wr) {
      // Non-native newtypes: only int- and real-based ones are supported, since
      // they lower to the GMP-backed DafnyInt/DafnyReal (see IsGmpInt/IsGmpReal and
      // the arithmetic routing in CompileBinOp). Any other non-native base (e.g. a
      // non-native bitvector) is still rejected cleanly.
      var baseIsGmpInt = nt.NativeType == null && IsGmpInt(nt.BaseType);
      var baseIsGmpReal = nt.NativeType == null && IsGmpReal(nt.BaseType);
      if (nt.NativeType != null) {
        if (nt.NativeType.Name != nt.Name) {
          GetNativeInfo(nt.NativeType.Sel, out var nt_name_def, out var literalSuffice_def, out var needsCastAfterArithmetic_def);
          wr.WriteLine("typedef {0} {1};", nt_name_def, nt.Name);
        }
      } else if (!baseIsGmpInt && !baseIsGmpReal) {
        throw new UnsupportedFeatureException(nt.Origin, Feature.NonNativeNewtypes);
      }
      var cw = CreateClass(nt.EnclosingModuleDefinition.GetCompileName(Options), nt, wr) as ClassWriter;
      var className = clName(nt);
      var w = cw.MethodDeclWriter;
      if (nt.WitnessKind == SubsetTypeDecl.WKind.Compiled) {
        // The Witness value is a static field named module_<newtype>_Witness (see
        // TypeInitializationValue, which reads Scope(module, clName(nt), "Witness")).
        // Emit it via the flat-global DeclareField path (passing `nt` as the
        // enclosing decl so the scoped name matches). The field's C type and the
        // witness value's form must agree: for a NATIVE newtype both are the native
        // machine integer (a `uint8`-typed field initialised from the literal); for
        // a non-native (GMP) newtype both are the DafnyInt base. The inherited
        // ".toNumber()" was a JS/Go-ism absent from C.
        var witness = new ConcreteSyntaxTree(w.RelativeIndentLevel);
        var wStmts = w.Fork();
        var witnessType = nt.BaseType;
        if (nt.NativeType != null) {
          // Native: cast the DafnyInt-rendered literal down to the native type, and
          // give the field the native type (via a UDT of the newtype itself).
          GetNativeInfo(nt.NativeType.Sel, out var ntName, out _, out _);
          witness.Write("({0})dafny_int_to_i64(", ntName);
          witness.Append(Expr(nt.Witness, false, wStmts));
          witness.Write(")");
          witnessType = UserDefinedType.FromTopLevelDecl(nt.Origin, nt);
        } else {
          witness.Append(Expr(nt.Witness, false, wStmts));
        }
        DeclareField(className, nt.TypeArgs, "Witness", true, true, witnessType, nt.Origin, witness.ToString(), w, wr, nt);
      }

      // (Previously a per-newtype `get_Default()` helper was emitted here, but it
      // was never referenced — newtype default values come from the Witness field
      // and from TypeInitializationValue at each use site — and an unscoped copy
      // collided across two newtypes. Removed as dead code.)

      return cw;
    }

    protected override void DeclareSubsetType(SubsetTypeDecl sst, ConcreteSyntaxTree wr) {
      // A subset type `type T = x: Base | P(x)` is TRANSPARENT in this backend: the
      // constraint P is a verification-time obligation, and everywhere a value of
      // type T is used, TypeName resolves it straight to its Base type (e.g. a
      // `Digit = x: int | …` value is just a DafnyInt). So there is nothing to
      // declare — the inherited C++ output (a `using` alias + a member `get_Default`
      // + a `.Witness` static field) was invalid C AND unnecessary. Emit nothing,
      // exactly as the `nat` case already did.
    }

    // Whether a native integer type is UNSIGNED. Matters for widening to an
    // unbounded int: an unsigned 64-bit value with the top bit set must go through
    // dafny_int_from_u64 (mpz_set_ui), not the signed dafny_int_from_i64, which
    // would store it as negative (0xFFFFFFFFFFFFFFFF -> -1).
    private static bool IsUnsignedNative(NativeType nt) {
      switch (nt.Sel) {
        case NativeType.Selection.Byte:
        case NativeType.Selection.UShort:
        case NativeType.Selection.UInt:
        case NativeType.Selection.ULong:
        case NativeType.Selection.UDoubleLong:
          return true;
        default:
          return false;
      }
    }

    // Emit `dafny_int_from_{u,i}64((cast)(expr))` widening a native source to a
    // DafnyInt, picking the unsigned helper for unsigned native types.
    private void WriteNativeToDafnyInt(Expression fromExpr, NativeType fromNative, bool inLetExprBody,
        ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      if (IsUnsignedNative(fromNative)) {
        wr.Write("dafny_int_from_u64((unsigned long long)(");
      } else {
        wr.Write("dafny_int_from_i64((long long)(");
      }
      wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
      wr.Write("))");
    }

    protected override void GetNativeInfo(NativeType.Selection sel, out string name, out string literalSuffix, out bool needsCastAfterArithmetic) {
      literalSuffix = "";
      needsCastAfterArithmetic = false;
      switch (sel) {
        case NativeType.Selection.Byte:
          name = "uint8";
          break;
        case NativeType.Selection.SByte:
          name = "int8";
          break;
        case NativeType.Selection.UShort:
          name = "uint16";
          break;
        case NativeType.Selection.Short:
          name = "int16";
          break;
        case NativeType.Selection.UInt:
          name = "uint32";
          break;
        case NativeType.Selection.Int:
          name = "int32";
          break;
        case NativeType.Selection.ULong:
          name = "uint64";
          break;
        case NativeType.Selection.Number:
        case NativeType.Selection.Long:
          name = "int64";
          break;
        default:
          Contract.Assert(false);  // unexpected native type
          throw new Cce.UnreachableException();  // to please the compiler
      }
    }

    protected class ClassWriter : IClassWriter {
      public string ClassName;
      public readonly CCodeGenerator CodeGenerator;
      public readonly ConcreteSyntaxTree MethodDeclWriter;
      public readonly ConcreteSyntaxTree MethodWriter;
      public readonly ConcreteSyntaxTree FieldWriter;
      public readonly ConcreteSyntaxTree Finisher;

      public ClassWriter(string className, CCodeGenerator codeGenerator, ConcreteSyntaxTree methodDeclWriter, ConcreteSyntaxTree methodWriter, ConcreteSyntaxTree fieldWriter, ConcreteSyntaxTree finisher) {
        Contract.Requires(codeGenerator != null);
        Contract.Requires(methodDeclWriter != null);
        Contract.Requires(methodWriter != null);
        Contract.Requires(fieldWriter != null);
        this.ClassName = className;
        this.CodeGenerator = codeGenerator;
        this.MethodDeclWriter = methodDeclWriter;
        this.MethodWriter = methodWriter;
        this.FieldWriter = fieldWriter;
        this.Finisher = finisher;
      }

      public ConcreteSyntaxTree/*?*/ CreateMethod(MethodOrConstructor m, List<TypeArgumentInstantiation> typeArgs, bool createBody, bool forBodyInheritance, bool lookasideBody) {
        return CodeGenerator.CreateMethod(m, typeArgs, createBody, MethodDeclWriter, MethodWriter, lookasideBody);
      }

      public ConcreteSyntaxTree SynthesizeMethod(Method m, List<TypeArgumentInstantiation> typeArgs, bool createBody, bool forBodyInheritance, bool lookasideBody) {
        throw new UnsupportedFeatureException(m.Origin, Feature.MethodSynthesis);
      }

      public ConcreteSyntaxTree/*?*/ CreateFunction(string name, List<TypeArgumentInstantiation>/*?*/ typeArgs,
        List<Formal> formals, Type resultType, IOrigin tok, bool isStatic, bool createBody, MemberDecl member, bool forBodyInheritance, bool lookasideBody) {
        return CodeGenerator.CreateFunction(member.EnclosingClass.GetCompileName(CodeGenerator.Options),
          member.EnclosingClass.TypeArgs, name, typeArgs, formals, resultType, tok, isStatic, createBody, member,
          MethodDeclWriter, MethodWriter, lookasideBody);
      }
      public ConcreteSyntaxTree/*?*/ CreateGetter(string name, TopLevelDecl enclosingDecl, Type resultType, IOrigin tok, bool isStatic, bool isConst, bool createBody, MemberDecl/*?*/ member, bool forBodyInheritance) {
        return CodeGenerator.CreateGetter(name, enclosingDecl, resultType, tok, isStatic, isConst, createBody, MethodDeclWriter, MethodWriter);
      }
      public ConcreteSyntaxTree/*?*/ CreateGetterSetter(string name, Type resultType, IOrigin tok, bool createBody, MemberDecl/*?*/ member, out ConcreteSyntaxTree setterWriter, bool forBodyInheritance) {
        return CodeGenerator.CreateGetterSetter(name, resultType, tok, createBody, out setterWriter, MethodWriter);
      }
      public void DeclareField(string name, TopLevelDecl enclosingDecl, bool isStatic, bool isConst, Type type, IOrigin tok, string rhs, Field field) {
        CodeGenerator.DeclareField(ClassName, enclosingDecl.TypeArgs, name, isStatic, isConst, type, tok, rhs, FieldWriter, Finisher, enclosingDecl);
      }
      public void InitializeField(Field field, Type instantiatedFieldType, TopLevelDeclWithMembers enclosingClass) {
        throw new Cce.UnreachableException();  // InitializeField should be called only for those compilers that set ClassesRedeclareInheritedFields to false.
      }
      public ConcreteSyntaxTree/*?*/ ErrorWriter() => MethodWriter;
      public void Finish() { }
    }

    protected ConcreteSyntaxTree/*?*/ CreateMethod(MethodOrConstructor m, List<TypeArgumentInstantiation> typeArgs, bool createBody, ConcreteSyntaxTree wdr, ConcreteSyntaxTree wr, bool lookasideBody) {
      List<Formal> nonGhostOuts = m.Outs.Where(o => !o.IsGhost).ToList();
      string targetReturnTypeReplacement = null;
      if (nonGhostOuts.Count == 1) {
        targetReturnTypeReplacement = TypeName(nonGhostOuts[0].Type, wr, nonGhostOuts[0].Origin);
      } else if (nonGhostOuts.Count > 1) {
        // A method with several non-ghost out-parameters returns a tuple. C has
        // no tuples, so it is monomorphised to a small struct DafnyTuple_<...>
        // with fields ._0, ._1, ... (registered/drained like seq element types).
        targetReturnTypeReplacement = RegisterTupleReturn(nonGhostOuts.ConvertAll(o => o.Type));
      }

      if (!createBody) {
        return null;
      }

      // C has no templates: no `template <...>` decoration on the definition.
      // Generics are monomorphised — one concrete copy per instantiation with
      // the concrete type args mangled into the name.
      //
      // On the FIRST (parametric) pass we cannot emit a usable definition for a
      // generic member: its formal type parameters (`T`) are not valid C. So we
      // defer: record the member and the target writers, and hand the base a
      // throwaway buffer to write the parametric body into (which we discard).
      // The concrete copies are emitted later from EmitConcreteInstantiations,
      // which re-enters CreateMethod with a substitution map installed
      // (activeSubst != null), at which point we DO emit real output.
      if (IsGeneric(m) && activeSubst == null) {
        pendingGenericMethods.Add(new PendingMethod {
          Member = m, DeclWriter = wdr, DefWriter = wr, LookasideBody = lookasideBody
        });
        return new ConcreteSyntaxTree();  // scratch: base writes the parametric body here; discarded
      }

      // C has no namespaces/classes: emit the definition with a fully flattened
      // name Module_Class_method (matching the call sites), and the declaration
      // as a free-function prototype with the same flat name (C++ used
      // Class::method inside a namespace + a class member decl). For a concrete
      // copy of a generic member the mangled suffix (e.g. "_bool") is appended so
      // the name matches the call site.
      // For a member of a reference class the class part uses RefClassName, which
      // carries the mangled type-argument suffix while a generic-class
      // instantiation is being emitted (e.g. _module_Box_bool___ctor), matching
      // the mangled companion call site. Other members keep the flat form plus any
      // generic-METHOD mangle suffix.
      var flatName = IsReferenceClass(m.EnclosingClass)
        ? Scope(RefClassName(m.EnclosingClass), IdName(m)) + (activeMangleSuffix ?? "")
        : Scope(
            IdProtect(m.EnclosingClass.EnclosingModuleDefinition.GetCompileName(Options)),
            clName(m.EnclosingClass),
            IdName(m)) + (activeMangleSuffix ?? "");

      wr.Write("{0} {1}",
        targetReturnTypeReplacement ?? "void",
        flatName);

      wdr.Write("{0} {1}",
        targetReturnTypeReplacement ?? "void",
        flatName);

      wr.Write("(");
      wdr.Write("(");
      // Instance member of a reference class: prepend an explicit `NAME* this`
      // parameter (methods are emitted as flat free functions, so the receiver
      // must be passed explicitly; the call site passes it as the first argument).
      var thisPrefix = "";
      if (IsInstanceRefMember(m)) {
        var thisParam = RefClassName(m.EnclosingClass) + "* this";
        wr.Write(thisParam);
        wdr.Write(thisParam);
        thisPrefix = ", ";
      }
      int nIns = WriteFormals(thisPrefix, m.Ins, wr);
      WriteFormals(thisPrefix, m.Ins, wdr);
      nIns += thisPrefix == "" ? 0 : 1;
      if (targetReturnTypeReplacement == null) {
        WriteFormals(nIns == 0 ? "" : ", ", m.Outs, wr);
        WriteFormals(nIns == 0 ? "" : ", ", m.Outs, wdr);
      }
      wdr.Write(");\n");

      var block = wr.NewBlock(")", null, BlockStyle.NewlineBrace, BlockStyle.NewlineBrace);

      if (targetReturnTypeReplacement != null) {
        var beforeReturnBlock = block.Fork(0);
        EmitReturn(m.Outs, block);
        return beforeReturnBlock;
      }
      return block;
    }

    protected ConcreteSyntaxTree/*?*/ CreateFunction(string className, List<TypeParameter> classArgs, string name, List<TypeArgumentInstantiation>/*?*/ typeArgs, List<Formal> formals, Type resultType, IOrigin tok, bool isStatic, bool createBody, MemberDecl member, ConcreteSyntaxTree wdr, ConcreteSyntaxTree wr, bool lookasideBody) {
      if (!createBody) {
        return null;
      }

      // C has no templates: no `template <...>` decoration (see CreateMethod).

      // Generic functions are monomorphised the same way as methods: on the
      // first (parametric) pass, defer and hand back a throwaway buffer; the
      // concrete copies are emitted later with a substitution map installed.
      if (member is Function fn && IsGeneric(member) && activeSubst == null) {
        pendingGenericFunctions.Add(new PendingFunction {
          Member = fn, ClassName = className, DeclWriter = wdr, DefWriter = wr, LookasideBody = lookasideBody
        });
        return new ConcreteSyntaxTree();  // scratch, discarded
      }

      // C: flat free-function name Module_Class_function for both the prototype
      // and the definition (C++ used static + Class::name inside a namespace).
      // For a concrete copy of a generic function the mangled suffix is appended.
      var flatName = IsReferenceClass(member.EnclosingClass)
        ? Scope(RefClassName(member.EnclosingClass), name) + (activeMangleSuffix ?? "")
        : Scope(
            IdProtect(member.EnclosingClass.EnclosingModuleDefinition.GetCompileName(Options)),
            className,
            name) + (activeMangleSuffix ?? "");
      wdr.Write("{0} {1}",
        TypeName(resultType, wr, tok),
        flatName);
      wr.Write("{0} {1}",
        TypeName(resultType, wr, tok),
        flatName);

      wdr.Write("(");
      wr.Write("(");
      // Instance function of a reference class: prepend an explicit `NAME* this`
      // parameter, matching the call site (see EmitNameAndActualTypeArgs).
      var thisPrefix = "";
      if (IsInstanceRefMember(member)) {
        var thisParam = RefClassName(member.EnclosingClass) + "* this";
        wdr.Write(thisParam);
        wr.Write(thisParam);
        thisPrefix = ", ";
      }
      WriteFormals(thisPrefix, formals, wdr);
      int nIns = WriteFormals(thisPrefix, formals, wr);

      wdr.Write(");");
      var w = wr.NewBlock(")", null, BlockStyle.NewlineBrace, BlockStyle.NewlineBrace);

      return w;
    }

    protected override void TypeArgDescriptorUse(bool isStatic, bool lookasideBody, TopLevelDeclWithMembers cl, out bool needsTypeParameter, out bool needsTypeDescriptor) {
      needsTypeParameter = false;
      needsTypeDescriptor = false;
    }

    protected override string TypeDescriptor(Type type, ConcreteSyntaxTree wr, IOrigin tok) {
      Contract.Requires(type != null);
      Contract.Requires(tok != null);
      Contract.Requires(wr != null);
      throw new UnsupportedFeatureException(tok, Feature.RuntimeTypeDescriptors, string.Format("RuntimeTypeDescriptor {0} not yet supported", type));
    }

    protected ConcreteSyntaxTree/*?*/ CreateGetter(string name, TopLevelDecl cls, Type resultType, IOrigin tok, bool isStatic, bool isConst, bool createBody, ConcreteSyntaxTree wdr, ConcreteSyntaxTree wr) {
      // Compiler insists on using Getter for constants, but we just use the raw variable name to hold the value,
      // because o/w Compiler tries to use the Getter function as an Lvalue in assignments
      // Unfortunately, Compiler doesn't tell us what the initial value is, so we hack around it
      // by declaring the variable and a function to statically initialize it

      ConcreteSyntaxTree w = null;
      string postfix = null;
      if (createBody) {
        w = wdr.NewNamedBlock("{0}{1} init__{2}()", isStatic ? "static " : "", TypeName(resultType, wr, tok), name);
        postfix = String.Format(" init__{0}()", name);
      }
      DeclareField(cls.GetCompileName(Options), cls.TypeArgs, name, isStatic, isConst, resultType, tok, postfix, wdr, wr, cls);
      //wdr.Write("{0}{1} {2}{3};", isStatic ? "static " : "", TypeName(resultType, wr, tok), name, postfix);
      return w;
    }

    protected ConcreteSyntaxTree/*?*/ CreateGetterSetter(string name, Type resultType, IOrigin tok, bool createBody, out ConcreteSyntaxTree setterWriter, ConcreteSyntaxTree wr) {
      // We don't use getter/setter pairs; we just embed the trait's fields.
      if (createBody) {
        var abyss = new ConcreteSyntaxTree();
        setterWriter = abyss;
        return abyss.NewBlock("");
      } else {
        setterWriter = null;
        return null;
      }
    }

    protected override ConcreteSyntaxTree EmitTailCallStructure(MemberDecl member, ConcreteSyntaxTree wr) {
      // In C11 a label must be followed by a *statement*, but the loop body that
      // follows may begin with a declaration (which is not a statement). Emit an
      // empty statement so `TAIL_CALL_START: <decl>` stays valid C.
      wr.WriteLine("TAIL_CALL_START: ;");
      return wr;
    }

    protected override void EmitJumpToTailCallStart(ConcreteSyntaxTree wr) {
      wr.WriteLine("goto TAIL_CALL_START;");
    }

    protected void Warn(string msg, IOrigin tok) {
      Options.ErrorWriter.WriteLine("WARNING: {3} ({0}:{1}:{2})", tok.Filepath, tok.line, tok.col, msg);
    }

    // Because we use reference counting (via shared_ptr), the TypeName of a class differs
    // depending on whether we are declaring a variable or talking about the class itself.
    // Use class_name = true if you want the actual name of the class, not the type used when declaring variables/arguments/etc.
    protected string TypeName(Type type, ConcreteSyntaxTree wr, IOrigin tok, MemberDecl/*?*/ member = null, bool class_name = false) {
      Contract.Ensures(Contract.Result<string>() != null);
      Contract.Assume(type != null);  // precondition; this ought to be declared as a Requires in the superclass

      // Monomorphisation: while a concrete copy of a generic member is being
      // emitted, resolve residual formal type parameters (`T`) to their concrete
      // actual (`bool`) so we produce concrete C type names.
      type = ApplyActiveSubst(type);
      var xType = type.NormalizeExpand();
      if (xType is TypeProxy) {
        // unresolved proxy; just treat as ref, since no particular type information is apparently needed for this type
        return "object";
      }

      if (xType is BoolType) {
        return "bool";
      } else if (xType is CharType) {
        return "dafny_char";
      } else if (xType is IntType or BigOrdinalType) {
        // Unbounded Dafny int -> GMP-backed pointer wrapper (see DafnyRuntime.h).
        // The minimal `c` target rejects it (like C++); only `c-extended` has GMP.
        RejectIfMinimal(Feature.UnboundedIntegers, tok);
        return "DafnyInt";
      } else if (xType is RealType) {
        // Dafny real is an exact rational -> GMP mpq-backed pointer wrapper.
        RejectIfMinimal(Feature.RealNumbers, tok);
        return "DafnyReal";
      } else if (xType is BitvectorType) {
        var t = (BitvectorType)xType;
        if (t.NativeType == null) {
          // A bitvector wider than 64 bits (e.g. bv128) has no native C type. The
          // inherited C++ spelling was "BigNumber" (a C++ runtime class that does
          // not exist in C), and its ops would emit ".And()"/".DivBy()" method
          // calls. Reject cleanly rather than emit invalid C. (bv1..bv64 map to
          // the native uint8/16/32/64 types and are fully supported.)
          throw new UnsupportedFeatureException(tok ?? Token.NoToken, Feature.RuntimeTypeDescriptors,
            "bitvectors wider than 64 bits are not supported by the C backend");
        }
        return GetNativeTypeName(t.NativeType);
      } else if (xType.AsNewtype != null) {
        var newtypeDecl = xType.AsNewtype;
        if (newtypeDecl.NativeType is { } nativeType) {
          return GetNativeTypeName(nativeType);
        }
        return TypeName(newtypeDecl.ConcreteBaseType(xType.TypeArgs), wr, tok, member);
      } else if (xType.IsObjectQ) {
        return "object";
      } else if (xType.IsArrayType) {
        ArrayClassDecl at = xType.AsArrayType;
        Contract.Assert(at != null);  // follows from type.IsArrayType
        Type elType = UserDefinedType.ArrayElementType(xType);
        if (at.Dims == 1) {
          // C has no templates: a 1-D array<T> is monomorphised to a per-element
          // struct DafnyArray_<elem> (data pointer + length). Registering the
          // element type schedules the struct+allocator in EmitArrayInstantiations.
          return "DafnyArray_" + RegisterArrayElementType(elType);
        } else {
          throw new UnsupportedFeatureException(tok, Feature.MultiDimensionalArrays);
        }
      } else if (xType is UserDefinedType) {
        var udt = (UserDefinedType)xType;
        if (udt.ResolvedClass is TupleTypeDecl) {
          // A Dafny tuple type `(T0, T1, ...)` maps to the SAME monomorphised
          // struct used for multiple-return values: DafnyTuple_<T0>_<T1>_... .
          // Registering it here schedules the typedef in
          // EmitTupleReturnInstantiations. The unit type () -> DafnyTuple_ (an
          // empty struct). GHOST components are erased at compile time, so only
          // the non-ghost element types become struct fields.
          return RegisterTupleReturn(NonGhostTupleArgs(udt));
        }
        if (xType is ArrowType) {
          // An arrow-typed variable/parameter/field. Function values are
          // unsupported by the C backend; reject rather than fall through to
          // TypeName_UDT (which would emit an invalid C++ shared_ptr spelling).
          throw new UnsupportedFeatureException(tok, Feature.FunctionValues);
        }
        var s = FullTypeName(udt, member);
        var cl = udt.ResolvedClass;
        if (xType.IsDatatype && cl is not TupleTypeDecl) {
          // C has no templates: a datatype instantiation is monomorphised to a
          // flat, mangled struct name (e.g. Option<bool> -> _module_Option_bool).
          // Registering the use here schedules the concrete tagged-union struct
          // for emission in EmitDatatypeInstantiations.
          return RegisterDatatypeInstance(udt);
        }
        if (IsReferenceClass(cl)) {
          // A reference class is a heap struct: variables/parameters use the
          // pointer type NAME*, but a bare class name (class_name, e.g. for
          // sizeof / the struct typedef) is just NAME.
          string refName;
          if (cl.TypeArgs.Count != 0) {
            // Generic reference class: monomorphise. Register the concrete
            // instantiation (so its struct + members get emitted) and use the
            // mangled struct name (e.g. _module_Box_bool).
            refName = RegisterClassInstance(udt);
          } else {
            refName = RefClassName(cl);
          }
          return class_name ? refName : refName + "*";
        }
        if (class_name || xType.IsTypeParameter || xType.IsAbstractType || xType.IsDatatype) {  // Don't add pointer decorations to class names or type parameters
          return IdProtect(s) + ActualTypeArgs(xType.TypeArgs);
        } else {
          return TypeName_UDT(s, udt, wr, udt.Origin);
        }
      } else if (xType is SetType) {
        Type argType = ((SetType)xType).Arg;
        if (ComplicatedTypeParameterForCompilation(TypeParameter.TPVariance.Co, argType)) {
          UnsupportedFeatureError(tok, Feature.CollectionsOfTraits, wr, "compilation of set<TRAIT> is not supported; consider introducing a ghost");
        }
        // C has no templates: set<T> is monomorphised to a per-element-type
        // struct `DafnySet_<elem>`. Registering the element type schedules the
        // concrete struct + helper emission in EmitSetInstantiations.
        return DafnySetClass + "_" + RegisterSetElementType(argType);
      } else if (xType is SeqType) {
        Type argType = ((SeqType)xType).Arg;
        if (ComplicatedTypeParameterForCompilation(TypeParameter.TPVariance.Co, argType)) {
          UnsupportedFeatureError(tok, Feature.CollectionsOfTraits, wr, "compilation of seq<TRAIT> is not supported; consider introducing a ghost");
        }
        // C has no templates: seq<T> is monomorphised to a per-element-type
        // struct `DafnySequence_<elem>` (e.g. DafnySequence_char). Registering
        // the element type here schedules the concrete struct + helper emission
        // in EmitSeqInstantiations. (Main's unused seq<seq<char>> args parameter
        // keeps the opaque `DafnySequence` spelling; see DeclareFormalString.)
        return DafnySeqClass + "_" + RegisterSeqElementType(argType);
      } else if (xType is MultiSetType) {
        Type argType = ((MultiSetType)xType).Arg;
        RejectIfMinimal(Feature.Multisets, tok);   // c-extended only
        if (ComplicatedTypeParameterForCompilation(TypeParameter.TPVariance.Co, argType)) {
          UnsupportedFeatureError(tok, Feature.CollectionsOfTraits, wr, "compilation of multiset<TRAIT> is not supported; consider introducing a ghost");
        }
        // C has no templates: multiset<T> is monomorphised to a per-element-type
        // struct `DafnyMultiset_<elem>`. Registering the element type schedules
        // the concrete struct + helper emission in EmitMultisetInstantiations.
        return DafnyMultiSetClass + "_" + RegisterMultisetElementType(argType);
      } else if (xType is MapType) {
        Type domType = ((MapType)xType).Domain;
        Type ranType = ((MapType)xType).Range;
        if (ComplicatedTypeParameterForCompilation(TypeParameter.TPVariance.Co, domType) || ComplicatedTypeParameterForCompilation(TypeParameter.TPVariance.Co, ranType)) {
          UnsupportedFeatureError(tok, Feature.CollectionsOfTraits, wr, "compilation of map<TRAIT, _> or map<_, TRAIT> is not supported; consider introducing a ghost");
        }
        // map<K,V> is monomorphised to a per-(key,value) struct DafnyMap_<k>_<v>.
        return DafnyMapClass + "_" + RegisterMapType(domType, ranType);
      } else {
        Contract.Assert(false); throw new Cce.UnreachableException();  // unexpected type
      }
    }

    internal override string TypeName(Type type, ConcreteSyntaxTree wr, IOrigin tok, MemberDecl/*?*/ member = null) {
      Contract.Ensures(Contract.Result<string>() != null);
      Contract.Assume(type != null);  // precondition; this ought to be declared as a Requires in the superclass
      return TypeName(type, wr, tok, member, false);
    }

    protected override string TypeInitializationValue(Type type, ConcreteSyntaxTree wr, IOrigin tok, bool usePlaceboValue, bool constructTypeParameterDefaultsFromTypeDescriptors) {
      // Monomorphisation: resolve residual formal type parameters to their
      // concrete actual so a generic local's default value (e.g. the `r` out
      // parameter of Id<T>) becomes the concrete default (`false`) rather than
      // the invalid C++ `get_default<T>::call()`.
      type = ApplyActiveSubst(type);
      var xType = type.NormalizeExpandKeepConstraints();
      if (xType is BoolType) {
        return "false";
      } else if (xType is CharType) {
        return CharType.DefaultValueAsString;
      } else if (xType is IntType or BigOrdinalType) {
        return "dafny_int_from_i64(0)";
      } else if (xType is RealType) {
        return "dafny_real_from_frac(\"0\", \"1\")";
      } else if (xType is BitvectorType) {
        var t = (BitvectorType)xType;
        if (t.NativeType != null) {
          return "0";
        } else {
          Warn("Non-native bitvector type used.  Code will not compile.", tok);
          return "new BigNumber(0)";
        }
      } else if (xType is SetType) {
        var s = (SetType)xType;
        var suffix = RegisterSetElementType(s.Arg);
        return string.Format("dafny_set_{0}_create(0, NULL)", suffix);
      } else if (xType is MultiSetType) {
        var ms = (MultiSetType)xType;
        var suffix = RegisterMultisetElementType(ms.Arg);
        return string.Format("dafny_multiset_{0}_create(0, NULL)", suffix);
      } else if (xType is SeqType) {
        // Empty sequence: zero-length, NULL data. Use the monomorphised helper.
        var suffix = RegisterSeqElementType(xType.AsSeqType.Arg);
        return string.Format("dafny_seq_{0}_create(0, NULL)", suffix);
      } else if (xType is MapType) {
        var m = (MapType)xType;
        var suffix = RegisterMapType(m.Domain, m.Range);
        return string.Format("dafny_map_{0}_create(0, NULL, NULL)", suffix);
      }

      var udt = (UserDefinedType)xType;
      var cl = udt.ResolvedClass;
      Contract.Assert(cl != null);
      if (cl is TypeParameter or AbstractTypeDecl) {
        // A default value for a bare type parameter T (or an abstract type).
        // Under monomorphisation, ApplyActiveSubst (above) replaces T with the
        // concrete actual whenever a concrete instantiation is being emitted, so
        // reaching here with a still-formal parameter means EITHER (a) we are in
        // the DISCARDED generic-template pass (activeSubst == null) whose output is
        // never compiled, or (b) a genuinely unsupported unsubstituted default.
        // In case (a) emit a harmless placeholder ((void*)0) into the scratch
        // buffer rather than throwing (throwing would abort a program that DOES
        // have a valid concrete instantiation, e.g. the out-param `r:T` of Id<T>).
        // In case (b) reject cleanly — the inherited C++ "get_default<T>::call()"
        // spelling is not valid C anyway.
        if (activeSubst == null) {
          return "((void*)0)";
        }
        throw new UnsupportedFeatureException(tok, Feature.RuntimeTypeDescriptors,
          "a default value for an unsubstituted type parameter / abstract type is not supported by the C backend");
      } else if (cl is NewtypeDecl) {
        var td = (NewtypeDecl)cl;
        if (td.Witness != null) {
          return Scope(td.EnclosingModuleDefinition.GetCompileName(Options), clName(td), "Witness");
        } else if (td.NativeType != null) {
          return "0";
        } else {
          return TypeInitializationValue(td.ConcreteBaseType(udt.TypeArgs), wr, tok, usePlaceboValue, constructTypeParameterDefaultsFromTypeDescriptors);
        }
      } else if (cl is SubsetTypeDecl) {
        var td = (SubsetTypeDecl)cl;
        if (td.WitnessKind == SubsetTypeDecl.WKind.Compiled) {
          return Scope(td.EnclosingModuleDefinition.GetCompileName(Options), clName(td), "Witness");
        } else if (td.WitnessKind == SubsetTypeDecl.WKind.Special) {
          // WKind.Special is only used with -->, ->, and non-null types:
          Contract.Assert(ArrowType.IsPartialArrowTypeName(td.Name) || ArrowType.IsTotalArrowTypeName(td.Name) || td is NonNullTypeDecl);
          if (ArrowType.IsPartialArrowTypeName(td.Name) || ArrowType.IsTotalArrowTypeName(td.Name)) {
            // Default value of an arrow type — function values are unsupported.
            throw new UnsupportedFeatureException(tok, Feature.FunctionValues);
          } else if (((NonNullTypeDecl)td).Class is ArrayClassDecl) {
            // non-null array type; we know how to initialize them
            var arrayClass = (ArrayClassDecl)((NonNullTypeDecl)td).Class;
            Type elType = UserDefinedType.ArrayElementType(xType);
            if (arrayClass.Dims == 1) {
              // Default/null 1-D array: empty (NULL data, length 0).
              var suffix = RegisterArrayElementType(elType);
              return string.Format("(DafnyArray_{0}){{ NULL, 0 }}", suffix);
            } else {
              throw new UnsupportedFeatureException(tok, Feature.MultiDimensionalArrays);
            }
          } else {
            // non-null (non-array) type: lay down a null pointer to satisfy
            // definite-assignment placebo initialisation. C spelling is NULL.
            return "NULL";
          }
        } else {
          return TypeInitializationValue(td.RhsWithArgument(udt.TypeArgs), wr, tok, usePlaceboValue, constructTypeParameterDefaultsFromTypeDescriptors);
        }
      } else if (cl is ClassLikeDecl or ArrowTypeDecl) {
        if (cl is ArrayClassDecl) {
          var arrayClass = (ArrayClassDecl)cl;
          Type elType = UserDefinedType.ArrayElementType(xType);
          if (arrayClass.Dims == 1) {
            var suffix = RegisterArrayElementType(elType);
            return string.Format("(DafnyArray_{0}){{ NULL, 0 }}", suffix);
          } else {
            throw new UnsupportedFeatureException(tok, Feature.MultiDimensionalArrays);
          }
        } else if (cl is ArrowTypeDecl) {
          // Default value of an arrow type — function values are unsupported.
          throw new UnsupportedFeatureException(tok, Feature.FunctionValues);
        } else {
          // A reference class default: the value is a NAME* pointer, so NULL.
          return "NULL";
        }
      } else if (cl is DatatypeDecl) {
        var dt = (DatatypeDecl)cl;
        if (dt is TupleTypeDecl) {
          // Default value of a tuple type: the monomorphised struct with each
          // NON-GHOST field set to its element type's default. () (or all-ghost)
          // -> the dummy-field form.
          var nonGhostArgs = NonGhostTupleArgs(udt);
          var structName = RegisterTupleReturn(nonGhostArgs);
          if (nonGhostArgs.Count == 0) {
            return string.Format("({0}){{ 0 }}", structName);
          }
          var elemDefaults = nonGhostArgs.ConvertAll(a =>
            DefaultValue(ApplyActiveSubst(a).NormalizeExpand(), wr, tok, constructTypeParameterDefaultsFromTypeDescriptors));
          return string.Format("({0}){{ {1} }}", structName, string.Join(", ", elemDefaults));
        }
        // C tagged union: the default value is the first constructor applied to
        // the default values of its fields, built via the monomorphised
        // NAME_create_Ctor0(...) function.
        var instName = RegisterDatatypeInstance(udt);
        var subst = BuildSubst(dt.TypeArgs, udt.TypeArgs.ConvertAll(t => ApplyActiveSubst(t).NormalizeExpand()));
        var ctor0 = dt.Ctors.First(c => !c.IsGhost);
        var args = new List<string>();
        foreach (var arg in ctor0.Formals) {
          if (!arg.IsGhost) {
            args.Add(DefaultValue(arg.Type.Subst(subst), wr, tok, constructTypeParameterDefaultsFromTypeDescriptors));
          }
        }
        return String.Format("{0}_create_{1}({2})", instName, ctor0.GetCompileName(Options), string.Join(", ", args));
      } else {
        Contract.Assert(false); throw new Cce.UnreachableException();  // unexpected type
      }

    }

    private string ActualTypeArgs(List<Type> typeArgs) {
      return typeArgs.Count > 0
        ? String.Format(" <{0}> ", Util.Comma(typeArgs, tp => TypeName(tp, null, Token.NoToken))) : "";
    }

    protected override string TypeName_UDT(string fullCompileName, List<TypeParameter.TPVariance> variance, List<Type> typeArgs,
      ConcreteSyntaxTree wr, IOrigin tok, bool omitTypeArguments) {
      Contract.Assume(fullCompileName != null);  // precondition; this ought to be declared as a Requires in the superclass
      Contract.Assume(typeArgs != null);  // precondition; this ought to be declared as a Requires in the superclass
      // The main TypeName routes every supported user-defined type (datatype,
      // reference class, arrow, tuple, collection, newtype) to its own C spelling
      // BEFORE delegating here, so this inherited fallthrough is only reached for
      // an unsupported UDT kind. The C++ body emitted "std::shared_ptr<...>", which
      // is not C. In the DISCARDED generic-template pass (activeSubst == null) the
      // output is never compiled, so emit a harmless placeholder rather than abort
      // a program that has a valid concrete instantiation; otherwise reject cleanly.
      if (activeSubst == null) {
        return "void*";
      }
      throw new UnsupportedFeatureException(tok ?? Token.NoToken, Feature.RuntimeTypeDescriptors,
        "this user-defined type is not supported by the C backend (no C type spelling): " + fullCompileName);
    }

    internal override string TypeName_Companion(Type type, ConcreteSyntaxTree wr, IOrigin tok, MemberDecl/*?*/ member) {
      // There are no companion classes for Cpp
      var t = TypeName(type, wr, tok, member, true);
      return t;
    }

    // ----- Declarations -------------------------------------------------------------
    protected override void DeclareExternType(AbstractTypeDecl d, Expression compileTypeHint, ConcreteSyntaxTree wr) {
      if (compileTypeHint.AsStringLiteral() == "struct") {
        modDeclWr.WriteLine("// Extern declaration of {1}\n{0} struct {1};", DeclareTemplate(d.TypeArgs), d.Name);
      } else {
        Error(GeneratorErrors.ErrorId.c_abstract_type_cannot_be_compiled_extern, d.Origin, wr,
          "Abstract type ('{0}') with unrecognized extern attribute {1} cannot be compiled.  Expected {{:extern compile_type_hint}}, e.g., 'struct'.", d.FullName, compileTypeHint.AsStringLiteral());
      }
    }

    protected void DeclareField(string className, List<TypeParameter> targs, string name, bool isStatic, bool isConst, Type type, IOrigin tok, string rhs, ConcreteSyntaxTree wr, ConcreteSyntaxTree finisher, TopLevelDecl enclosingDecl = null) {
      var t = TypeName(type, wr, tok);
      // An instance field of a reference class is a plain C struct member:
      // `type name;` (no initializer; C struct members cannot be initialized in
      // place). The field is zero-initialized at malloc and then set by the
      // constructor. (wr here is the struct body created in CreateClass.)
      if (!isStatic && enclosingDecl != null && IsReferenceClass(enclosingDecl)) {
        wr.WriteLine("{0} {1};", t, name);
        return;
      }
      var r = rhs != null ? rhs : DefaultValue(type, wr, tok);
      if (isStatic) {
        // A static field / module-level or class const. The read side names it with
        // the FLAT scoped name (module_class_name), so declare a global with that
        // name and initialise it at startup. The C++ spellings ("Class::name = …",
        // a member init function) are not valid C. `rhs` is either an init-function
        // call like " init__name()" (built by CreateGetter, whose body computes the
        // value) or a default value; either way it may be a non-constant expression
        // (e.g. a GMP call), which C forbids in a file-scope initializer — so assign
        // it from a __attribute__((constructor)) function that runs before main.
        // The read side (EmitMemberSelect for a static ConstantField) names the
        // field with the flat scoped name module_class_name, built via Scope(...).
        // Match it exactly here.
        var flatName = enclosingDecl != null
          ? Scope(IdProtect(enclosingDecl.EnclosingModuleDefinition.GetCompileName(Options)), IdProtect(clName(enclosingDecl)), name)
          : Scope(className, name);
        wr.WriteLine("{0} {1};", t, flatName);
        var ctorName = "_dafny_init_" + flatName;
        finisher.WriteLine("__attribute__((constructor)) static void {0}(void) {{ {1} = {2}; }}", ctorName, flatName, r.Trim());
      } else {
        wr.WriteLine("{0} {1} = {2};", t, name, r);
      }
    }

    private string DeclareFormalString(string prefix, string name, Type type, IOrigin tok, bool isInParam) {
      if (isInParam) {
        // Main's args parameter is seq<seq<char>> but is always unused. Keep the
        // opaque `DafnySequence` placeholder type for it (matching
        // dafny_get_args / CreateStaticMain) instead of monomorphising a nested
        // sequence struct that nothing reads.
        var typeName = name == "__noArgsParameter" ? DafnySeqClass : TypeName(type, null, tok);
        var result = String.Format("{0}{2} {1}", prefix, name, typeName);
        if (name == "__noArgsParameter") {
          result += " __attribute__((unused))";
        }

        return result;
      } else {
        return null;
      }
    }

    protected override bool DeclareFormal(string prefix, string name, Type type, IOrigin tok, bool isInParam, ConcreteSyntaxTree wr) {
      var formal_str = DeclareFormalString(prefix, name, type, tok, isInParam);
      if (formal_str != null) {
        wr.Write(formal_str);
        return true;
      } else {
        return false;
      }
    }

    private string DeclareFormals(List<Formal> formals) {
      var i = 0;
      var ret = "";
      var sep = "";
      foreach (Formal arg in formals) {
        if (!arg.IsGhost) {
          string name = FormalName(arg, i);
          string decl = DeclareFormalString(sep, name, arg.Type, arg.Origin, arg.InParam);
          if (decl != null) {
            ret += decl;
            sep = ", ";
          }
          i++;
        }
      }
      return ret;
    }

    protected override void DeclareLocalVar(string name, Type/*?*/ type, IOrigin/*?*/ tok, bool leaveRoomForRhs, string/*?*/ rhs, ConcreteSyntaxTree wr) {
      if (type != null) {
        wr.Write("{0} ", TypeName(type, wr, tok));
      } else {
        wr.Write("auto ");
      }
      wr.Write("{0}", name);
      if (leaveRoomForRhs) {
        Contract.Assert(rhs == null);  // follows from precondition
      } else if (rhs != null) {
        wr.WriteLine(" = {0};", rhs);
      } else {
        wr.WriteLine(";");
      }
    }

    protected override ConcreteSyntaxTree DeclareLocalVar(string name, Type/*?*/ type, IOrigin/*?*/ tok, ConcreteSyntaxTree wr) {
      if (type != null) {
        wr.Write("{0} ", TypeName(type, wr, tok));
      } else {
        wr.Write("auto ");
      }
      wr.Write("{0} = ", name);
      var w = wr.Fork();
      wr.WriteLine(";");
      return w;
    }

    protected override bool UseReturnStyleOuts(MethodOrConstructor m, int nonGhostOutCount) => true;

    protected override void DeclareOutCollector(string collectorVarName, ConcreteSyntaxTree wr) {
      // Fallback: C has no `auto`. This overload has no type info, but in
      // practice the return-style multi-out path always goes through
      // DeclareSpecificOutCollector below (which knows the formal types), so
      // this is unreachable for a real tuple return.
      wr.Write("auto {0} = ", collectorVarName);
    }

    protected override void DeclareSpecificOutCollector(string collectorVarName, ConcreteSyntaxTree wr,
      List<Type> formalTypes, List<Type> lhsTypes) {
      // A multi-out call is captured into a single DafnyTuple_<...> struct value.
      // Declare it with the concrete struct type (C has no `auto`).
      var structName = RegisterTupleReturn(formalTypes);
      wr.Write("{0} {1} = ", structName, collectorVarName);
    }

    protected override void DeclareLocalOutVar(string name, Type type, IOrigin tok, string rhs, bool useReturnStyleOuts, ConcreteSyntaxTree wr) {
      DeclareLocalVar(name, type, tok, false, rhs, wr);
    }

    protected override void EmitOutParameterSplits(string outCollector, List<string> actualOutParamNames, ConcreteSyntaxTree wr) {
      if (actualOutParamNames.Count == 1) {
        EmitAssignment(actualOutParamNames[0], null, outCollector, null, wr);
      } else {
        // Unpack the captured DafnyTuple_<...> struct into the actual out-vars:
        //   name0 = collector._0; name1 = collector._1; ...
        for (var i = 0; i < actualOutParamNames.Count; i++) {
          EmitAssignment(actualOutParamNames[i], null,
            string.Format("{0}._{1}", outCollector, i), null, wr);
        }
      }
    }

    protected override void EmitActualTypeArgs(List<Type> typeArgs, IOrigin tok, ConcreteSyntaxTree wr) {
      wr.Write(ActualTypeArgs(typeArgs));
    }

    protected void EmitNullText(Type type, ConcreteSyntaxTree wr) {
      var xType = type.NormalizeExpand();
      if (xType.IsArrayType) {
        ArrayClassDecl at = xType.AsArrayType;
        Contract.Assert(at != null);  // follows from xType.IsArrayType
        Type elType = UserDefinedType.ArrayElementType(xType);
        if (at.Dims == 1) {
          // A null 1-D array is the empty struct value (NULL data, length 0).
          var suffix = RegisterArrayElementType(elType);
          wr.Write("(DafnyArray_{0}){{ NULL, 0 }}", suffix);
        } else {
          throw new UnsupportedFeatureException(Token.NoToken, Feature.MultiDimensionalArrays);
        }
      } else {
        // A null reference (e.g. a `Node?` field/variable) is the C null pointer.
        wr.Write("NULL");
      }
    }

    protected override void EmitNull(Type type, ConcreteSyntaxTree wr) {
      EmitNullText(type, wr);
    }

    // ----- Statements -------------------------------------------------------------

    protected override void EmitPrintStmt(ConcreteSyntaxTree wr, Expression arg) {
      var wStmts = wr.Fork();
      // Sequences are structs, which C11 _Generic in dafny_print cannot dispatch
      // on (each is a distinct type). Route seq<char> (strings) to the dedicated
      // char-sequence printer; scalars keep the _Generic dafny_print macro.
      var substType = ApplyActiveSubst(arg.Type);
      var argType = substType.NormalizeToAncestorType();
      if (argType is SeqType st && st.Arg.IsCharType) {
        wr.Write("dafny_print_seq_char(");
      } else if (argType is CharType) {
        // A Dafny char is a dafny_char code point; print it UTF-8-encoded rather
        // than via the _Generic dafny_print macro (which would print the numeric
        // code point value, since dafny_char aliases uint32).
        wr.Write("dafny_print_char(");
      } else if ((argType is IntType or BigOrdinalType) && AsNativeType(substType) == null && extended) {
        // Extended target only: unbounded int -> GMP-backed DafnyInt (a struct
        // pointer). _Generic in dafny_print cannot dispatch on it, so use the
        // dedicated printer. In the minimal target unbounded int is rejected, so
        // any `int`-typed value reaching here (e.g. a cardinality) is native and
        // falls through to the _Generic dafny_print path below.
        // NativeType newtypes (e.g. int32) normalize to IntType but are machine
        // integers and must keep the _Generic dafny_print path.
        wr.Write("dafny_print_int(");
      } else if ((argType is IntType or BigOrdinalType) && !extended) {
        // Minimal target: an `int`-typed value here is a native size_t (a
        // cardinality); print it via the _Generic dafny_print macro.
        wr.Write("dafny_print(");
      } else if (argType is RealType) {
        // real is GMP-backed (DafnyReal) — extended only. Reject in minimal `c`
        // rather than emit a call into the GMP-guarded printer.
        RejectIfMinimal(Feature.RealNumbers, arg.Origin);
        wr.Write("dafny_print_real(");
      } else if (argType is BoolType) {
        // A Dafny bool. In C, a comparison/logical expression (x == y, x < y, !p)
        // has static type `int`, not `bool`, so the _Generic dafny_print macro
        // would hit its numeric `default` branch and print "1"/"0" instead of
        // "true"/"false". Force the bool branch with an explicit (bool) cast.
        wr.Write("dafny_print((bool)(");
        wr.Append(Expr(arg, false, wStmts));
        wr.WriteLine("));");
        return;
      } else if (AsNativeType(substType) != null) {
        // Native-integer newtypes: the _Generic dafny_print macro dispatches on
        // the concrete machine-integer C type.
        wr.Write("dafny_print(");
      } else if (argType is SeqType nonCharSeq) {
        // A non-string sequence: render Dafny's "[e0, e1, ...]" form via a
        // generated per-element-type printer (see RegisterSeqPrinter). The
        // element type must itself be printable, else this throws cleanly.
        var suffix = RegisterSeqPrinter(nonCharSeq.Arg);
        wr.Write("dafny_print_seq_{0}(", suffix);
      } else if (argType is UserDefinedType tupType && tupType.ResolvedClass is TupleTypeDecl) {
        // A tuple value: render Dafny's "(e0, e1, ...)" form via a generated
        // per-shape printer (see RegisterTuplePrinter). Each field type must
        // itself be printable, else this throws cleanly.
        var suffix = RegisterTuplePrinter(NonGhostTupleArgs(tupType));
        wr.Write("dafny_print_tuple_{0}(", suffix);
      } else if (argType is UserDefinedType dtType && dtType.ResolvedClass is DatatypeDecl dtDecl && dtDecl is not TupleTypeDecl) {
        // A datatype value: render Dafny's "<Name>.<Ctor>(f0, f1, ...)" form via
        // a generated per-instantiation printer (see RegisterDatatypePrinter).
        // Each field type must itself be printable, else this throws cleanly.
        var instName = RegisterDatatypePrinter(dtType);
        wr.Write("dafny_print_dt_{0}(", instName);
      } else if (substType.IsArrayType) {
        // Printing a whole array value: C# prints an object reference (e.g.
        // "array<...>" with an identity hash) that the C backend cannot
        // reproduce byte-for-byte. Reject cleanly. (a.Length, a[i] and a[..]
        // are all supported.)
        throw new UnsupportedFeatureException(arg.Origin, Feature.ConvertingValuesToStrings,
          "printing an array reference is not supported by the C backend");
      } else if (argType is MultiSetType msType) {
        // A whole multiset value: render Dafny's "multiset{e0, e0, e1}" form via a
        // generated per-element-type printer (see RegisterMultisetPrinter). The
        // element type must itself be printable, else this throws cleanly. Order
        // of distinct elements is the C hash-table order (see note there).
        var suffix = RegisterMultisetPrinter(msType.Arg);
        wr.Write("dafny_print_multiset_{0}(", suffix);
      } else if (argType is SetType setType) {
        // A whole set value: render Dafny's "{e0, e1, ...}" form via a generated
        // per-element-type printer (see RegisterSetPrinter). The element type must
        // itself be printable, else this throws cleanly. Element order is the C
        // hash-table order (see note at RegisterSetPrinter).
        var suffix = RegisterSetPrinter(setType.Arg);
        wr.Write("dafny_print_set_{0}(", suffix);
      } else if (argType is MapType mapType) {
        // A whole map value: render Dafny's "map[k := v, ...]" form via a generated
        // per-(key,value)-type printer (see RegisterMapPrinter). Both types must
        // themselves be printable, else this throws cleanly. Entry order is the C
        // hash-table order (see note at RegisterSetPrinter).
        var suffix = RegisterMapPrinter(mapType.Domain, mapType.Range);
        wr.Write("dafny_print_map_{0}(", suffix);
      } else {
        // Any other unsupported print target: reject cleanly rather than emit
        // invalid C. (seq, tuples, datatypes, set/map/multiset ARE handled above.)
        throw new UnsupportedFeatureException(arg.Origin, Feature.ConvertingValuesToStrings,
          "printing a value of type '" +
          TypeName(argType, null, arg.Origin, null, false) + "' is not supported by the C backend");
      }
      wr.Append(Expr(arg, false, wStmts));
      wr.WriteLine(");");
    }

    protected override void EmitReturn(List<Formal> outParams, ConcreteSyntaxTree wr) {
      outParams = outParams.Where(f => !f.IsGhost).ToList();
      if (!outParams.Any()) {
        wr.WriteLine("return;");
      } else if (outParams.Count == 1) {
        wr.WriteLine("return {0};", IdName(outParams[0]));
      } else {
        // Multiple out-parameters: build and return the monomorphised
        // DafnyTuple_<...> struct value with fields ._0, ._1, ... .
        var structName = RegisterTupleReturn(outParams.ConvertAll(o => o.Type));
        var fields = new List<string>();
        for (var i = 0; i < outParams.Count; i++) {
          fields.Add(string.Format("._{0} = {1}", i, IdName(outParams[i])));
        }
        wr.WriteLine("return ({0}){{ {1} }};", structName, string.Join(", ", fields));
      }
    }

    protected override ConcreteSyntaxTree CreateLabeledCode(string label, bool createContinueLabel, ConcreteSyntaxTree wr) {
      var w = wr.Fork();
      var prefix = createContinueLabel ? "continue_" : "after_";
      wr.Fork(-1).WriteLine($"{prefix}{label}: ;");
      return w;
    }

    protected override void EmitBreak(string/*?*/ label, ConcreteSyntaxTree wr) {
      if (label == null) {
        wr.WriteLine("break;");
      } else {
        wr.WriteLine("goto after_{0};", label);
      }
    }

    protected override void EmitContinue(string label, ConcreteSyntaxTree wr) {
      wr.WriteLine("goto continue_{0};", label);
    }

    protected override void EmitYield(ConcreteSyntaxTree wr) {
      throw new UnsupportedFeatureException(Token.NoToken, Feature.Iterators);
    }

    protected override void EmitAbsurd(string/*?*/ message, ConcreteSyntaxTree wr) {
      if (message == null) {
        message = "unexpected control point";
      }
      wr.WriteLine("throw \"{0}\";", message);
    }

    protected override void EmitHalt(IOrigin tok, Expression messageExpr, ConcreteSyntaxTree wr) {
      var wStmts = wr.Fork();
      wr.Write("throw DafnyHaltException(");
      if (tok != null) {
        wr.Write("\"" + tok.OriginToString(Options) + ": \" + ");
      }

      if (messageExpr.Type.IsStringType) {
        wr.Write("ToVerbatimString(");
        wr.Append(Expr(messageExpr, false, wStmts));
        wr.Write(")");
      } else {
        throw new UnsupportedFeatureException(tok, Feature.ConvertingValuesToStrings);
      }

      wr.WriteLine(");");
    }

    protected override ConcreteSyntaxTree EmitForStmt(IOrigin tok, IVariable loopIndex, bool goingUp,
      string/*?*/ endVarName,
      List<Statement> body, List<Label> labels, ConcreteSyntaxTree wr) {
      // Dafny `for i := start to end` / `for i := start downto end`. Lower to a C
      // for-loop. The index type is either a native machine integer or a GMP
      // DafnyInt; emit the right init/cond/step for each. `endVarName` (null for an
      // unbounded `for i := start to *`) was pre-declared by the base; we write the
      // START expression into the returned writer.
      var idx = IdName(loopIndex);
      var idxType = ApplyActiveSubst(loopIndex.Type).NormalizeExpand();
      var nativeType = AsNativeType(idxType);
      var typeName = TypeName(idxType, wr, tok);

      // Build the condition and step clauses for either a native machine integer or
      // a GMP DafnyInt index, in the loop direction. Dafny's `downto` decrements the
      // index BEFORE running the body, so for the downward GMP/native case we do the
      // step at the top of the body rather than in the for-header's step clause.
      string cond, step;
      bool stepAtBodyTop = false;
      if (nativeType != null) {
        if (goingUp) {
          cond = endVarName == null ? "1" : string.Format("{0} < {1}", idx, endVarName);
          step = string.Format("{0}++", idx);
        } else {
          cond = endVarName == null ? "1" : string.Format("{0} < {1}", endVarName, idx);
          step = ""; stepAtBodyTop = true;
        }
      } else {
        if (goingUp) {
          cond = endVarName == null ? "1" : string.Format("dafny_int_lt({0}, {1})", idx, endVarName);
          step = string.Format("{0} = dafny_int_add({0}, dafny_int_from_i64(1))", idx);
        } else {
          cond = endVarName == null ? "1" : string.Format("dafny_int_lt({0}, {1})", endVarName, idx);
          step = ""; stepAtBodyTop = true;
        }
      }

      // Emit `for (<type> <idx> = <START>; <cond>; <step>) { <body> }`. The START
      // expression is written by the base into the returned fork, so it sits between
      // the "= " prefix and the "; cond; step)" block header.
      wr.Write("for ({0} {1} = ", typeName, idx);
      var startWr = wr.Fork();
      // NewBlock emits `<header> { … }` verbatim (it does NOT add a closing paren),
      // so the ")" that closes the for-header must be part of the header string.
      var bodyWr = wr.NewBlock(string.Format("; {0}; {1})", cond, step));
      if (stepAtBodyTop) {
        // `downto`: decrement before the body executes.
        var decr = nativeType != null
          ? string.Format("{0}--;", idx)
          : string.Format("{0} = dafny_int_sub({0}, dafny_int_from_i64(1));", idx);
        bodyWr.WriteLine(decr);
      }
      bodyWr = EmitContinueLabel(labels, bodyWr);
      TrStmtList(body, bodyWr);
      return startWr;
    }

    protected override ConcreteSyntaxTree CreateForLoop(string indexVar, Action<ConcreteSyntaxTree> boundAction, ConcreteSyntaxTree wr, string start = null) {
      // Not reached: the base only calls CreateForLoop from the multi-dimensional
      // array-initialization-with-function lowering (SinglePassCodeGenerator
      // ~4308/4351), and that path is rejected earlier via Feature.FunctionValues
      // (the arrow-typed init local). Dafny `for` statements do not route here.
      // Assert rather than emit the inherited C++ body ("for (auto ...)", invalid
      // C11). (Feature.ForLoops is in neither UnsupportedFeatures set, so throwing
      // it here would crash the not-an-element check if ever reached.)
      Contract.Assert(false); throw new Cce.UnreachableException();  // CreateForLoop is unreachable in the C backend
    }

    protected override ConcreteSyntaxTree CreateDoublingForLoop(string indexVar, int start, ConcreteSyntaxTree wr) {
      return wr.NewNamedBlock("for (unsigned long long {0} = 1; ; {0} = {0} * 2)", indexVar, start);
    }

    protected override void EmitIncrementVar(string varName, ConcreteSyntaxTree wr) {
      wr.WriteLine("{0} += 1;", varName);
    }

    protected override void EmitDecrementVar(string varName, ConcreteSyntaxTree wr) {
      // The inherited body was "{0} = {0} -= 1;" — malformed C (double assignment,
      // undefined behaviour). Only for-loop lowering calls this, and for loops are
      // rejected (Feature.ForLoops), so it is unreached; emit correct C anyway
      // rather than leave a landmine.
      wr.WriteLine("{0} -= 1;", varName);
    }

    protected override string GetQuantifierName(string bvType) {
      throw new UnsupportedFeatureException(Token.NoToken, Feature.Quantifiers);
    }

    protected override ConcreteSyntaxTree CreateForeachLoop(string tmpVarName, Type collectionElementType, IOrigin tok,
      out ConcreteSyntaxTree collectionWriter, ConcreteSyntaxTree wr) {
      // The only remaining caller that reaches here is the assign-such-that
      // (`x :| P(x)`) statement/expression search, whose lowering (C++ range-for
      // + throw + doubling iterLimit) is not adapted to C. Reject cleanly rather
      // than leak invalid C++. (Set/map comprehensions, quantifiers and forall
      // are already rejected earlier as their own unsupported features.)
      throw new UnsupportedFeatureException(tok ?? Token.NoToken, Feature.LetSuchThatExpressions,
        "assign-such-that (':|') is not supported by the C backend");
    }

    [CanBeNull]
    protected override Action<ConcreteSyntaxTree> GetSubtypeCondition(string tmpVarName, Type boundVarType, IOrigin tok, ConcreteSyntaxTree wPreconditions) {
      string typeTest;
      if (boundVarType.IsRefType) {
        if (boundVarType.IsObject || boundVarType.IsObjectQ) {
          typeTest = "true";
        } else {
          // A runtime subtype test on a trait/class bound variable. The inherited
          // body emitted C++/Go pseudocode ("typeid(...) is typeid(...)",
          // "_dafny.InstanceOfTrait") that is not C, and the C backend has no RTTI.
          // Traits/type-tests are already rejected as their own features, so this
          // is unreached; reject cleanly rather than leak invalid C.
          throw new UnsupportedFeatureException(tok ?? Token.NoToken, Feature.TypeTests,
            "runtime subtype tests on reference types are not supported by the C backend");
        }
        if (boundVarType.IsNonNullRefType) {
          typeTest = $"{tmpVarName} != null && {typeTest}";
        } else {
          typeTest = $"{tmpVarName} == null || {typeTest}";
        }
      } else {
        typeTest = "true";
      }

      typeTest = typeTest == "true" ? null : typeTest;
      return typeTest == null ? null : wr => wr.Write(typeTest);
    }

    protected override void EmitDowncastVariableAssignment(string boundVarName, Type boundVarType, string tmpVarName,
      Type sourceType, bool introduceBoundVar, IOrigin tok, ConcreteSyntaxTree wr) {
      var typeName = TypeName(boundVarType, wr, tok);
      wr.WriteLine("{0}{1} = ({2}){3};", introduceBoundVar ? typeName + " " : "", boundVarName, typeName, tmpVarName);
    }

    protected override ConcreteSyntaxTree CreateForeachIngredientLoop(string boundVarName, int L, string tupleTypeArgs, out ConcreteSyntaxTree collectionWriter, ConcreteSyntaxTree wr) {
      // Reached only by the "ingredient" lowering of a non-sequentializable forall
      // update, which is already rejected (Feature.NonSequentializableForallStatements).
      // The inherited body emits a C++ range-for ("for (auto x : ...)"), not valid C;
      // reject rather than leave the landmine.
      throw new UnsupportedFeatureException(Token.NoToken, Feature.NonSequentializableForallStatements);
    }

    // ----- Expressions -------------------------------------------------------------

    protected override void EmitNew(Type type, IOrigin tok, CallStmt initCall /*?*/, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      var cl = (type.NormalizeExpand() as UserDefinedType)?.ResolvedClass;
      if (cl != null && cl.Name == "object") {
        //wr.Write("_dafny.NewObject()");
        throw new UnsupportedFeatureException(tok, Feature.NewObject);
      } else if (cl != null && IsReferenceClass(cl)) {
        // A reference class is a heap struct. Allocate zero-initialized storage
        // (arena/leak model: never freed). calloc zeroes the fields so any field
        // not set by the constructor has a defined (zero) value. The constructor
        // is invoked separately by TrRhs via TrCallStmt(initCall, _nw, ...), which
        // passes _nw as the explicit `this` argument (custom receiver).
        var name = TypeName(type, wr, tok, null, true);  // bare NAME (no pointer)
        wr.Write("({0}*)calloc(1, sizeof({0}))", name);
      } else {
        // Reference-class allocation goes through the calloc branch above; anything
        // else here would be an extern/`shared_ptr`-style construction the C backend
        // does not support. (The C++ backend emitted std::make_shared here.)
        throw new UnsupportedFeatureException(tok, Feature.ExternalClasses);
      }
    }

    protected override void EmitNewArray(Type elementType, IOrigin tok, List<Expression> dimensions,
        bool mustInitialize, [CanBeNull] string exampleElement, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      if (dimensions.Count == 1) {
        // 1-D array<T>: monomorphised allocator. The allocator ALWAYS zero-inits
        // the backing store to the element type's default (matching how C#/Dafny
        // zero-init a freshly-allocated array), so mustInitialize needs no extra
        // handling. `exampleElement` is always null for this backend
        // (DeterminesArrayTypeFromExampleElement is false); an array-init lambda
        // `new T[n](i => f(i))` is a function value, cleanly rejected when the
        // element-init loop body is emitted.
        var suffix = RegisterArrayElementType(elementType);
        // The allocator takes a size_t; the dimension has Dafny type `int`
        // (a DafnyInt) — or a native newtype — so convert down to a native size.
        wr.Write($"dafny_array_{suffix}_new(");
        EmitIndexAsSize(dimensions[0], false, wr, wStmts);
        wr.Write(")");
      } else {
        throw new UnsupportedFeatureException(tok, Feature.MultiDimensionalArrays);
      }
    }

    // The string-based overload is unused: this backend keeps the
    // Expression-based dimension so the size can be correctly converted per its
    // Dafny type (GMP int vs native newtype). It must still be implemented (it is
    // abstract) — route it to the Expression overload's shape by rejecting the
    // multi-dim case and otherwise treating the already-emitted string as a
    // Dafny-int size expression. In practice the Expression overload above is
    // always the one invoked (EmitNewArray(List<Expression>) is called directly).
    protected override void EmitNewArray(Type elementType, IOrigin tok, List<string> dimensions,
        bool mustInitialize, [CanBeNull] string exampleElement, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      if (dimensions.Count == 1) {
        var suffix = RegisterArrayElementType(elementType);
        wr.Write($"dafny_array_{suffix}_new(dafny_int_to_u64({dimensions[0]}))");
      } else {
        throw new UnsupportedFeatureException(tok, Feature.MultiDimensionalArrays);
      }
    }

    protected override void EmitLiteralExpr(ConcreteSyntaxTree wr, LiteralExpr e) {
      if (e is StaticReceiverExpr) {
        wr.Write(TypeName(e.Type, wr, e.Origin));
      } else if (e.Value == null) {
        EmitNullText(e.Type, wr);
      } else if (e.Value is bool) {
        wr.Write((bool)e.Value ? "true" : "false");
      } else if (e is CharLiteralExpr) {
        // A Dafny char is a dafny_char code point (see string literals). A C
        // char literal 'x' only works for ASCII; for code points > 127 it is
        // ill-formed. Emit the numeric code point instead, which is always
        // valid and matches how strings encode their characters.
        var v = (string)e.Value;
        var cps = Util.UnescapedCharacters(Options, v, false).ToList();
        // A char literal always denotes exactly one code point.
        wr.Write("((dafny_char)0x{0:X})", cps.Count > 0 ? cps[0] : 0);
      } else if (e is StringLiteralExpr) {
        var str = (StringLiteralExpr)e;
        // A Dafny string is seq<char>, i.e. DafnySequence_char, whose element
        // type is dafny_char (a 32-bit code point). We emit the literal as an
        // explicit array of code points, NOT a C string literal: a C string
        // literal of a non-ASCII string is its multi-byte UTF-8 encoding, whose
        // BYTE length != the Dafny character count, which would corrupt both the
        // stored length (|s|) and printing. Emitting code points keeps |s| equal
        // to the Dafny character count and lets printing UTF-8-encode correctly.
        RegisterSeqElementType(Type.Char);
        var codePoints = Util.UnescapedCharacters(Options, (string)str.Value, str.IsVerbatim).ToList();
        if (codePoints.Count == 0) {
          // Empty seq: NULL data. A zero-length C array literal is ill-formed.
          wr.Write("dafny_seq_char_create(0, NULL)");
        } else {
          wr.Write("dafny_seq_char_create({0}, (dafny_char[]){{", codePoints.Count);
          for (var ci = 0; ci < codePoints.Count; ci++) {
            if (ci != 0) {
              wr.Write(", ");
            }
            wr.Write("0x{0:X}", codePoints[ci]);
          }
          wr.Write("})");
        }
      } else if (AsNativeType(e.Type) is NativeType nt) {
        wr.Write("({0}){1}", GetNativeTypeName(nt), (BigInteger)e.Value);
        if ((BigInteger)e.Value > 9223372036854775807) {
          // Avoid compiler warning: integer literal is too large to be represented in a signed integer type
          wr.Write("U");
        }
      } else if (e.Value is BigInteger i) {
        // An unbounded-int literal (native-int literals were handled above). GMP is
        // c-extended only.
        RejectIfMinimal(Feature.UnboundedIntegers, e.Origin);
        EmitIntegerLiteral(i, wr);
      } else if (e.Value is BaseTypes.BigDec n) {
        // Dafny real literal = exact rational. BigDec is mantissa * 10^exponent.
        // Convert to numerator/denominator strings for dafny_real_from_frac.
        // e.g. 1.5 -> mantissa 15, exponent -1 -> 15 / 10. GMP is c-extended only.
        RejectIfMinimal(Feature.RealNumbers, e.Origin);
        BigInteger num = n.Mantissa;
        BigInteger den = BigInteger.One;
        if (n.Exponent >= 0) {
          num *= BigInteger.Pow(10, n.Exponent);
        } else {
          den = BigInteger.Pow(10, -n.Exponent);
        }
        wr.Write("dafny_real_from_frac(\"{0}\", \"{1}\")", num, den);
      } else {
        Contract.Assert(false); throw new Cce.UnreachableException();  // unexpected literal
      }
    }
    void EmitIntegerLiteral(BigInteger i, ConcreteSyntaxTree wr) {
      Contract.Requires(wr != null);
      // Unbounded Dafny int literal -> GMP-backed DafnyInt. Values within int64
      // use the cheap i64 constructor; larger magnitudes use the decimal-string
      // constructor so arbitrary precision is preserved.
      if (i >= long.MinValue && i <= long.MaxValue) {
        wr.Write("dafny_int_from_i64({0}LL)", i);
      } else {
        wr.Write("dafny_int_from_str(\"{0}\")", i);
      }
    }

    protected override void EmitStringLiteral(string str, bool isVerbatim, ConcreteSyntaxTree wr) {
      var n = str.Length;
      if (!isVerbatim) {
        wr.Write($"\"{TranslateEscapes(str)}\"");
      } else {
        wr.Write("\"");
        for (var i = 0; i < n; i++) {
          if (str[i] == '\"' && i + 1 < n && str[i + 1] == '\"') {
            wr.Write("\\\"");
            i++;
          } else if (str[i] == '\\') {
            wr.Write("\\\\");
          } else if (str[i] == '\n') {
            wr.Write("\\n");
          } else if (str[i] == '\r') {
            wr.Write("\\r");
          } else {
            wr.Write(str[i]);
          }
        }
        wr.Write("\"");
      }

      // C: a plain string literal (no C++ `"..."s` suffix). The length is passed
      // explicitly by the caller (dafny_seq_char_create), so embedded NULs are
      // fine even though the literal itself is null-terminated.
    }

    private static string TranslateEscapes(string s) {
      s = Util.ReplaceNullEscapesWithCharacterEscapes(s);
      // TODO: Other cases, once we address the fact that we shouldn't be
      // using the C++ char as the Dafny 16-bit char in the first place.
      return s;
    }

    protected override ConcreteSyntaxTree EmitBitvectorTruncation(BitvectorType bvType, [CanBeNull] NativeType nativeType,
      bool surroundByUnchecked, ConcreteSyntaxTree wr) {
      string literalSuffix = null;
      if (nativeType != null) {
        GetNativeInfo(nativeType.Sel, out _, out literalSuffix, out _);
      }

      if (nativeType == null) {
        // A bitvector wider than any native C integer would need a GMP-backed
        // big-integer representation with explicit width masking, which the C
        // backend does not implement. Reject cleanly rather than emit bad C.
        throw new UnsupportedFeatureException(Token.NoToken, Feature.NonNativeNewtypes,
          "bitvectors wider than 64 bits (big-integer-backed) are not supported by the C backend");
      } else if (bvType.Width == 0) {
        // bv0: the single value 0.
        wr.Write("0");
        return new ConcreteSyntaxTree();  // discard the operand
      } else if (bvType.Width < nativeType.Bitwidth || bvType.Width < 64) {
        // Mask to the bitvector width. This is needed not only when the bv is
        // NARROWER than its native C type (e.g. bv7 in a uint8), but ALSO for a
        // full-width sub-64-bit bv (bv8/bv16/bv32): C integer promotion computes
        // `a + b` as `int`, so a full-width bv8 sum of 200+100 is 300, not 44,
        // unless masked. (bv64 fills uint64, whose arithmetic already wraps mod
        // 2^64, so no mask is emitted — and `1ULL << 64` would be UB.)
        wr.Write("((");
        var middle = wr.Fork();
        wr.Write(") & 0x{0:X}{1})", (1UL << bvType.Width) - 1, literalSuffix);
        return middle;
      } else {
        // bv64: uint64 arithmetic already wraps mod 2^64. No mask needed.
        return wr;
      }
    }

    protected override void EmitRotate(Expression e0, Expression e1, bool isRotateLeft, ConcreteSyntaxTree wr,
      bool inLetExprBody, ConcreteSyntaxTree wStmts, FCE_Arg_Translator tr) {
      throw new UnsupportedFeatureException(e0.Origin, Feature.BitvectorRotateFunctions);
    }

    protected override void EmitEmptyTupleList(string tupleTypeArgs, ConcreteSyntaxTree wr) {
      throw new UnsupportedFeatureException(Token.NoToken, Feature.NonSequentializableForallStatements);
    }

    protected override ConcreteSyntaxTree EmitAddTupleToList(string ingredients, string tupleTypeArgs, ConcreteSyntaxTree wr) {
      throw new UnsupportedFeatureException(Token.NoToken, Feature.NonSequentializableForallStatements);
    }

    protected override void EmitTupleSelect(string prefix, int i, ConcreteSyntaxTree wr) {
      // Element access on a monomorphised tuple struct: field ._i .
      wr.Write("({0})._{1}", prefix, i);
    }

    protected override string IdProtect(string name) {
      return PublicIdProtect(name);
    }

    public override string PublicIdProtect(string name) {
      Contract.Requires(name != null);
      switch (name) {
        // Taken from: https://www.w3schools.in/cplusplus-tutorial/keywords/
        // Keywords
        case "asm":
        case "auto":
        case "bool":
        case "break":
        case "case":
        case "catch":
        case "char":
        case "class":
        case "const":
        case "const_cast":
        case "continue":
        case "default":
        case "delete":
        case "do":
        case "double":
        case "dynamic_cast":
        case "else":
        case "enum":
        case "explicit":
        case "export":
        case "extern":
        case "false":
        case "float":
        case "for":
        case "friend":
        case "goto":
        case "if":
        case "inline":
        case "int":
        case "long":
        case "mutable":
        case "namespace":
        case "new":
        case "operator":
        case "private":
        case "public":
        case "register":
        case "reinterpret_cast":
        case "return":
        case "short":
        case "signed":
        case "sizeof":
        case "static":
        case "static_cast":
        case "struct":
        case "switch":
        case "template":
        case "this":
        case "throw":
        case "true":
        case "try":
        case "typedef":
        case "typeid":
        case "typename":
        case "union":
        case "unsigned":
        case "using":
        case "virtual":
        case "void":
        case "volatile":
        case "wchar_t":
        case "while":

        // Also reserved
        case "And":
        case "and_eq":
        case "bitand":
        case "bitor":
        case "compl":
        case "not":
        case "not_eq":
        case "or":
        case "or_eq":
        case "xor":
        case "xor_eq":
          return name + "_";
        default:
          return name;
      }
    }

    protected override string FullTypeName(UserDefinedType udt, MemberDecl/*?*/ member = null) {
      Contract.Assume(udt != null);  // precondition; this ought to be declared as a Requires in the superclass
      if (udt is ArrowType) {
        // Function/arrow value — unsupported by the C backend.
        throw new UnsupportedFeatureException(udt.Origin, Feature.FunctionValues);
      }
      var cl = udt.ResolvedClass;
      if (cl is TypeParameter) {
        return IdProtect(udt.GetCompileName(Options));
      } else if (cl is DefaultClassDecl && Attributes.Contains(cl.EnclosingModuleDefinition.Attributes, "extern") &&
                 member != null && Attributes.Contains(member.Attributes, "extern")) {
        // omit the default class name ("_default") in extern modules, when the class is used to qualify an extern member
        Contract.Assert(!cl.EnclosingModuleDefinition.IsDefaultModule); // default module is not marked ":extern"
        return IdProtect(cl.EnclosingModuleDefinition.GetCompileName(Options));
      } else if (Attributes.Contains(cl.Attributes, "extern")) {
        return Scope(IdProtect(cl.EnclosingModuleDefinition.GetCompileName(Options)), IdProtect(cl.Name));
      } else if (cl is TupleTypeDecl) {
        // Tuple types (including the unit type ()) have no C representation in
        // this backend; the C++ `Tuple<...>` template does not exist here.
        throw new UnsupportedFeatureException(udt.Origin, Feature.TupleInitialization,
          "tuple types are not supported by the C backend");
      } else {
        return Scope(IdProtect(cl.EnclosingModuleDefinition.GetCompileName(Options)), IdProtect(cl.GetCompileName(Options)));
      }
    }

    protected override void EmitThis(ConcreteSyntaxTree wr, bool callToInheritedMember) {
      wr.Write("this");
    }

    protected override void EmitDatatypeValue(DatatypeValue dtv, string typeDescriptorArguments, string arguments, ConcreteSyntaxTree wr) {
      EmitDatatypeValue(dtv, dtv.Ctor, dtv.IsCoCall, arguments, wr);
    }

    void EmitDatatypeValue(DatatypeValue dtv, DatatypeCtor ctor, bool isCoCall, string arguments, ConcreteSyntaxTree wr) {
      var dt = dtv.Ctor.EnclosingDatatype;
      var dtName = dt.GetCompileName(Options);
      var ctorName = ctor.GetCompileName(Options);

      if (dt is TupleTypeDecl) {
        // A tuple literal `(a, b, ...)` -> a C compound literal of the
        // monomorphised struct: (DafnyTuple_...){ ._0 = a, ._1 = b, ... }.
        // Element types come from the literal's NON-GHOST arguments; this maps to
        // the SAME struct as a same-shape multiple-return value. The pre-joined
        // `arguments` string was already built from the non-ghost args by the
        // base generator, so the struct shape must match by also dropping ghosts.
        var tupleDecl = (TupleTypeDecl)dt;
        var elemTypes = new List<Type>();
        for (var i = 0; i < dtv.Arguments.Count; i++) {
          if (!tupleDecl.ArgumentGhostness[i]) {
            elemTypes.Add(dtv.Arguments[i].Type);
          }
        }
        var structName = RegisterTupleReturn(elemTypes);
        if (elemTypes.Count == 0) {
          // The unit value (): the struct has only a dummy field.
          wr.Write("({0}){{ 0 }}", structName);
        } else {
          // Positional C compound literal: the pre-joined `arguments` is already
          // the field values in order (._0, ._1, ...). Positional init avoids
          // splitting the string, which would be wrong if an argument is itself
          // a nested tuple literal containing ", ".
          wr.Write("({0}){{ {1} }}", structName, arguments);
        }
      } else if (!isCoCall) {
        // Ordinary constructor. C tagged union: call the monomorphised
        // NAME_create_Ctor(arguments) function, where NAME is the mangled
        // instantiation of this datatype value's type (e.g. _module_Option_bool).
        var udt = (UserDefinedType)dtv.Type.NormalizeExpand();
        var instName = RegisterDatatypeInstance(udt);
        wr.Write("{0}_create_{1}({2})", instName, ctorName, arguments);
      } else {
        // Co-recursive call
        // Generate:  Dt.lazy_Ctor(($dt) => Dt.create_Ctor($dt, args))
        wr.Write("{0}.lazy_{1}(($dt) => ", dtName, ctorName);
        wr.Write("{0}.create_{1}($dt{2}{3})", dtName, ctorName, arguments.Length == 0 ? "" : ", ", arguments);
        wr.Write(")");
      }
    }

    protected override void GetSpecialFieldInfo(SpecialField.ID id, object idParam, Type receiverType, out string compiledName, out string preString, out string postString) {
      compiledName = "";
      preString = "";
      postString = "";
      switch (id) {
        case SpecialField.ID.UseIdParam:
          compiledName = (string)idParam;
          break;
        case SpecialField.ID.ArrayLength:
        case SpecialField.ID.ArrayLengthInt:
          // 1-D array<T> length: the `len` field of the {data, len} struct. This
          // yields a native size_t (used e.g. as a loop bound by EmitArrayLength).
          // The `a.Length` MEMBER-SELECT expression is handled specially in
          // EmitMemberSelect (wrapping in dafny_int_from_size to yield a DafnyInt).
          compiledName = "len";
          break;
        case SpecialField.ID.Floor:
          // real.Floor: wrap the receiver in the GMP flooring conversion. The
          // member itself contributes nothing (compiledName empty); the
          // pre/post strings surround the emitted (parenthesized) receiver.
          preString = "dafny_int_from_real(";
          postString = ")";
          break;
        case SpecialField.ID.IsLimit:
          throw new UnsupportedFeatureException(Token.NoToken, Feature.Ordinals);
        case SpecialField.ID.IsSucc:
          throw new UnsupportedFeatureException(Token.NoToken, Feature.Ordinals);
        case SpecialField.ID.Offset:
          throw new UnsupportedFeatureException(Token.NoToken, Feature.Ordinals);
        case SpecialField.ID.IsNat:
          throw new UnsupportedFeatureException(Token.NoToken, Feature.Ordinals);
        case SpecialField.ID.Keys:
        case SpecialField.ID.Values:
          // Handled entirely in EmitMemberSelect (it builds the set by iterating
          // the map's hash table); compiledName is never consulted for these. The
          // inherited C++ method names (dafnyKeySet()/dafnyValues()) were dead.
          break;
        case SpecialField.ID.Items:
          throw new UnsupportedFeatureException(Token.NoToken, Feature.MapItems);
        case SpecialField.ID.Reads:
          compiledName = "_reads";
          break;
        case SpecialField.ID.Modifies:
          compiledName = "_modifies";
          break;
        case SpecialField.ID.New:
          compiledName = "_new";
          break;
        default:
          Contract.Assert(false); // unexpected ID
          break;
      }
    }

    protected override ILvalue EmitMemberSelect(Action<ConcreteSyntaxTree> obj, Type objType, MemberDecl member, List<TypeArgumentInstantiation> typeArgs, Dictionary<TypeParameter, Type> typeMap,
      Type expectedType, string/*?*/ additionalCustomParameter = null, bool internalAccess = false) {
      if (member.IsStatic && member is ConstantField) {
        // This used to work, but now obj comes in wanting to use TypeName on the class, which results in (std::shared_ptr<_module::MyClass>)::c;
        //return SuffixLvalue(obj, "::{0}", member.CompileName);
        return SimpleLvalue(wr => {
          wr.Write(Scope(IdProtect(member.EnclosingClass.EnclosingModuleDefinition.GetCompileName(Options)), IdProtect(clName(member.EnclosingClass)), IdProtect(member.GetCompileName(Options))));
        });
      } else if (member is DatatypeDestructor dtor && dtor.EnclosingClass is TupleTypeDecl) {
        // Tuple element access `t.i` -> field access `(t)._j`. The destructor
        // `Name` is the ORIGINAL Dafny index (which counts ghost components too),
        // but the C struct only has the NON-GHOST fields. NameForCompilation of
        // the corresponding formal is exactly that non-ghost field index (e.g.
        // for (a, ghost b, c): .0 -> ._0, .2 -> ._1). Ghost indices are never
        // accessed in compiled code, so this only ever sees non-ghost formals.
        Contract.Assert(dtor.CorrespondingFormals.Count == 1);
        var tupleFormal = dtor.CorrespondingFormals[0];
        return SuffixLvalue(obj, "._{0}", tupleFormal.NameForCompilation);
      } else if (member is DatatypeDiscriminator { IdParam: string fieldName } discriminator && fieldName.StartsWith("is_")) {
        // Datatype query `d.Ctor?`. C tagged union: compare the tag against the
        // shared per-datatype tag constant (baseName_TAG_Ctor). The tag enum does
        // not depend on the concrete type arguments, so no instantiation is
        // needed here. (The corresponding constructor name is fieldName[3..].)
        var baseName = DatatypeBaseName((DatatypeDecl)discriminator.EnclosingClass);
        return SuffixLvalue(obj, ".tag == {0}_TAG_{1}", baseName, fieldName.Substring(3));
      } else if (member is SpecialField sf) {
        GetSpecialFieldInfo(sf.SpecialId, sf.IdParam, objType, out var compiledName, out var preStr, out var postStr);
        if (sf.SpecialId == SpecialField.ID.ArrayLength || sf.SpecialId == SpecialField.ID.ArrayLengthInt) {
          // a.Length on a 1-D array<T>: the array is a {data, len} struct value,
          // so read the `len` field. Dafny's .Length has type `int`, so wrap the
          // size_t length in dafny_int_from_size to yield a DafnyInt.
          return SimpleLvalue(wr => {
            wr.Write("dafny_int_from_size((");
            obj(wr);
            wr.Write(").len)");
          });
        }
        if (sf.SpecialId == SpecialField.ID.Keys || sf.SpecialId == SpecialField.ID.Values) {
          // m.Keys / m.Values -> a set<K> / set<V>. The inherited ".dafnyKeySet()"
          // was a C++ method. Build the set by iterating the map's hash table and
          // inserting each key (or value) into a fresh DafnySet_<elem>, via a GNU
          // statement expression. Requires the element type to be set-hashable.
          var mapType = objType.NormalizeToAncestorType().AsMapType;
          var isKeys = sf.SpecialId == SpecialField.ID.Keys;
          var elemType = isKeys ? mapType.Domain : mapType.Range;
          var mapSuffix = RegisterMapType(mapType.Domain, mapType.Range);
          var setSuffix = RegisterSetElementType(elemType);
          var field = isKeys ? "keys" : "vals";
          return SimpleLvalue(wr => {
            wr.Write("({{ DafnyMap_{0} _m = ", mapSuffix);
            obj(wr);
            // Size the set for the map's entry count so linear-probing insert always
            // has a free slot (dafny_set_<e>_alloc + cap_for, matching _create).
            wr.Write("; DafnySet_{0} _s = dafny_set_{0}_alloc(dafny_set_{0}_cap_for(_m.len)); ", setSuffix);
            wr.Write(string.Format("for (size_t _i = 0; _i < _m.cap; _i++) {{ if (_m.used[_i]) {{ dafny_set_{0}_insert(&_s, _m.{1}[_i]); }} }} ", setSuffix, field));
            wr.Write("_s; })");   // raw single-arg Write: no format processing, literal braces
          });
        } else if (sf is DatatypeDestructor dtor2) {
          if (!(dtor2.EnclosingClass is IndDatatypeDecl)) {
            // Destructor on a codatatype (lazy/infinite) — unsupported. Throw so we
            // stop here rather than fall through with a null `dt` and crash.
            throw new UnsupportedFeatureException(dtor2.Origin, Feature.Codatatypes,
              string.Format("destructor {0} on a codatatype is not supported by the C backend", member.Name));
          }

          var dt = dtor2.EnclosingClass as IndDatatypeDecl;
          return SimpleLvalue(wr => {
            if (dt.Ctors.Count > 1) {
              // C tagged union: read the field out of the union member for the
              // constructor this destructor belongs to: (obj).val.Ctor.field.
              // (If the destructor is shared by several constructors, use the
              // first; that is correct when they share the field position.)
              var ctor = dtor2.EnclosingCtors[0];
              // A boxed (self-referential) field is stored as a pointer: deref it.
              var boxed = IsBoxedField(EnclosingDatatypeInstName(ctor), dtor2.Type);
              wr.Write("({0}", boxed ? "*" : "");
              obj(wr);
              wr.Write(".val.{0}.{1}", ctor.GetCompileName(Options), sf.GetCompileName(Options));
            } else {
              var ctor = dtor2.EnclosingCtors[0];
              var boxed = IsBoxedField(EnclosingDatatypeInstName(ctor), dtor2.Type);
              // Single constructor (record type): fields live at the top level.
              wr.Write("({0}", boxed ? "*" : "");
              obj(wr);
              wr.Write(".{0}", sf.GetCompileName(Options));
            }

            wr.Write(")");
          });
        } else if (!member.IsStatic && compiledName.Length != 0) {
          return SuffixLvalue(obj, "->{0}", compiledName);
        } else if (compiledName.Length != 0) {
          // Static special field with a compiled name. The inherited C++ backend
          // emitted C++ scope resolution (`obj::name`), which is not valid C. No
          // supported C-subset construct reaches here (static specials are handled
          // as ConstantFields above), so reject rather than emit broken C.
          throw new UnsupportedFeatureException(member.Origin, Feature.ExternalClasses,
            string.Format("static special field {0} is not supported by the C backend", member.Name));
        } else {
          // this member selection is handled by some kind of enclosing function call, so nothing to do here
          return SimpleLvalue(obj);
        }
      } else if (member is Function) {
        return StringLvalue(Scope(
          IdProtect(member.EnclosingClass.EnclosingModuleDefinition.GetCompileName(Options)),
          IdName(member.EnclosingClass),
          IdName(member)
        ));
      } else {
        return SuffixLvalue(obj, "->{0}", IdName(member));
      }
    }

    protected override ConcreteSyntaxTree EmitArraySelect(List<Action<ConcreteSyntaxTree>> indices, Type elmtType, ConcreteSyntaxTree wr) {
      // 1-D array element access a[i] -> struct field read (a).data[i]. The array
      // value is a {data, len} struct; the forked `w` receives the array
      // expression. `indices` are already the native array-index type (converted
      // by ArrayIndexToNativeInt / EmitExprAsNativeInt). This same shape is used
      // as an lvalue by EmitArrayUpdate (base appends " = rhs"), so a[i] := v
      // becomes (a).data[i] = v.
      Contract.Assert(indices != null && indices.Count == 1);  // 1-D only
      wr.Write("(");
      var w = wr.Fork();
      wr.Write(").data[");
      indices[0](wr);
      wr.Write("]");
      return w;
    }

    protected override ConcreteSyntaxTree EmitArraySelect(List<Expression> indices, Type elmtType, bool inLetExprBody,
        ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      Contract.Assert(indices != null && indices.Count == 1);  // 1-D only
      wr.Write("(");
      var w = wr.Fork();
      wr.Write(").data[");
      // The index has Dafny type `int` (a DafnyInt); the backing array is indexed
      // by size_t, so convert the unbounded index down to a native size.
      EmitIndexAsSize(indices[0], inLetExprBody, wr, wStmts);
      wr.Write("]");
      return w;
    }

    protected override void EmitExprAsNativeInt(Expression expr, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      // A "native int" is a size_t here (used for array/seq indices). A Dafny
      // `int` expression is a DafnyInt (GMP), so convert it down; a native
      // newtype index passes through unchanged.
      EmitIndexAsSize(expr, inLetExprBody, wr, wStmts);
    }

    // Array-index conversion for the Action-based (lvalue / already-stringified)
    // path: the string is an already-emitted index expression of type
    // `fromType`. When that is a GMP `int`, wrap it in dafny_int_to_u64 so it
    // indexes the size_t-indexed backing store; a native newtype passes through.
    protected override string ArrayIndexToNativeInt(string arrayIndex, Type fromType) {
      if (IsGmpInt(fromType)) {
        return $"dafny_int_to_u64({arrayIndex})";
      }
      return arrayIndex;
    }

    protected override void EmitIndexCollectionSelect(Expression source, Expression index, bool inLetExprBody,
        ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      if (source.Type.NormalizeToAncestorType() is SeqType st) {
        // seq index s[i]: monomorphised free function dafny_seq_<elem>_select.
        // The index has Dafny type `int` (a DafnyInt); the helper takes size_t,
        // so convert the unbounded index down to a native size.
        var suffix = RegisterSeqElementType(st.Arg);
        wr.Write("dafny_seq_{0}_select(", suffix);
        wr.Append(Expr(source, inLetExprBody, wStmts));
        wr.Write(", ");
        EmitIndexAsSize(index, inLetExprBody, wr, wStmts);
        wr.Write(")");
        return;
      }
      if (source.Type.NormalizeToAncestorType() is MultiSetType ms) {
        // multiset multiplicity m[x]: the count of x in m. In Dafny m[x] has type
        // `int` (unbounded); dafny_multiset_<elem>_count returns a native size_t,
        // so wrap it in dafny_int_from_size to yield a DafnyInt.
        var suffix = RegisterMultisetElementType(ms.Arg);
        wr.Write("dafny_int_from_size(dafny_multiset_{0}_count(", suffix);
        wr.Append(Expr(source, inLetExprBody, wStmts));
        wr.Write(", ");
        wr.Append(Expr(index, inLetExprBody, wStmts));
        wr.Write("))");
        return;
      }
      if (source.Type.NormalizeToAncestorType() is MapType mt) {
        // map lookup m[k]: monomorphised dafny_map_<k>_<v>_get.
        var suffix = RegisterMapType(mt.Domain, mt.Range);
        wr.Write("dafny_map_{0}_get(", suffix);
        wr.Append(Expr(source, inLetExprBody, wStmts));
        wr.Write(", ");
        wr.Append(Expr(index, inLetExprBody, wStmts));
        wr.Write(")");
        return;
      }
      TrParenExpr(source, wr, inLetExprBody, wStmts);
      {
        // imap (unsupported spelling retained)
        wr.Write(".get(");
        wr.Append(Expr(index, inLetExprBody, wStmts));
        wr.Write(")");
      }
    }

    protected override void EmitIndexCollectionUpdate(Expression source, Expression index, Expression value,
        CollectionType resultCollectionType, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      var srcType = source.Type.NormalizeToAncestorType();
      if (srcType is MapType mt) {
        // map update m[k := v]: monomorphised dafny_map_<k>_<v>_update (new map).
        var suffix = RegisterMapType(mt.Domain, mt.Range);
        wr.Write("dafny_map_{0}_update(", suffix);
        wr.Append(Expr(source, inLetExprBody, wStmts));
        wr.Write(", ");
        wr.Append(Expr(index, inLetExprBody, wStmts));
        wr.Write(", ");
        wr.Append(CoercedExpr(value, resultCollectionType.ValueArg, inLetExprBody, wStmts));
        wr.Write(")");
        return;
      }
      if (srcType is SeqType seqt) {
        // seq update s[i := v]: monomorphised dafny_seq_<elem>_update (fresh copy).
        // The index is a Dafny nat (unbounded int / DafnyInt); narrow to size_t.
        var suffix = RegisterSeqElementType(seqt.Arg);
        wr.Write("dafny_seq_{0}_update(", suffix);
        wr.Append(Expr(source, inLetExprBody, wStmts));
        wr.Write(", (size_t)dafny_int_to_u64(");
        wr.Append(Expr(index, inLetExprBody, wStmts));
        wr.Write("), ");
        wr.Append(CoercedExpr(value, resultCollectionType.ValueArg, inLetExprBody, wStmts));
        wr.Write(")");
        return;
      }
      // multiset update m[x := count] and any other collection update are not
      // wired; reject cleanly rather than emit an invalid C++ ".update()" call.
      throw new UnsupportedFeatureException(source.Origin, Feature.MapItems,
        "this collection update is not supported by the C backend");
    }

    protected override void EmitSeqSelectRange(Expression source, Expression lo /*?*/, Expression hi /*?*/,
        bool fromArray, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      if (fromArray) {
        // Array-to-seq conversion a[..] / a[lo..hi]: copy the requested slice of
        // the array's backing store into a fresh DafnySequence_<elem>. The array
        // is a {data, len} struct; evaluate it once into a temp (GNU statement
        // expression) so a general array expression isn't re-evaluated, then call
        // dafny_seq_<elem>_from_array on (data + lo, hi - lo). Missing bounds
        // default to lo=0 / hi=len.
        Type elemType;
        if (source.Type.TypeArgs.Count == 0 && source.Type is UserDefinedType udt && udt.ResolvedClass != null &&
            udt.ResolvedClass is TypeSynonymDecl tsd) {
          // Type synonym wrapped around the array type.
          elemType = tsd.Rhs.TypeArgs[0];
        } else {
          elemType = source.Type.TypeArgs[0];
        }
        var elemSuffix = RegisterSeqElementType(elemType);
        var arrSuffix = RegisterArrayElementType(elemType);
        wr.Write("({{ DafnyArray_{0} _arr = ", arrSuffix);
        wr.Append(Expr(source, inLetExprBody, wStmts));
        wr.Write("; size_t _lo = ");
        if (lo == null) {
          wr.Write("0");
        } else {
          EmitIndexAsSize(lo, inLetExprBody, wr, wStmts);
        }
        wr.Write("; size_t _hi = ");
        if (hi == null) {
          wr.Write("_arr.len");
        } else {
          EmitIndexAsSize(hi, inLetExprBody, wr, wStmts);
        }
        wr.Write("; dafny_seq_{0}_from_array(_arr.data + _lo, _hi - _lo); }})", elemSuffix);
      } else {
        // seq slice s[lo..hi]: monomorphised take (upper bound) then drop (lower
        // bound). Both produce a new DafnySequence_<elem>. Nest the calls so the
        // drop is applied to the result of the take.
        var suffix = RegisterSeqElementType(source.Type.NormalizeToAncestorType().AsSeqType.Arg);
        if (lo != null) {
          wr.Write("dafny_seq_{0}_drop(", suffix);
        }
        if (hi != null) {
          wr.Write("dafny_seq_{0}_take(", suffix);
        }
        wr.Append(Expr(source, inLetExprBody, wStmts));
        if (hi != null) {
          wr.Write(", ");
          EmitIndexAsSize(hi, inLetExprBody, wr, wStmts);
          wr.Write(")");
        }
        if (lo != null) {
          wr.Write(", ");
          EmitIndexAsSize(lo, inLetExprBody, wr, wStmts);
          wr.Write(")");
        }
      }
    }

    // Emit a collection index/bound as a native size_t. Dafny indices have type
    // `int`, which this backend represents as a GMP DafnyInt; the seq helpers take
    // size_t, so convert unbounded ints down. Native-int indices pass through.
    private void EmitIndexAsSize(Expression index, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      if (IsGmpInt(index.Type)) {
        wr.Write("dafny_int_to_u64(");
        wr.Append(Expr(index, inLetExprBody, wStmts));
        wr.Write(")");
      } else {
        wr.Append(Expr(index, inLetExprBody, wStmts));
      }
    }

    protected override void EmitSeqConstructionExpr(SeqConstructionExpr expr, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      // seq(n, f) initializes each element via the function value `f`, which the C
      // backend does not support (no expression-level lambdas).
      throw new UnsupportedFeatureException(expr.Origin, Feature.FunctionValues);
    }

    protected override void EmitMultiSetFormingExpr(MultiSetFormingExpr expr, bool inLetExprBody, ConcreteSyntaxTree wr,
      ConcreteSyntaxTree wStmts) {
      var srcType = expr.E.Type.NormalizeToAncestorType();
      if (srcType is SeqType seqSrc) {
        // multiset(s) for a sequence s: count each element. A seq value is a
        // contiguous {data, len} pair, so feed its backing array straight into
        // dafny_multiset_<elem>_create (which counts duplicates). Evaluate the
        // source into a temp via a GNU statement expression so it is computed
        // once even though both its .data and .len are read.
        var elemSuffix = RegisterMultisetElementType(seqSrc.Arg);
        var seqSuffix = RegisterSeqElementType(seqSrc.Arg);
        wr.Write("({{ DafnySequence_{0} _src = ", seqSuffix);
        wr.Append(Expr(expr.E, inLetExprBody, wStmts));
        wr.Write("; dafny_multiset_{0}_create(_src.len, _src.data); }})", elemSuffix);
        return;
      }
      if (srcType is SetType setSrc) {
        // multiset(s) for a set s: each distinct element with multiplicity 1.
        // Walk the set's hash table, adding each present element once, via the
        // monomorphised runtime helper. Evaluate the source once through the
        // helper's parameter.
        var elemType = setSrc.Arg;
        var elemSuffix = RegisterMultisetElementType(elemType);
        var setSuffix = RegisterSetElementType(elemType);
        RegisterMultisetFromSet(elemSuffix, setSuffix, elemType);
        wr.Write("dafny_multiset_{0}_from_set_{1}(", elemSuffix, setSuffix);
        wr.Append(Expr(expr.E, inLetExprBody, wStmts));
        wr.Write(")");
        return;
      }
      // multiset(e) operands are resolver-restricted to seq/set, both handled
      // above. (Do NOT throw Feature.Multisets here: it is extended-only, so in
      // c-extended it is not in the UnsupportedFeatures set and would crash the
      // not-an-element check; in minimal `c` the multiset type is already rejected
      // via RejectIfMinimal before reaching here.)
      Contract.Assert(false); throw new Cce.UnreachableException();  // unexpected multiset-forming operand
    }

    protected override void EmitApplyExpr(Type functionType, IOrigin tok, Expression function, List<Expression> arguments,
        bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      // Applying a function value requires function values, which the C backend
      // does not support (C has no expression-level lambdas).
      throw new UnsupportedFeatureException(tok, Feature.FunctionValues);
    }

    public override void EmitExpr(Expression expr, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      // Lambdas / function values are unsupported by the C backend; reject rather
      // than fall through to the base machinery (which emits inline closures C
      // cannot express).
      if (expr is LambdaExpr) {
        throw new UnsupportedFeatureException(expr.Origin, Feature.FunctionValues);
      }
      if (expr is MemberSelectExpr { Member: Function } mse &&
          mse.Type.NormalizeExpand() is ArrowType && !inLetExprBody) {
        throw new UnsupportedFeatureException(expr.Origin, Feature.FunctionValues,
          "using a named function as a first-class value is not supported by the C backend");
      }
      base.EmitExpr(expr, inLetExprBody, wr, wStmts);
    }

    protected override ConcreteSyntaxTree EmitBetaRedex(List<string> boundVars, List<Expression> arguments,
      List<Type> boundTypes, Type resultType, IOrigin tok, bool inLetExprBody, ConcreteSyntaxTree wr,
      ref ConcreteSyntaxTree wStmts) {
      // The inherited body emits a JS/C#-style arrow immediately-applied lambda
      // ("((x) => body)(args)"), which is not C. Function values are unsupported;
      // reject cleanly.
      throw new UnsupportedFeatureException(tok, Feature.FunctionValues,
        "this expression form (an inline applied lambda) is not supported by the C backend");
    }

    protected override void EmitConstructorCheck(string source, DatatypeCtor ctor, ConcreteSyntaxTree wr) {
      // C tagged union: `(source).tag == baseName_TAG_Ctor`. The tag enum is
      // shared across all instantiations of this datatype, so no type args are
      // needed here.
      var baseName = DatatypeBaseName(ctor.EnclosingDatatype);
      wr.Write("({0}).tag == {1}_TAG_{2}", source, baseName, ctor.GetCompileName(Options));
    }

    protected override void EmitDestructor(Action<ConcreteSyntaxTree> source, Formal dtor, int formalNonGhostIndex,
      DatatypeCtor ctor, Func<List<Type>> getTypeArgs, Type bvType, ConcreteSyntaxTree wr) {
      if (ctor.EnclosingDatatype is TupleTypeDecl) {
        // Tuple element access -> field ._i on the monomorphised struct.
        wr.Write("(");
        source(wr);
        wr.Write(")._{0}", formalNonGhostIndex);
      } else {
        var dtorName = FormalName(dtor, formalNonGhostIndex);
        // A boxed (self-referential) field was stored as a pointer, so dereference
        // it to recover the by-value type the rest of the code expects.
        var boxed = IsBoxedField(EnclosingDatatypeInstName(ctor), dtor.Type);
        var deref = boxed ? "*" : "";
        if (ctor.EnclosingDatatype.Ctors.Count > 1) {
          // C tagged union: read the field out of the constructor's union
          // member, i.e. (source).val.Ctor.field.
          wr.Write("({0}(", deref);
          source(wr);
          wr.Write(").val.{0}.{1})", ctor.GetCompileName(Options), dtorName);
        } else {
          // Single constructor (record type): fields live at the top level.
          wr.Write("({0}(", deref);
          source(wr);
          wr.Write(").{0})", dtorName);
        }
      }
    }

    protected override ConcreteSyntaxTree CreateLambda(List<Type> inTypes, IOrigin tok, List<string> inNames,
        Type resultType, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts, bool untyped = false) {
      // The inherited body emits JS-style "function (...) { }", which is not C.
      // Function values are unsupported by the C backend; reject cleanly rather
      // than emit invalid C.
      throw new UnsupportedFeatureException(tok, Feature.FunctionValues,
        "this construct lowers to a closure the C backend cannot emit");
    }

    protected override void CreateIIFE(string bvName, Type bvType, IOrigin bvTok, Type bodyType, IOrigin bodyTok,
      ConcreteSyntaxTree wr, ref ConcreteSyntaxTree wStmts, out ConcreteSyntaxTree wrRhs, out ConcreteSyntaxTree wrBody) {
      // Emit an immediately-evaluated let-binding as a GNU statement expression:
      //   ({ <bvType> <bvName> = (<rhs>); (<body>); })
      // C has no lambdas, so the C++ backend's "[&](...)->...{...}(...)" form does
      // not compile here. The statement-expression form binds "bvName" once and
      // yields the value of the final expression, which is exactly the IIFE
      // semantics the type-test / let machinery relies on.
      wr.Write("({{ {0} {1} = (", TypeName(bvType, wr, bvTok), bvName);
      wrRhs = wr.Fork();
      wr.Write("); (");
      wrBody = wr.Fork();
      wr.Write("); }})");
      // No separate statement scope is used in this form; keep wStmts as-is.
    }

    protected override ConcreteSyntaxTree CreateIIFE0(Type resultType, IOrigin resultTok, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      // Inherited body emits a C++ capture-lambda "[&] () { }()", not C. The
      // let/type-test machinery uses CreateIIFE (a GNU statement expression); this
      // zero-arg variant is only reached by lowerings C doesn't support (sibling
      // CreateIIFE1 already rejects). Reject cleanly.
      throw new UnsupportedFeatureException(resultTok, Feature.LetSuchThatExpressions);
    }

    protected override ConcreteSyntaxTree CreateIIFE1(int source, Type resultType, IOrigin resultTok, string bvName,
        ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      throw new UnsupportedFeatureException(resultTok, Feature.LetSuchThatExpressions);
    }

    protected override void EmitUnaryExpr(ResolvedUnaryOp op, Expression expr, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      switch (op) {
        case ResolvedUnaryOp.BoolNot:
          TrParenExpr("!", expr, wr, inLetExprBody, wStmts);
          break;
        case ResolvedUnaryOp.BitwiseNot:
          if (AsNativeType(expr.Type) != null) {
            wr.Write("~ ");
            TrParenExpr(expr, wr, inLetExprBody, wStmts);
          } else {
            // Bitwise-not on a non-native (unbounded) integer. The inherited C++
            // backend emitted `.Not()` on its BigNumber class; C has no such wrapper
            // (GMP's mpz_com is never emitted), so `.Not()` would be invalid C.
            // Reject in BOTH targets (Feature.NonNativeNewtypes is unsupported by
            // both; Feature.UnboundedIntegers is extended-only and would crash the
            // extended target's not-an-element check).
            throw new UnsupportedFeatureException(expr.Origin, Feature.NonNativeNewtypes,
              "bitwise complement on a non-native integer is not supported by the C backend");
          }
          break;
        case ResolvedUnaryOp.Cardinality: {
          // The monomorphised _size helpers return a native size_t. Following the
          // C++/C#/Java backends, cardinality is a native count internally: minimal
          // `c` prints/uses it natively (no GMP), while the extended target wraps it
          // in a DafnyInt (dafny_int_from_size) so |s| participates in unbounded-int
          // arithmetic. (A collection can never hold more than size_t elements, so
          // the native width is not a real restriction — same as C++'s `.size()`.)
          string sizeCall;
          if (expr.Type.NormalizeToAncestorType() is SeqType st) {
            var suffix = RegisterSeqElementType(st.Arg);
            sizeCall = $"dafny_seq_{suffix}_size";
          } else if (expr.Type.NormalizeToAncestorType() is SetType setCard) {
            var suffix = RegisterSetElementType(setCard.Arg);
            sizeCall = $"dafny_set_{suffix}_size";
          } else if (expr.Type.NormalizeToAncestorType() is MultiSetType msCard) {
            var suffix = RegisterMultisetElementType(msCard.Arg);
            sizeCall = $"dafny_multiset_{suffix}_card";
          } else if (expr.Type.NormalizeToAncestorType() is MapType mapCard) {
            var suffix = RegisterMapType(mapCard.Domain, mapCard.Range);
            sizeCall = $"dafny_map_{suffix}_size";
          } else {
            // Cardinality only applies to seq/set/multiset/map, all handled above.
            // The inherited C++ backend had a generic `.size()` fallback here, which
            // is invalid C; no other type reaches this point.
            Contract.Assert(false); throw new Cce.UnreachableException();  // unexpected cardinality operand
          }
          if (extended) { wr.Write("dafny_int_from_size("); }
          wr.Write("{0}(", sizeCall);
          wr.Append(Expr(expr, inLetExprBody, wStmts));
          wr.Write(extended ? "))" : ")");
          break;
        }
        default:
          Contract.Assert(false); throw new Cce.UnreachableException();  // unexpected unary expression
      }
    }

    bool IsDirectlyComparable(Type t) {
      Contract.Requires(t != null);
      return t.IsBoolType || t.IsCharType || AsNativeType(t) != null;
    }

    // True if the (normalized) type is the unbounded Dafny `int`, represented in
    // C as the GMP-backed DafnyInt. Newtypes with a native representation (e.g.
    // int32) normalize to IntType but are machine integers, so they are excluded.
    private bool IsGmpInt(Type t) {
      // Only the EXTENDED target represents unbounded `int` as a GMP DafnyInt. In
      // the minimal `c` target there is no GMP: an `int`-typed value that actually
      // reaches emission is never a DafnyInt — declared ints are rejected up front
      // (RejectIfMinimal via TypeName), and the only `int`-typed values that slip
      // past that are collection sizes/cardinalities, which the runtime yields as a
      // native size_t. Treating those as GMP would emit GMP-only helpers
      // (dafny_int_to_u64 / dafny_int_from_size) into a GMP-free build — a link
      // break. So report "not a GMP int" in minimal mode; the native fallbacks
      // (direct cast / size_t pass-through) are then correct.
      if (!extended) { return false; }
      var n = t.NormalizeToAncestorType();
      return (n is IntType or BigOrdinalType) && AsNativeType(t) == null;
    }

    // True if the (normalized) type is Dafny `real`, represented in C as the
    // GMP-backed DafnyReal. Only the extended target has real; the minimal `c`
    // target rejects it (RejectIfMinimal via TypeName), and there is no native
    // real, so report "not a GMP real" in minimal mode for symmetry with IsGmpInt.
    private bool IsGmpReal(Type t) {
      if (!extended) { return false; }
      return t.NormalizeToAncestorType() is RealType;
    }

    // Value-based hash/eq function names for a set/map/multiset element/key type.
    //
    // The set/map/multiset (and seq) runtime macros locate slots by (HASHFN(x))
    // and confirm identity with (EQFN(a,b)). We must pass a hash+eq that respect
    // VALUE equality for the element type, otherwise pointer-backed values (int,
    // real, seq) at different addresses are treated as distinct (the bug this
    // fixes). Returns the (hash, eq) C names/macros to plug into those macros,
    // and REGISTERS any dependency (e.g. the underlying seq instantiation) so its
    // own hash/eq helpers are emitted.
    //   * primitives (bool, char, native ints): byte hash + `==` (DAFNY_PRIM_*).
    //   * unbounded int (DafnyInt): mpz value hash + dafny_int_eq.
    //   * real (DafnyReal):         mpq value hash + dafny_real_eq.
    //   * seq<E> (incl. string):    dafny_seq_<NAME>_hash + dafny_seq_<NAME>_equals
    //                               (recursive: E's own hash/eq drive the seq's).
    // Deeper key shapes (set/map/datatype as a key) are not value-hashable yet
    // and are rejected with a clear UnsupportedFeatureException.
    private (string Hash, string Eq) ValueHashEq(Type elemType) {
      var t = ApplyActiveSubst(elemType).NormalizeExpand();
      if (t.IsBoolType || t.IsCharType || AsNativeType(t) != null) {
        return ("DAFNY_PRIM_HASH", "DAFNY_PRIM_EQ");
      }
      if (IsGmpInt(t)) {
        return ("dafny_hash_int", "dafny_int_eq");
      }
      if (IsGmpReal(t)) {
        return ("dafny_hash_real", "dafny_real_eq");
      }
      var seq = t.NormalizeToAncestorType().AsSeqType;
      if (seq != null) {
        // Register the underlying seq<E> so its value hash/eq get emitted, then
        // use them. RegisterSeqElementType recurses on E, so E's own value
        // hash/eq are available for the seq DEFINE.
        var suffix = RegisterSeqElementType(seq.Arg);
        return ("dafny_seq_" + suffix + "_hash", "dafny_seq_" + suffix + "_equals");
      }
      throw new UnsupportedFeatureException(Token.NoToken, Feature.CollectionsOfTraits,
        "C backend: set/map/multiset element/key of type '" + TypeName(t, null, Token.NoToken, null, false) +
        "' has no value-based hash/equality (only bool, char, native ints, int, real, and seq/string thereof are supported as keys)");
    }

    // Value equality for a map VALUE type (used only by map ==). Values are never
    // hashed, so no hash is needed and the type set is broader than keys: for the
    // pointer-backed types we know how to compare by value (int, real, seq) we do
    // so; for everything else we fall back to C `==` (DAFNY_PRIM_EQ), which is the
    // pre-existing behaviour (correct for primitives; a shallow compare otherwise,
    // matching what the byte-`==` map equality already did).
    private string ValueEq(Type valType) {
      var t = ApplyActiveSubst(valType).NormalizeExpand();
      if (IsGmpInt(t)) {
        return "dafny_int_eq";
      }
      if (IsGmpReal(t)) {
        return "dafny_real_eq";
      }
      var seq = t.NormalizeToAncestorType().AsSeqType;
      if (seq != null) {
        var suffix = RegisterSeqElementType(seq.Arg);
        return "dafny_seq_" + suffix + "_equals";
      }
      if (t.NormalizeToAncestorType() is SetType st) {
        return "dafny_set_" + RegisterSetElementType(st.Arg) + "_equals";
      }
      if (t.NormalizeToAncestorType() is MultiSetType mst) {
        return "dafny_multiset_" + RegisterMultisetElementType(mst.Arg) + "_equals";
      }
      if (t.NormalizeToAncestorType() is MapType mpt) {
        return "dafny_map_" + RegisterMapType(mpt.Domain, mpt.Range) + "_equals";
      }
      if (t is UserDefinedType tudt && tudt.ResolvedClass is TupleTypeDecl) {
        return "dafny_tuple_eq_" + RegisterTupleEq(NonGhostTupleArgs(tudt));
      }
      if (t is UserDefinedType dudt && dudt.ResolvedClass is DatatypeDecl ddt && ddt is not TupleTypeDecl) {
        return "dafny_dt_eq_" + RegisterDatatypeEq(dudt);
      }
      return "DAFNY_PRIM_EQ";
    }

    protected override void CompileBinOp(BinaryExpr.ResolvedOpcode op,
      Type e0Type, Type e1Type, IOrigin tok, Type resultType,
      out string opString,
      out string preOpString,
      out string postOpString,
      out string callString,
      out string staticCallString,
      out bool reverseArguments,
      out bool truncateResult,
      out bool convertE1_to_int,
      out bool coerceE1,
      ConcreteSyntaxTree errorWr) {

      opString = null;
      preOpString = "";
      postOpString = "";
      callString = null;
      staticCallString = null;
      reverseArguments = false;
      truncateResult = false;
      convertE1_to_int = false;
      coerceE1 = false;

      switch (op) {
        case BinaryExpr.ResolvedOpcode.Iff:
          opString = "=="; break;
        case BinaryExpr.ResolvedOpcode.Imp:
          preOpString = "!"; opString = "||"; break;
        case BinaryExpr.ResolvedOpcode.Or:
          opString = "||"; break;
        case BinaryExpr.ResolvedOpcode.And:
          opString = "&&"; break;
        case BinaryExpr.ResolvedOpcode.BitwiseAnd:
          if (AsNativeType(resultType) != null) {
            opString = "&";
          } else {
            callString = "And";
          }
          break;
        case BinaryExpr.ResolvedOpcode.BitwiseOr:
          if (AsNativeType(resultType) != null) {
            opString = "|";
          } else {
            callString = "Or";
          }
          break;
        case BinaryExpr.ResolvedOpcode.BitwiseXor:
          if (AsNativeType(resultType) != null) {
            opString = "^";
          } else {
            callString = "Xor";
          }
          break;

        case BinaryExpr.ResolvedOpcode.EqCommon: {
            if (e0Type.NormalizeToAncestorType() is SeqType seqEq) {
              // Sequences are structs: element-wise equality via the
              // monomorphised helper (== on structs is not valid C).
              staticCallString = "dafny_seq_" + RegisterSeqElementType(seqEq.Arg) + "_equals";
            } else if (IsGmpInt(e0Type)) {
              staticCallString = "dafny_int_eq";
            } else if (IsGmpReal(e0Type)) {
              staticCallString = "dafny_real_eq";
            } else if (TupleEqSuffix(e0Type) is { } tupSuffix) {
              // Tuple value: a struct, so C == is invalid; use the generated helper.
              staticCallString = "dafny_tuple_eq_" + tupSuffix;
            } else if (DatatypeEqInstance(e0Type) is { } dtInst) {
              // Datatype value: tagged-union struct, so C == is invalid; use the
              // generated per-instance value-equality helper.
              staticCallString = "dafny_dt_eq_" + dtInst;
            } else if (IsDirectlyComparable(e0Type)) {
              opString = "==";
            } else if (e0Type.IsRefType) {
              opString = "==";
            } else {
              //staticCallString = "==";
              opString = "==";
            }
            break;
          }
        case BinaryExpr.ResolvedOpcode.NeqCommon: {
            if (e0Type.NormalizeToAncestorType() is SeqType seqNeq) {
              staticCallString = "dafny_seq_" + RegisterSeqElementType(seqNeq.Arg) + "_equals";
              preOpString = "!";
            } else if (IsGmpInt(e0Type)) {
              staticCallString = "dafny_int_ne";
            } else if (IsGmpReal(e0Type)) {
              staticCallString = "dafny_real_ne";
            } else if (TupleEqSuffix(e0Type) is { } tupSuffix) {
              preOpString = "!"; staticCallString = "dafny_tuple_eq_" + tupSuffix;
            } else if (DatatypeEqInstance(e0Type) is { } dtInst) {
              preOpString = "!"; staticCallString = "dafny_dt_eq_" + dtInst;
            } else if (IsDirectlyComparable(e0Type)) {
              opString = "!=";
            } else if (e0Type.IsRefType) {
              opString = "!=";
            } else {
              opString = "!=";
            }
            break;
          }

        case BinaryExpr.ResolvedOpcode.Lt:
        case BinaryExpr.ResolvedOpcode.LtChar:
          if (IsGmpInt(e0Type)) { staticCallString = "dafny_int_lt"; }
          else if (IsGmpReal(e0Type)) { staticCallString = "dafny_real_lt"; }
          else { opString = "<"; }
          break;
        case BinaryExpr.ResolvedOpcode.Le:
        case BinaryExpr.ResolvedOpcode.LeChar:
          if (IsGmpInt(e0Type)) { staticCallString = "dafny_int_le"; }
          else if (IsGmpReal(e0Type)) { staticCallString = "dafny_real_le"; }
          else { opString = "<="; }
          break;
        case BinaryExpr.ResolvedOpcode.Ge:
        case BinaryExpr.ResolvedOpcode.GeChar:
          if (IsGmpInt(e0Type)) { staticCallString = "dafny_int_ge"; }
          else if (IsGmpReal(e0Type)) { staticCallString = "dafny_real_ge"; }
          else { opString = ">="; }
          break;
        case BinaryExpr.ResolvedOpcode.Gt:
        case BinaryExpr.ResolvedOpcode.GtChar:
          if (IsGmpInt(e0Type)) { staticCallString = "dafny_int_gt"; }
          else if (IsGmpReal(e0Type)) { staticCallString = "dafny_real_gt"; }
          else { opString = ">"; }
          break;
        case BinaryExpr.ResolvedOpcode.LeftShift:
          if (resultType.NormalizeToAncestorType().IsBitVectorType) {
            truncateResult = true;
          }
          if (AsNativeType(resultType) != null) {
            opString = "<<";
            // The shift AMOUNT may be a plain (non-native) int -> a DafnyInt
            // pointer, which C can't shift by. Narrow it to a machine integer.
            convertE1_to_int = AsNativeType(e1Type) == null;
          } else {
            throw new UnsupportedFeatureException(tok, Feature.NonNativeNewtypes,
              "LeftShift of non-native type");
          }
          break;
        case BinaryExpr.ResolvedOpcode.RightShift:
          if (AsNativeType(resultType) != null) {
            opString = ">>";
            // Same as LeftShift: a non-native shift amount is a DafnyInt pointer;
            // narrow it via convertE1_to_int (the inherited Go ".Uint64()" postfix
            // referenced a method that does not exist in the C runtime).
            convertE1_to_int = AsNativeType(e1Type) == null;
          } else {
            throw new UnsupportedFeatureException(tok, Feature.NonNativeNewtypes,
              "RightShift of non-native type");
          }
          break;
        case BinaryExpr.ResolvedOpcode.Add:
          if (resultType.NormalizeToAncestorType().IsBitVectorType) {
            truncateResult = true;
          }
          if (resultType.IsCharType || AsNativeType(resultType) != null) {
            opString = "+";
          } else if (IsGmpInt(resultType)) {
            staticCallString = "dafny_int_add";
          } else if (IsGmpReal(resultType)) {
            staticCallString = "dafny_real_add";
          } else {
            throw new UnsupportedFeatureException(tok, Feature.NonNativeNewtypes,
              "Add of non-native type");
          }
          break;
        case BinaryExpr.ResolvedOpcode.Sub:
          if (resultType.NormalizeToAncestorType().IsBitVectorType) {
            truncateResult = true;
          }
          if (resultType.IsCharType || AsNativeType(resultType) != null) {
            opString = "-";
          } else if (IsGmpInt(resultType)) {
            staticCallString = "dafny_int_sub";
          } else if (IsGmpReal(resultType)) {
            staticCallString = "dafny_real_sub";
          } else {
            throw new UnsupportedFeatureException(tok, Feature.NonNativeNewtypes);
          }
          break;
        case BinaryExpr.ResolvedOpcode.Mul:
          if (resultType.NormalizeToAncestorType().IsBitVectorType) {
            truncateResult = true;
          }
          if (AsNativeType(resultType) != null) {
            opString = "*";
          } else if (IsGmpInt(resultType)) {
            staticCallString = "dafny_int_mul";
          } else if (IsGmpReal(resultType)) {
            staticCallString = "dafny_real_mul";
          } else {
            throw new UnsupportedFeatureException(tok, Feature.NonNativeNewtypes);
          }
          break;
        case BinaryExpr.ResolvedOpcode.Div:
          if (AsNativeType(resultType) != null) {
            var nt = AsNativeType(resultType);
            if (nt.LowerBound < BigInteger.Zero) {
              // Want Euclidean division for signed types
              staticCallString = "dafny_euclid_div_i64";
            } else {
              // Native division is fine for unsigned
              opString = "/";
            }
          } else if (IsGmpInt(resultType)) {
            staticCallString = "dafny_int_div";   // Euclidean
          } else if (IsGmpReal(resultType)) {
            staticCallString = "dafny_real_div";   // exact rational
          } else {
            callString = "DivBy";
          }
          break;
        case BinaryExpr.ResolvedOpcode.Mod:
          if (AsNativeType(resultType) != null) {
            var nt = AsNativeType(resultType);
            if (nt.LowerBound < BigInteger.Zero) {
              // Want Euclidean division for signed types
              staticCallString = "dafny_euclid_mod_i64";
            } else {
              // Native division is fine for unsigned
              opString = "%";
            }
          } else if (IsGmpInt(resultType)) {
            staticCallString = "dafny_int_mod";   // Euclidean, non-negative
          } else {
            callString = "Modulo";
          }
          break;
        case BinaryExpr.ResolvedOpcode.SeqEq:
          // Sequence equality: monomorphised free function (not a struct method).
          staticCallString = "dafny_seq_" + RegisterSeqElementType(e0Type.NormalizeToAncestorType().AsSeqType.Arg) + "_equals";
          break;
        case BinaryExpr.ResolvedOpcode.SeqNeq:
          staticCallString = "dafny_seq_" + RegisterSeqElementType(e0Type.NormalizeToAncestorType().AsSeqType.Arg) + "_equals";
          preOpString = "!";
          break;
        case BinaryExpr.ResolvedOpcode.SetEq:
          staticCallString = "dafny_set_" + RegisterSetElementType(e0Type.NormalizeToAncestorType().AsSetType.Arg) + "_equals"; break;
        case BinaryExpr.ResolvedOpcode.MapEq:
          staticCallString = "dafny_map_" + MapSuffix(e0Type) + "_equals"; break;
        case BinaryExpr.ResolvedOpcode.MultiSetEq:
          staticCallString = "dafny_multiset_" + RegisterMultisetElementType(e0Type.NormalizeToAncestorType().AsMultiSetType.Arg) + "_equals"; break;
        case BinaryExpr.ResolvedOpcode.SetNeq:
          preOpString = "!"; staticCallString = "dafny_set_" + RegisterSetElementType(e0Type.NormalizeToAncestorType().AsSetType.Arg) + "_equals"; break;
        case BinaryExpr.ResolvedOpcode.MapNeq:
          preOpString = "!"; staticCallString = "dafny_map_" + MapSuffix(e0Type) + "_equals"; break;
        case BinaryExpr.ResolvedOpcode.MultiSetNeq:
          preOpString = "!"; staticCallString = "dafny_multiset_" + RegisterMultisetElementType(e0Type.NormalizeToAncestorType().AsMultiSetType.Arg) + "_equals"; break;
        case BinaryExpr.ResolvedOpcode.ProperSubset:
          staticCallString = "dafny_set_" + RegisterSetElementType(e0Type.NormalizeToAncestorType().AsSetType.Arg) + "_proper_subset"; break;
        case BinaryExpr.ResolvedOpcode.ProperMultiSubset:
          staticCallString = "dafny_multiset_" + RegisterMultisetElementType(e0Type.NormalizeToAncestorType().AsMultiSetType.Arg) + "_proper_subset"; break;
        case BinaryExpr.ResolvedOpcode.Subset:
          staticCallString = "dafny_set_" + RegisterSetElementType(e0Type.NormalizeToAncestorType().AsSetType.Arg) + "_subset"; break;
        case BinaryExpr.ResolvedOpcode.MultiSubset:
          staticCallString = "dafny_multiset_" + RegisterMultisetElementType(e0Type.NormalizeToAncestorType().AsMultiSetType.Arg) + "_subset"; break;
        case BinaryExpr.ResolvedOpcode.Superset:
          // a >= b  <=>  b <= a : reverse arguments to the subset helper.
          staticCallString = "dafny_set_" + RegisterSetElementType(e0Type.NormalizeToAncestorType().AsSetType.Arg) + "_subset"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.MultiSuperset:
          // a >= b  <=>  b <= a : reverse arguments to the subset helper.
          staticCallString = "dafny_multiset_" + RegisterMultisetElementType(e0Type.NormalizeToAncestorType().AsMultiSetType.Arg) + "_subset"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.ProperSuperset:
          // a > b  <=>  b < a : reverse args to proper_subset.
          staticCallString = "dafny_set_" + RegisterSetElementType(e0Type.NormalizeToAncestorType().AsSetType.Arg) + "_proper_subset"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.ProperMultiSuperset:
          staticCallString = "dafny_multiset_" + RegisterMultisetElementType(e0Type.NormalizeToAncestorType().AsMultiSetType.Arg) + "_proper_subset"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.Disjoint:
          staticCallString = "dafny_set_" + RegisterSetElementType(e0Type.NormalizeToAncestorType().AsSetType.Arg) + "_disjoint"; break;
        case BinaryExpr.ResolvedOpcode.MultiSetDisjoint:
          staticCallString = "dafny_multiset_" + RegisterMultisetElementType(e0Type.NormalizeToAncestorType().AsMultiSetType.Arg) + "_disjoint"; break;
        case BinaryExpr.ResolvedOpcode.InSet:
          // x in s: reverse args so the set is first -> contains(s, x).
          staticCallString = "dafny_set_" + RegisterSetElementType(e1Type.NormalizeToAncestorType().AsSetType.Arg) + "_contains"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.InMap:
          staticCallString = "dafny_map_" + MapSuffix(e1Type) + "_contains_key"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.InMultiSet:
          staticCallString = "dafny_multiset_" + RegisterMultisetElementType(e1Type.NormalizeToAncestorType().AsMultiSetType.Arg) + "_contains"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.NotInSet:
          preOpString = "!"; staticCallString = "dafny_set_" + RegisterSetElementType(e1Type.NormalizeToAncestorType().AsSetType.Arg) + "_contains"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.NotInMap:
          preOpString = "!"; staticCallString = "dafny_map_" + MapSuffix(e1Type) + "_contains_key"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.NotInMultiSet:
          preOpString = "!"; staticCallString = "dafny_multiset_" + RegisterMultisetElementType(e1Type.NormalizeToAncestorType().AsMultiSetType.Arg) + "_contains"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.Union:
          staticCallString = "dafny_set_" + RegisterSetElementType(e0Type.NormalizeToAncestorType().AsSetType.Arg) + "_union"; break;
        case BinaryExpr.ResolvedOpcode.MultiSetUnion:
          staticCallString = "dafny_multiset_" + RegisterMultisetElementType(e0Type.NormalizeToAncestorType().AsMultiSetType.Arg) + "_union"; break;
        case BinaryExpr.ResolvedOpcode.MapMerge:
          staticCallString = "dafny_map_" + MapSuffix(e0Type) + "_merge"; break;
        case BinaryExpr.ResolvedOpcode.Intersection:
          staticCallString = "dafny_set_" + RegisterSetElementType(e0Type.NormalizeToAncestorType().AsSetType.Arg) + "_intersection"; break;
        case BinaryExpr.ResolvedOpcode.MultiSetIntersection:
          staticCallString = "dafny_multiset_" + RegisterMultisetElementType(e0Type.NormalizeToAncestorType().AsMultiSetType.Arg) + "_intersection"; break;
        case BinaryExpr.ResolvedOpcode.SetDifference:
          staticCallString = "dafny_set_" + RegisterSetElementType(e0Type.NormalizeToAncestorType().AsSetType.Arg) + "_difference"; break;
        case BinaryExpr.ResolvedOpcode.MultiSetDifference:
          staticCallString = "dafny_multiset_" + RegisterMultisetElementType(e0Type.NormalizeToAncestorType().AsMultiSetType.Arg) + "_difference"; break;
        case BinaryExpr.ResolvedOpcode.MapSubtraction:
          // m - s (remove a set of keys from a map): cross-type (map minus a
          // key-set), no single monomorphised helper. Reject cleanly rather than
          // emit an invalid C++ method call.
          throw new UnsupportedFeatureException(Token.NoToken, Feature.MapItems,
            "map subtraction (map - set-of-keys) is not supported by the C backend");

        case BinaryExpr.ResolvedOpcode.ProperPrefix:
          staticCallString = "dafny_seq_" + RegisterSeqElementType(e0Type.NormalizeToAncestorType().AsSeqType.Arg) + "_is_proper_prefix"; break;
        case BinaryExpr.ResolvedOpcode.Prefix:
          staticCallString = "dafny_seq_" + RegisterSeqElementType(e0Type.NormalizeToAncestorType().AsSeqType.Arg) + "_is_prefix"; break;
        case BinaryExpr.ResolvedOpcode.Concat:
          // Sequence concatenation s1 + s2: monomorphised free function.
          staticCallString = "dafny_seq_" + RegisterSeqElementType(e0Type.NormalizeToAncestorType().AsSeqType.Arg) + "_concat";
          break;
        case BinaryExpr.ResolvedOpcode.InSeq:
          // x in s: seq is the second operand; reverse so it's first -> contains(s, x).
          staticCallString = "dafny_seq_" + RegisterSeqElementType(e1Type.NormalizeToAncestorType().AsSeqType.Arg) + "_contains"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.NotInSeq:
          preOpString = "!"; staticCallString = "dafny_seq_" + RegisterSeqElementType(e1Type.NormalizeToAncestorType().AsSeqType.Arg) + "_contains"; reverseArguments = true; break;

        default:
          Contract.Assert(false); throw new Cce.UnreachableException();  // unexpected binary expression
      }
    }

    protected override void EmitIsZero(string varName, ConcreteSyntaxTree wr) {
      wr.Write("{0} == 0", varName);
    }

    protected override void EmitConversionExpr(Expression fromExpr, Type fromType, Type toType, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      // Conversions to/from function (arrow) types. An identity conversion (same
      // arrow signature, e.g. subset-type witness plumbing) just emits the value.
      // A conversion between DIFFERENT arrow signatures would need an adapter
      // trampoline, which is not implemented; reject that cleanly.
      if (fromType.IsArrowType || toType.IsArrowType) {
        // Any conversion involving a function (arrow) type needs function values,
        // which the C backend does not support.
        throw new UnsupportedFeatureException(fromExpr.Origin, Feature.FunctionValues);
      }
      // A conversion whose RESULT is an unbounded int or exact real produces a
      // GMP-backed DafnyInt/DafnyReal (e.g. `'a' as int`, `x as real`). The minimal
      // `c` target is GMP-free, so reject these — even when the result is only
      // printed and never bound to an int/real-typed entity (which would otherwise
      // slip past the TypeName guard).
      if (!extended) {
        var toN = toType.NormalizeToAncestorType();
        if (toN.IsNumericBased(Type.NumericPersuasion.Int) && AsNativeType(toType) == null) {
          RejectIfMinimal(Feature.UnboundedIntegers, fromExpr.Origin);
        } else if (toN.IsNumericBased(Type.NumericPersuasion.Real)) {
          RejectIfMinimal(Feature.RealNumbers, fromExpr.Origin);
        }
      }
      if (fromType.IsNumericBased(Type.NumericPersuasion.Int) || fromType.IsBitVectorType || fromType.IsCharType) {
        if (toType.IsNumericBased(Type.NumericPersuasion.Real)) {
          // (int or native-int) -> real. real is a GMP mpq wrapper (DafnyReal);
          // build it from a DafnyInt. A native source is first widened to
          // DafnyInt via dafny_int_from_i64.
          wr.Write("dafny_real_from_int(");
          if (AsNativeType(fromType) is { } realFromNative) {
            WriteNativeToDafnyInt(fromExpr, realFromNative, inLetExprBody, wr, wStmts);
          } else {
            wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
          }
          wr.Write(")");
        } else if (toType.IsCharType) {
          // -> char (a uint32 code point). From a native int or char, a plain
          // cast works. From an unbounded int (DafnyInt), narrow via
          // dafny_int_to_i64 first, otherwise we'd cast the pointer itself.
          if (!fromType.IsCharType && AsNativeType(fromType) == null) {
            wr.Write("(dafny_char)dafny_int_to_i64(");
            wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
            wr.Write(")");
          } else {
            wr.Write("(dafny_char)");
            TrParenExpr(fromExpr, wr, inLetExprBody, wStmts);
          }
        } else {
          // (int or bv or char) -> (int or bv or ORDINAL)
          var fromNative = AsNativeType(fromType);
          var toNative = AsNativeType(toType);
          if (fromNative != null && toNative != null) {
            // from a native, to a native -- simple!
            wr.Write(GetNativeTypeName(toNative));
            TrParenExpr(fromExpr, wr, inLetExprBody, wStmts);
          } else if (fromType.IsCharType) {
            Contract.Assert(fromNative == null);
            if (toNative == null) {
              // char -> unbounded int: a dafny_char is a uint32 code point.
              wr.Write("dafny_int_from_i64((long long)(");
              wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
              wr.Write("))");
            } else {
              // char -> native
              wr.Write($"({GetNativeTypeName(toNative)})");
              TrParenExpr(fromExpr, wr, inLetExprBody, wStmts);
            }
          } else if (fromNative == null && toNative == null) {
            // big-integer (int or bv) -> big-integer (int or bv or ORDINAL), so identity will do
            wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
          } else if (fromNative != null) {
            Contract.Assert(toNative == null); // follows from other checks
            // native (int or bv) -> unbounded int (DafnyInt, GMP-backed). Unsigned
            // native sources (uint64/bv64 with the top bit set) must widen via the
            // unsigned helper, else 0xFFFFFFFFFFFFFFFF would become -1.
            WriteNativeToDafnyInt(fromExpr, fromNative, inLetExprBody, wr, wStmts);
          } else {
            // any unbounded int (or bv) -> native (int or bv): narrow the GMP
            // value back to a machine integer, then cast to the concrete native C
            // type. An UNSIGNED target must go through dafny_int_to_u64 (mpz_get_ui):
            // the signed dafny_int_to_i64 (mpz_get_si) can't represent values in
            // [2^63, 2^64) and would clamp/garble them (e.g. 2^64-1 -> 2^63-1).
            Contract.Assert(fromNative == null && toNative != null);
            var literal = PartiallyEvaluate(fromExpr);
            if (literal != null) {
              // Optimize constant to avoid an intermediate DafnyInt allocation.
              wr.Write("(({0}){1})", GetNativeTypeName(toNative), literal);
            } else if (!extended) {
              // Minimal `c`: there is no GMP DafnyInt. A non-native `int` source
              // that actually reaches here is a collection size/cardinality, which
              // the runtime already yields as a native size_t — so a plain cast to
              // the target native type is correct (dafny_int_to_* is GMP-only and
              // absent from a GMP-free build). Declared unbounded ints never reach
              // here; they are rejected up front via RejectIfMinimal.
              wr.Write("(({0})(", GetNativeTypeName(toNative));
              wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
              wr.Write("))");
            } else {
              var narrowHelper = IsUnsignedNative(toNative) ? "dafny_int_to_u64" : "dafny_int_to_i64";
              wr.Write("(({0}){1}(", GetNativeTypeName(toNative), narrowHelper);
              wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
              wr.Write("))");
            }
          }
        }
      } else if (fromType.IsNumericBased(Type.NumericPersuasion.Real)) {
        Contract.Assert(AsNativeType(fromType) == null);
        if (toType.IsNumericBased(Type.NumericPersuasion.Real)) {
          // real -> real
          Contract.Assert(AsNativeType(toType) == null);
          wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
        } else {
          // real -> (int or bv). Dafny only permits this when the real is
          // provably integral, so flooring gives the exact value. Produce a
          // DafnyInt via dafny_int_from_real, then narrow to a native type if
          // the target is a native int/bv.
          if (AsNativeType(toType) is NativeType nt) {
            // An UNSIGNED native target must narrow via dafny_int_to_u64, else a
            // real in [2^63, 2^64) (e.g. 18446744073709551615.0 as u64/bv64) would
            // go through the signed dafny_int_to_i64 and come out garbled. Same fix
            // as the int->native narrowing above.
            var realNarrow = IsUnsignedNative(nt) ? "dafny_int_to_u64" : "dafny_int_to_i64";
            wr.Write("(({0}){1}(dafny_int_from_real(", GetNativeTypeName(nt), realNarrow);
            wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
            wr.Write(")))");
          } else {
            wr.Write("dafny_int_from_real(");
            wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
            wr.Write(")");
          }
        }
      } else if (fromType.IsBigOrdinalType) {
        Contract.Assert(toType.IsNumericBased(Type.NumericPersuasion.Int));
        // identity will do
        wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
      } else if (fromType.Equals(toType) || fromType.AsNewtype != null || toType.AsNewtype != null) {
        // identity will do (a newtype wraps its base with an identical C repn)
        wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
      } else {
        // A genuine reference/datatype variance downcast (e.g. Co<X> as Co<Y>).
        // The monomorphised C structs for the two instantiations are distinct
        // types, so an identity copy would be invalid C. Reject cleanly rather
        // than trip the old assertion.
        throw new UnsupportedFeatureException(fromExpr.Origin, Feature.SubsetTypeTests,
          "variance/type-parameter downcast conversions are not supported by the C backend");
      }
    }

    protected override void EmitTypeTest(string localName, Type fromType, Type toType, IOrigin tok, ConcreteSyntaxTree wr) {
      // Reached only for the trait/reference-type "is" path (an `instanceof`-style
      // downcast). The monomorphised C model has no runtime type information for
      // class/trait hierarchies, so this cannot be answered at run time. Reject
      // cleanly rather than emit code that always yields the wrong answer.
      throw new UnsupportedFeatureException(tok, Feature.TypeTests,
        "type tests on class/trait reference types are not supported by the C backend (no runtime type information)");
    }

    // The C backend never emits the generated "_Is" constraint-checking method
    // (unlike C#, it does not call GenerateIsMethodBody), so any call to one would
    // reference an undefined C function. This override is reached for a `is`/`as`
    // against a subset type (or a newtype with a non-trivial, non-bit-pattern
    // constraint) whose membership can only be decided by running the constraint
    // predicate. Reject cleanly. Tractable cases never get here: native newtypes /
    // bitvectors that cover all their bit patterns short-circuit to a `true`
    // literal in MaybeEmitCallToIsMethod, and range/integral/char membership is
    // handled by EmitIsInIntegerRange / EmitIsIntegerTest.
    protected override ConcreteSyntaxTree EmitCallToIsMethod(RedirectingTypeDecl declWithConstraints, Type type, ConcreteSyntaxTree wr) {
      throw new UnsupportedFeatureException(declWithConstraints.Tok, Feature.SubsetTypeTests,
        "type tests against a subset type's constraint predicate are not supported by the C backend");
    }

    // The integer/range/unicode `is`-test emitters below are only invoked by the
    // base machinery for compiler-internal coercions; no *valid* user program in
    // this Dafny version reaches them under the flags the C backend supports
    // (a user-level `real is int` / `int is char` is a resolution error). Rather
    // than ship an emission path no test can exercise, keep clean rejects. If a
    // reachable case is ever found, the GMP building blocks (dafny_real_is_integer
    // for denominator==1, dafny_int_le/lt for range membership) make it a small
    // change.
    protected override void EmitIsIntegerTest(Expression source, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      throw new UnsupportedFeatureException(source.Origin, Feature.TypeTests);
    }

    protected override void EmitIsUnicodeScalarValueTest(Expression source, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      throw new UnsupportedFeatureException(source.Origin, Feature.TypeTests);
    }

    protected override void EmitIsInIntegerRange(Expression source, BigInteger lo, BigInteger hi, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      throw new UnsupportedFeatureException(source.Origin, Feature.TypeTests);
    }

    protected override void EmitCollectionDisplay(CollectionType ct, IOrigin tok, List<Expression> elements,
      bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      if (ct is SetType) {
        // set display {a, b, c}: build the concrete monomorphised set from a C
        // compound-literal array (create() dedups on insert).
        var elemType = ct.TypeArgs[0];
        var suffix = RegisterSetElementType(elemType);
        var elemName = TypeName(elemType, wr, tok, null, false);
        if (elements.Count == 0) {
          wr.Write("dafny_set_{0}_create(0, NULL)", suffix);
        } else {
          wr.Write("dafny_set_{0}_create({1}, ({2}[]){{", suffix, elements.Count, elemName);
          for (var i = 0; i < elements.Count; i++) {
            wr.Append(Expr(elements[i], inLetExprBody, wStmts));
            if (i < elements.Count - 1) {
              wr.Write(",");
            }
          }
          wr.Write("})");
        }
      } else if (ct is MultiSetType) {
        // multiset display multiset{a, b, b}: build the concrete monomorphised
        // multiset from a C compound-literal array (create() counts duplicates).
        var elemType = ct.TypeArgs[0];
        var suffix = RegisterMultisetElementType(elemType);
        var elemName = TypeName(elemType, wr, tok, null, false);
        if (elements.Count == 0) {
          wr.Write("dafny_multiset_{0}_create(0, NULL)", suffix);
        } else {
          wr.Write("dafny_multiset_{0}_create({1}, ({2}[]){{", suffix, elements.Count, elemName);
          for (var i = 0; i < elements.Count; i++) {
            wr.Append(Expr(elements[i], inLetExprBody, wStmts));
            if (i < elements.Count - 1) {
              wr.Write(",");
            }
          }
          wr.Write("})");
        }
      } else {
        Contract.Assert(ct is SeqType);  // follows from precondition
        // seq display [a, b, c]: build the concrete monomorphised sequence from a
        // C compound-literal array. seq<char> displays are handled the same way
        // (no special-casing needed) since a char sequence is DafnySequence_char.
        var elemType = ct.TypeArgs[0];
        var suffix = RegisterSeqElementType(elemType);
        var elemName = TypeName(elemType, wr, tok, null, false);
        if (elements.Count == 0) {
          wr.Write("dafny_seq_{0}_create(0, NULL)", suffix);
        } else {
          wr.Write("dafny_seq_{0}_create({1}, ({2}[]){{", suffix, elements.Count, elemName);
          for (var i = 0; i < elements.Count; i++) {
            wr.Append(Expr(elements[i], inLetExprBody, wStmts));
            if (i < elements.Count - 1) {
              wr.Write(",");
            }
          }
          wr.Write("})");
        }
      }
    }

    protected override void EmitMapDisplay(MapType mt, IOrigin tok, List<MapDisplayEntry> elements,
      bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      // map display m := [k0 := v0, ...]: build the concrete monomorphised map
      // from parallel key/value compound-literal arrays (create() dedups keys).
      var keyType = mt.TypeArgs[0];
      var valType = mt.TypeArgs[1];
      var suffix = RegisterMapType(keyType, valType);
      var keyName = TypeName(keyType, wr, tok, null, false);
      var valName = TypeName(valType, wr, tok, null, false);
      if (elements.Count == 0) {
        wr.Write("dafny_map_{0}_create(0, NULL, NULL)", suffix);
        return;
      }
      wr.Write("dafny_map_{0}_create({1}, ({2}[]){{", suffix, elements.Count, keyName);
      string sep = "";
      foreach (MapDisplayEntry p in elements) {
        wr.Write(sep);
        wr.Append(Expr(p.A, inLetExprBody, wStmts));
        sep = ", ";
      }
      wr.Write("}}, ({0}[]){{", valName);
      sep = "";
      foreach (MapDisplayEntry p in elements) {
        wr.Write(sep);
        wr.Append(Expr(p.B, inLetExprBody, wStmts));
        sep = ", ";
      }
      wr.Write("})");
    }

    protected override void EmitSetBuilder_New(ConcreteSyntaxTree wr, SetComprehension e, string collectionName) {
      var wrVarInit = DeclareLocalVar(collectionName, null, null, wr);
      wrVarInit.Write("DafnySet<{0}>()", TypeName(e.Type.NormalizeToAncestorType().AsSetType.Arg, wrVarInit, e.Origin, null, false));
    }

    protected override void EmitMapBuilder_New(ConcreteSyntaxTree wr, MapComprehension e, string collectionName) {
      throw new UnsupportedFeatureException(e.Origin, Feature.MapComprehensions);
    }

    protected override void EmitSetBuilder_Add(CollectionType ct, string collName, Expression elmt, bool inLetExprBody, ConcreteSyntaxTree wr) {
      Contract.Assume(ct is SetType || ct is MultiSetType);  // follows from precondition
      if (ct is MultiSetType) {
        // This should never occur since there is no syntax for multiset comprehensions yet
        throw new Cce.UnreachableException();
      }
      var wStmts = wr.Fork();
      wr.Write("{0}.set.emplace(", collName);
      wr.Append(Expr(elmt, inLetExprBody, wStmts));
      wr.WriteLine(");");
    }

    protected override ConcreteSyntaxTree EmitMapBuilder_Add(MapType mt, IOrigin tok, string collName, Expression term, bool inLetExprBody, ConcreteSyntaxTree wr) {
      throw new UnsupportedFeatureException(tok, Feature.MapComprehensions);
    }

    protected override void GetCollectionBuilder_Build(CollectionType ct, IOrigin tok, string collName,
      ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmt) {
      // collections are built in place
      wr.Write(collName);
    }

    protected override void EmitSingleValueGenerator(Expression e, bool inLetExprBody, string type,
      ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
      throw new UnsupportedFeatureException(Token.NoToken, Feature.ExactBoundedPool);
    }

    protected override void EmitHaltRecoveryStmt(Statement body, string haltMessageVarName, Statement recoveryBody, ConcreteSyntaxTree wr) {
      throw new UnsupportedFeatureException(Token.NoToken, Feature.RunAllTests);
    }
  }
}
