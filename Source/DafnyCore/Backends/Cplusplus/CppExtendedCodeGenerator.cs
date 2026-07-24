//-----------------------------------------------------------------------------
//
// Copyright by the contributors to the Dafny Project
// SPDX-License-Identifier: MIT
//
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.Contracts;
using System.Numerics;

namespace Microsoft.Dafny.Compilers;

// Code generator for the `c++-extended` target. It is NOT a fork of the C++
// backend: it inherits everything from CppCodeGenerator and only overrides the
// handful of sites where the minimal C++ backend deliberately rejects a feature,
// implementing them with C++ standard facilities instead:
//
//   * unbounded `int`   -> GMP  mpz_class          (<gmpxx.h>)
//   * exact `real`      -> DafnyReal (unreduced num/den pair, mirrors C#'s
//                          Dafny.BigRational; lives in DafnyRuntime.h)
//   * `multiset<T>`     -> DafnyMultiset<T>         (std::unordered_multiset)
//   * function values   -> std::function<R(A...)>  + C++ lambdas
//
// The GMP-backed types and the multiset live in the shared C++ runtime header
// (DafnyRuntime.h); this generator only decides how to spell/emit them.
class CppExtendedCodeGenerator : CppCodeGenerator {

  public CppExtendedCodeGenerator(DafnyOptions options, ErrorReporter reporter, ReadOnlyCollection<string> headers)
    : base(options, reporter, headers) {
  }

  // Start from the C++ backend's unsupported set and re-enable the four features
  // this target implements.
  public override IReadOnlySet<Feature> UnsupportedFeatures {
    get {
      var features = new HashSet<Feature>(base.UnsupportedFeatures);
      features.Remove(Feature.UnboundedIntegers);
      features.Remove(Feature.RealNumbers);
      features.Remove(Feature.Multisets);
      features.Remove(Feature.FunctionValues);
      return features;
    }
  }

  // ---- Type spelling -------------------------------------------------------

  protected override string TypeName(Type type, ConcreteSyntaxTree wr, IOrigin tok, MemberDecl/*?*/ member = null, bool class_name = false) {
    var xType = type.NormalizeExpand();
    if (xType is IntType or BigOrdinalType) {
      return "mpz_class";
    } else if (xType is RealType) {
      return "DafnyReal";
    } else if (xType is BitvectorType bv && bv.NativeType == null) {
      // Non-native bitvectors also need arbitrary precision.
      return "mpz_class";
    } else if (xType is ArrowType at) {
      return ArrowTypeName(at, wr, tok);
    }
    return base.TypeName(type, wr, tok, member, class_name);
  }

  // std::function<R(A0, A1, ...)> for a Dafny arrow type A0,A1,... -> R.
  private string ArrowTypeName(ArrowType at, ConcreteSyntaxTree wr, IOrigin tok) {
    var result = TypeName(at.Result, wr, tok, null, false);
    var args = new List<string>();
    foreach (var a in at.Args) {
      args.Add(TypeName(a, wr, tok, null, false));
    }
    return $"std::function<{result}({string.Join(", ", args)})>";
  }

  protected override string FullTypeName(UserDefinedType udt, MemberDecl/*?*/ member = null) {
    if (udt is ArrowType at) {
      return ArrowTypeName(at, null, udt.Origin);
    }
    return base.FullTypeName(udt, member);
  }

  protected override string TypeInitializationValue(Type type, ConcreteSyntaxTree wr, IOrigin tok, bool usePlaceboValue, bool constructTypeParameterDefaultsFromTypeDescriptors) {
    var xType = type.NormalizeExpandKeepConstraints();
    if (xType is IntType or BigOrdinalType) {
      return "mpz_class(0)";
    } else if (xType is RealType) {
      return "DafnyReal(0L)";
    } else if (xType is BitvectorType bv && bv.NativeType == null) {
      return "mpz_class(0)";
    } else if (xType is MultiSetType ms) {
      return $"DafnyMultiset<{TypeName(ms.Arg, wr, tok)}>::empty()";
    } else if (xType is ArrowType at) {
      // A default (never-called) function value.
      return $"{ArrowTypeName(at, wr, tok)}()";
    }
    return base.TypeInitializationValue(type, wr, tok, usePlaceboValue, constructTypeParameterDefaultsFromTypeDescriptors);
  }

  // ---- Literals ------------------------------------------------------------

  protected override void EmitLiteralExpr(ConcreteSyntaxTree wr, LiteralExpr e) {
    if (e.Value is BigInteger i && !(e is CharLiteralExpr) && AsNativeType(e.Type) == null) {
      EmitIntegerLiteral(i, wr);
      return;
    }
    if (e.Value is BaseTypes.BigDec n) {
      // Dafny real literal = exact rational (mantissa * 10^exponent). Build the
      // UNREDUCED num/den (mirroring C#'s Dafny.BigRational, which does NOT
      // reduce literals) and hand them to DafnyReal as a decimal-string pair.
      BigInteger num = n.Mantissa;
      BigInteger den = BigInteger.One;
      if (n.Exponent >= 0) {
        num *= BigInteger.Pow(10, n.Exponent);
      } else {
        den = BigInteger.Pow(10, -n.Exponent);
      }
      wr.Write("DafnyReal(\"{0}\", \"{1}\")", num, den);
      return;
    }
    base.EmitLiteralExpr(wr, e);
  }

  protected override void EmitIntegerLiteral(BigInteger i, ConcreteSyntaxTree wr) {
    // mpz_class has an i64 constructor and a decimal-string constructor; use the
    // string form for values outside the native long range so precision is kept.
    if (i >= long.MinValue && i <= long.MaxValue) {
      wr.Write("mpz_class({0}L)", i);
    } else {
      wr.Write("mpz_class(\"{0}\")", i);
    }
  }

  // ---- Conversions ---------------------------------------------------------

  protected override void EmitConversionExpr(Expression fromExpr, Type fromType, Type toType, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
    var fromNative = AsNativeType(fromType);
    var toNative = AsNativeType(toType);

    if (fromType.IsNumericBased(Type.NumericPersuasion.Int) || fromType.IsBitVectorType || fromType.IsCharType) {
      if (toType.IsNumericBased(Type.NumericPersuasion.Real)) {
        // int/char/bv -> real : make an exact rational with denominator 1.
        wr.Write("DafnyReal(");
        EmitToBigInt(fromExpr, fromType, inLetExprBody, wr, wStmts);
        wr.Write(")");
        return;
      } else if (toType.IsCharType) {
        wr.Write("(char)");
        if (fromNative == null) {
          // mpz_class -> char
          TrParenExpr(fromExpr, wr, inLetExprBody, wStmts);
          wr.Write(".get_si()");
        } else {
          TrParenExpr(fromExpr, wr, inLetExprBody, wStmts);
        }
        return;
      } else {
        // (int or bv or char) -> (int or bv or ORDINAL)
        if (fromNative != null && toNative != null) {
          wr.Write(GetNativeTypeName(toNative));
          TrParenExpr(fromExpr, wr, inLetExprBody, wStmts);
          return;
        } else if (fromNative == null && toNative == null) {
          // big -> big : identity (mpz_class).
          wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
          return;
        } else if (fromNative != null) {
          // native -> mpz_class : the constructor handles it.
          wr.Write("mpz_class(");
          if (fromType.IsCharType) {
            wr.Write("(long)");
          }
          TrParenExpr(fromExpr, wr, inLetExprBody, wStmts);
          wr.Write(")");
          return;
        } else {
          // mpz_class -> native : extract as long, then narrow.
          wr.Write("({0})(", GetNativeTypeName(toNative));
          TrParenExpr(fromExpr, wr, inLetExprBody, wStmts);
          wr.Write(".get_si())");
          return;
        }
      }
    } else if (fromType.IsNumericBased(Type.NumericPersuasion.Real)) {
      if (toType.IsNumericBased(Type.NumericPersuasion.Real)) {
        wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
        return;
      } else {
        // real -> int : floor of the (unreduced) rational toward negative
        // infinity, matching Dafny's `.Floor` / `r as int`.
        wr.Write("(");
        wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
        wr.Write(").Floor()");
        if (toNative != null) {
          wr.Write(".get_si()");
        }
        return;
      }
    } else if (fromType.IsBigOrdinalType) {
      wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
      return;
    }
    base.EmitConversionExpr(fromExpr, fromType, toType, inLetExprBody, wr, wStmts);
  }

  // Emit `fromExpr` as an mpz_class value (used as the numerator when building a
  // DafnyReal).
  private void EmitToBigInt(Expression fromExpr, Type fromType, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
    if (AsNativeType(fromType) != null || fromType.IsCharType) {
      wr.Write("mpz_class((long)(");
      wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
      wr.Write("))");
    } else {
      wr.Append(Expr(fromExpr, inLetExprBody, wStmts));
    }
  }

  // ---- Printing ------------------------------------------------------------

  protected override void EmitPrintStmt(ConcreteSyntaxTree wr, Expression arg) {
    // Arithmetic on mpz_class yields GMP *expression templates*, whose concrete
    // type is not mpz_class. dafny_print's generic template would then match the
    // expression type and print GMP's default form instead of Dafny's
    // formatting. Materialize int print arguments into a concrete mpz_class (and
    // real into a concrete DafnyReal) so the dedicated dafny_print overloads
    // (which produce Dafny-compatible output) are selected.
    var t = arg.Type.NormalizeToAncestorType();
    if (AsNativeType(arg.Type) == null &&
        (t.IsNumericBased(Type.NumericPersuasion.Int) || t.IsNumericBased(Type.NumericPersuasion.Real))) {
      var wStmts = wr.Fork();
      var conv = t.IsNumericBased(Type.NumericPersuasion.Real) ? "dafny_as_real" : "dafny_as_int";
      wr.Write("dafny_print({0}(", conv);
      wr.Append(Expr(arg, false, wStmts));
      wr.WriteLine("));");
      return;
    }
    base.EmitPrintStmt(wr, arg);
  }

  // ---- Multisets -----------------------------------------------------------

  protected override void EmitCollectionDisplay(CollectionType ct, IOrigin tok, List<Expression> elements,
      bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
    if (ct is MultiSetType) {
      wr.Write("DafnyMultiset<{0}>::Create({{", TypeName(ct.TypeArgs[0], wr, tok, null, false));
      for (var i = 0; i < elements.Count; i++) {
        wr.Append(Expr(elements[i], inLetExprBody, wStmts));
        if (i < elements.Count - 1) {
          wr.Write(",");
        }
      }
      wr.Write("})");
      return;
    }
    base.EmitCollectionDisplay(ct, tok, elements, inLetExprBody, wr, wStmts);
  }

  protected override void EmitMultiSetFormingExpr(MultiSetFormingExpr expr, bool inLetExprBody, ConcreteSyntaxTree wr,
      ConcreteSyntaxTree wStmts) {
    var srcType = expr.E.Type.NormalizeToAncestorType();
    if (srcType is SeqType seqSrc) {
      var elemName = TypeName(seqSrc.Arg, wr, expr.Origin, null, false);
      // multiset(s) for a seq s: insert every element (with repetition).
      wr.Write("[](DafnySequence<{0}> _s) -> DafnyMultiset<{0}> {{ DafnyMultiset<{0}> _m; for (uint64 _i = 0; _i < _s.size(); _i++) {{ _m.multiset.insert(_s.select(_i)); }} return _m; }}(", elemName);
      wr.Append(Expr(expr.E, inLetExprBody, wStmts));
      wr.Write(")");
      return;
    }
    if (srcType is SetType setSrc) {
      var elemName = TypeName(setSrc.Arg, wr, expr.Origin, null, false);
      // multiset(s) for a set s: each distinct element once.
      wr.Write("[](DafnySet<{0}> _s) -> DafnyMultiset<{0}> {{ DafnyMultiset<{0}> _m; for (auto const& _e : _s.set) {{ _m.multiset.insert(_e); }} return _m; }}(", elemName);
      wr.Append(Expr(expr.E, inLetExprBody, wStmts));
      wr.Write(")");
      return;
    }
    base.EmitMultiSetFormingExpr(expr, inLetExprBody, wr, wStmts);
  }

  // m[x] on a multiset is the multiplicity of x. (Map keeps the base spelling.)
  protected override void EmitIndexCollectionSelect(Expression source, Expression index, bool inLetExprBody,
      ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
    if (source.Type.NormalizeToAncestorType() is MultiSetType) {
      wr.Write("mpz_class((long)");
      TrParenExpr(source, wr, inLetExprBody, wStmts);
      wr.Write(".multiplicity(");
      wr.Append(Expr(index, inLetExprBody, wStmts));
      wr.Write("))");
      return;
    }
    if (source.Type.NormalizeToAncestorType() is SeqType) {
      // s[i]: DafnySequence::select takes a uint64, but in this target a Dafny
      // `int` index is an mpz_class. Narrow it (a native-int index passes through
      // unchanged). Base cpp never hits this because it rejects unbounded int.
      TrParenExpr(source, wr, inLetExprBody, wStmts);
      wr.Write(".select(");
      EmitIndexAsUint64(index, inLetExprBody, wr, wStmts);
      wr.Write(")");
      return;
    }
    base.EmitIndexCollectionSelect(source, index, inLetExprBody, wr, wStmts);
  }

  // Emit a collection index/bound as a uint64. A Dafny `int` index is an mpz_class
  // here, so extract it via get_ui(); a native-int index is emitted as-is.
  private void EmitIndexAsUint64(Expression index, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
    if (AsNativeType(index.Type) == null && index.Type.NormalizeToAncestorType() is IntType) {
      wr.Write("(uint64)(");
      wr.Append(Expr(index, inLetExprBody, wStmts));
      wr.Write(").get_ui()");
    } else {
      wr.Append(Expr(index, inLetExprBody, wStmts));
    }
  }

  // s[lo..hi] lowers to `.take(hi).drop(lo)` (and array slices to SeqFromArray*),
  // whose bounds are uint64. In this target a Dafny `int` bound is an mpz_class, so
  // narrow lo/hi. Only the seq (non-array) form is re-spelled here; the array-slice
  // form defers to the base (array<T> sources are largely rejected by this target).
  protected override void EmitSeqSelectRange(Expression source, Expression lo, Expression hi,
      bool fromArray, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
    if (!fromArray) {
      TrParenExpr(source, wr, inLetExprBody, wStmts);
      if (hi != null) {
        wr.Write(".take(");
        EmitIndexAsUint64(hi, inLetExprBody, wr, wStmts);
        wr.Write(")");
      }
      if (lo != null) {
        wr.Write(".drop(");
        EmitIndexAsUint64(lo, inLetExprBody, wr, wStmts);
        wr.Write(")");
      }
      return;
    }
    base.EmitSeqSelectRange(source, lo, hi, fromArray, inLetExprBody, wr, wStmts);
  }

  // |s| (Cardinality). The base emits `.size()`, a uint64. In this target a Dafny
  // `int` is an mpz_class, so a bare uint64 collides in mixed expressions like
  // `|s| == 0` (ambiguous operator==) or `|s| + x` (no mpz overload for ull).
  // Materialize the size as an mpz_class so |s| behaves as a Dafny int everywhere.
  protected override void EmitUnaryExpr(ResolvedUnaryOp op, Expression expr, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
    if (op == ResolvedUnaryOp.Cardinality) {
      wr.Write("mpz_class((long)");
      TrParenExpr(expr, wr, inLetExprBody, wStmts);
      wr.Write(".size())");
      return;
    }
    base.EmitUnaryExpr(op, expr, inLetExprBody, wr, wStmts);
  }

  // m[x := n] on a multiset sets x's multiplicity to n. The base emits
  // `.update(index, value)`, but DafnyMultiset::update takes a uint64 count while
  // the Dafny multiplicity `value` has type `int` (an mpz_class), so narrow it.
  // (Seq/map keep the base spelling, whose value is the element/range type.)
  protected override void EmitIndexCollectionUpdate(Expression source, Expression index, Expression value,
      CollectionType resultCollectionType, bool inLetExprBody, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts) {
    if (source.Type.NormalizeToAncestorType() is MultiSetType) {
      TrParenExpr(source, wr, inLetExprBody, wStmts);
      wr.Write(".update(");
      wr.Append(Expr(index, inLetExprBody, wStmts));
      wr.Write(", (uint64)(");
      wr.Append(Expr(value, inLetExprBody, wStmts));
      wr.Write(").get_ui())");
      return;
    }
    base.EmitIndexCollectionUpdate(source, index, value, resultCollectionType, inLetExprBody, wr, wStmts);
  }

  // ---- Arithmetic on unbounded int / exact real ---------------------------

  protected override void CompileBinOp(BinaryExpr.ResolvedOpcode op,
      Type e0Type, Type e1Type, IOrigin tok, Type resultType,
      out string opString, out string preOpString, out string postOpString,
      out string callString, out string staticCallString,
      out bool reverseArguments, out bool truncateResult,
      out bool convertE1_to_int, out bool coerceE1,
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

    var normResult = resultType.NormalizeToAncestorType();
    var nonNativeInt = normResult.IsNumericBased(Type.NumericPersuasion.Int) && AsNativeType(resultType) == null;
    var isReal = normResult.IsNumericBased(Type.NumericPersuasion.Real);

    // mpz_class (non-native int) and DafnyReal (real) both overload the arithmetic
    // operators, so most ops map straight to a C++ operator. The exceptions are
    // integer division/modulo, which Dafny defines as EUCLIDEAN (non-negative
    // remainder) whereas GMP's operators truncate toward zero — those go through
    // runtime helpers.
    if (nonNativeInt || isReal) {
      switch (op) {
        case BinaryExpr.ResolvedOpcode.Add: opString = "+"; return;
        case BinaryExpr.ResolvedOpcode.Sub: opString = "-"; return;
        case BinaryExpr.ResolvedOpcode.Mul: opString = "*"; return;
        case BinaryExpr.ResolvedOpcode.Div:
          if (isReal) {
            opString = "/";                       // exact rational division
          } else {
            staticCallString = "DafnyEuclideanDiv";
          }
          return;
        case BinaryExpr.ResolvedOpcode.Mod:
          // real has no modulo in Dafny; only int reaches here.
          staticCallString = "DafnyEuclideanMod";
          return;
        default:
          break;   // comparisons / equality fall through to the base handling
      }
    }

    base.CompileBinOp(op, e0Type, e1Type, tok, resultType,
      out opString, out preOpString, out postOpString, out callString,
      out staticCallString, out reverseArguments, out truncateResult,
      out convertE1_to_int, out coerceE1, errorWr);
  }

  // ---- Function values / lambdas ------------------------------------------

  protected override ConcreteSyntaxTree CreateLambda(List<Type> inTypes, IOrigin tok, List<string> inNames,
      Type resultType, ConcreteSyntaxTree wr, ConcreteSyntaxTree wStmts, bool untyped = false) {
    Contract.Assert(inTypes.Count == inNames.Count);
    wr.Write("[=](");
    for (var i = 0; i < inNames.Count; i++) {
      if (i != 0) {
        wr.Write(", ");
      }
      wr.Write("{0} {1}", TypeName(inTypes[i], wr, tok, null, false), inNames[i]);
    }
    var w = wr.NewExprBlock(") -> {0} ", TypeName(resultType, wr, tok, null, false));
    return w;
  }

  protected override ConcreteSyntaxTree EmitBetaRedex(List<string> boundVars, List<Expression> arguments,
      List<Type> boundTypes, Type resultType, IOrigin tok, bool inLetExprBody, ConcreteSyntaxTree wr,
      ref ConcreteSyntaxTree wStmts) {
    // An immediately-applied lambda: [=](T0 v0, ...) -> R { return <body>; }(args)
    wr.Write("[=](");
    for (var i = 0; i < boundVars.Count; i++) {
      if (i != 0) {
        wr.Write(", ");
      }
      wr.Write("{0} {1}", TypeName(boundTypes[i], wr, tok, null, false), boundVars[i]);
    }
    wr.Write(") -> {0} {{ return ", TypeName(resultType, wr, tok, null, false));
    var w = wr.Fork();
    wr.Write("; }");
    TrExprList(arguments, wr, inLetExprBody, wStmts);
    return w;
  }
}
