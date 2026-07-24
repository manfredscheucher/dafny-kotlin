// Copyright by the contributors to the Dafny Project
// SPDX-License-Identifier: MIT
//
// Minimal C runtime for the (experimental) Dafny-to-C backend.
//
// Unlike DafnyRuntimeCpp/DafnyRuntime.h this is plain C11: no <iostream>,
// no templates, no classes. See docs/DafnyRef/integration-c/IntegrationC.md for
// the supported feature set and the relation to the C++ backend.

#ifndef DAFNY_RUNTIME_C_H
#define DAFNY_RUNTIME_C_H

#include <stdint.h>
#include <stdbool.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <inttypes.h>
// GMP is only needed by the extended C target (unbounded int / exact real).
// The minimal `c` target rejects those features and compiles GMP-free.
#ifdef DAFNY_C_EXTENDED
#include <gmp.h>
#endif

// Native integer type names used by the code generator (same spelling as the
// C++ runtime, but plain typedefs here).
typedef uint8_t  uint8;
typedef uint16_t uint16;
typedef uint32_t uint32;
typedef uint64_t uint64;

typedef int8_t   int8;
typedef int16_t  int16;
typedef int32_t  int32;
typedef int64_t  int64;

// The Dafny `char` type. With --unicode-char:false a Dafny char is a 16-bit
// code unit, but a string is a seq<char> and cardinality |s| must count chars,
// not UTF-8 bytes. We therefore model a Dafny char as a 32-bit code point (wide
// enough for any code unit / code point) rather than a 1-byte C `char`. String
// literals are emitted by the generator as arrays of dafny_char code points,
// and printing UTF-8-encodes each code point (see dafny_print_seq_char). This
// keeps |s| == the Dafny character count while printing non-ASCII correctly.
typedef uint32_t dafny_char;

#ifdef DAFNY_C_EXTENDED
// ---------------------------------------------------------------------------
// Unbounded integers (Dafny `int`) via GMP mpz_t
//
// A Dafny `int` is an arbitrary-precision integer (like C#'s BigInteger). GMP's
// mpz_t is an array type, which is awkward to pass/return by value, so a Dafny
// int is represented as a POINTER to a heap-allocated wrapper struct. Values are
// immutable at the Dafny level: every operation allocates a fresh result. The
// backing store is never freed (arena/leak model): correctness over cleanup.
// ---------------------------------------------------------------------------

typedef struct DafnyInt_s { mpz_t v; } *DafnyInt;

static inline DafnyInt dafny_int_alloc(void) {
  DafnyInt r = (DafnyInt)malloc(sizeof(struct DafnyInt_s));
  mpz_init(r->v);
  return r;
}

static inline DafnyInt dafny_int_from_i64(long long x) {
  DafnyInt r = dafny_int_alloc();
  mpz_set_si(r->v, (long)x);
  // mpz_set_si takes a signed long; on LP64 (macOS/Linux) long is 64-bit so this
  // is exact for the full int64 range.
  return r;
}

static inline DafnyInt dafny_int_from_u64(unsigned long long x) {
  DafnyInt r = dafny_int_alloc();
  mpz_set_ui(r->v, (unsigned long)x);
  // Unsigned widening. mpz_set_ui takes an unsigned long; on LP64 that is 64-bit,
  // so values up to 2^64-1 (e.g. a uint64/bv64 with the top bit set) are exact.
  // Using the SIGNED dafny_int_from_i64 here would reinterpret such a value as
  // negative (0xFFFFFFFFFFFFFFFF -> -1). Route unsigned native sources here.
  return r;
}

static inline DafnyInt dafny_int_from_str(const char* s) {
  DafnyInt r = dafny_int_alloc();
  mpz_set_str(r->v, s, 10);
  return r;
}

static inline DafnyInt dafny_int_add(DafnyInt a, DafnyInt b) {
  DafnyInt r = dafny_int_alloc(); mpz_add(r->v, a->v, b->v); return r;
}
static inline DafnyInt dafny_int_sub(DafnyInt a, DafnyInt b) {
  DafnyInt r = dafny_int_alloc(); mpz_sub(r->v, a->v, b->v); return r;
}
static inline DafnyInt dafny_int_mul(DafnyInt a, DafnyInt b) {
  DafnyInt r = dafny_int_alloc(); mpz_mul(r->v, a->v, b->v); return r;
}

// Dafny uses Euclidean division/modulus: the remainder is ALWAYS non-negative
// (0 <= a mod b < |b|), and the quotient is a - (a mod b) then divided by b.
// GMP's mpz_fdiv (floor) matches Euclidean only when b > 0; for b < 0 the floor
// remainder is <= 0. We implement Euclidean directly on top of tdiv.
static inline DafnyInt dafny_int_div(DafnyInt a, DafnyInt b) {
  DafnyInt r = dafny_int_alloc();
  mpz_t q, rem; mpz_init(q); mpz_init(rem);
  mpz_tdiv_qr(q, rem, a->v, b->v);   // truncated: rem has sign of a
  if (mpz_sgn(rem) < 0) {
    if (mpz_sgn(b->v) > 0) { mpz_sub_ui(q, q, 1); }
    else { mpz_add_ui(q, q, 1); }
  }
  mpz_set(r->v, q);
  mpz_clear(q); mpz_clear(rem);
  return r;
}
static inline DafnyInt dafny_int_mod(DafnyInt a, DafnyInt b) {
  DafnyInt r = dafny_int_alloc();
  mpz_t rem; mpz_init(rem);
  mpz_tdiv_r(rem, a->v, b->v);       // truncated: rem has sign of a
  if (mpz_sgn(rem) < 0) {
    mpz_t ab; mpz_init(ab); mpz_abs(ab, b->v);
    mpz_add(rem, rem, ab);
    mpz_clear(ab);
  }
  mpz_set(r->v, rem);
  mpz_clear(rem);
  return r;
}

static inline bool dafny_int_eq(DafnyInt a, DafnyInt b) { return mpz_cmp(a->v, b->v) == 0; }
static inline bool dafny_int_ne(DafnyInt a, DafnyInt b) { return mpz_cmp(a->v, b->v) != 0; }
static inline bool dafny_int_lt(DafnyInt a, DafnyInt b) { return mpz_cmp(a->v, b->v) <  0; }
static inline bool dafny_int_le(DafnyInt a, DafnyInt b) { return mpz_cmp(a->v, b->v) <= 0; }
static inline bool dafny_int_gt(DafnyInt a, DafnyInt b) { return mpz_cmp(a->v, b->v) >  0; }
static inline bool dafny_int_ge(DafnyInt a, DafnyInt b) { return mpz_cmp(a->v, b->v) >= 0; }

// Conversion to a native machine integer (Dafny `x as intNN` / `as nat`).
static inline int64 dafny_int_to_i64(DafnyInt a) { return (int64)mpz_get_si(a->v); }
static inline uint64 dafny_int_to_u64(DafnyInt a) { return (uint64)mpz_get_ui(a->v); }
// Build a Dafny int from a native size (e.g. |s| cardinality, which is size_t).
static inline DafnyInt dafny_int_from_size(size_t x) {
  DafnyInt r = dafny_int_alloc(); mpz_set_ui(r->v, (unsigned long)x); return r;
}

static inline void dafny_print_int(DafnyInt a) { gmp_printf("%Zd", a->v); }
#endif // DAFNY_C_EXTENDED

// Euclidean division/modulus on native signed integers, matching Dafny's
// semantics (remainder is always in [0, |b|)). Used for signed native newtypes;
// the caller assigns the result back into the concrete (narrower) native type,
// which truncates harmlessly since the mathematical result is in range.
static inline int64 dafny_euclid_div_i64(int64 a, int64 b) {
  int64 q = a / b;
  int64 r = a - q * b;
  if (r < 0) { if (b > 0) { q -= 1; } else { q += 1; } }
  return q;
}
static inline int64 dafny_euclid_mod_i64(int64 a, int64 b) {
  int64 r = a % b;
  if (r < 0) { r += (b < 0) ? -b : b; }
  return r;
}

#ifdef DAFNY_C_EXTENDED
// ---------------------------------------------------------------------------
// Reals (Dafny `real`) as an UNREDUCED rational num/den, mirroring C#'s
// Dafny.BigRational.
//
// A Dafny `real` is an EXACT rational, not a float. We represent it as two
// mpz_t (num, den) with the invariant `1 <= den` (or num == 0). CRUCIALLY we do
// NOT keep it reduced: C#'s BigRational multiplies/divides WITHOUT reducing
// (`*` = num*num / den*den), so 1.5/0.5 is stored as 150/50, not 3/1. Its
// ToString then pads the decimal to the trailing-zero count implied by the
// unreduced denominator's 2/5 factors ("3.00", not "3.0"). Using GMP's mpq_t
// (always canonical/reduced) instead prints "3.0" and diverges from every other
// Dafny backend. So we mirror BigRational's exact num/den arithmetic here.
// Arena/leak model (never freed).
// ---------------------------------------------------------------------------

typedef struct DafnyReal_s { mpz_t num, den; } *DafnyReal;

static inline DafnyReal dafny_real_alloc(void) {
  DafnyReal r = (DafnyReal)malloc(sizeof(struct DafnyReal_s));
  mpz_init(r->num); mpz_init_set_ui(r->den, 1);
  return r;
}

// From a numerator/denominator decimal-string pair, e.g. "15","10" for 1.5.
// The frontend hands us the literal's exact num/den; keep them UNREDUCED so the
// printed precision matches C#.
static inline DafnyReal dafny_real_from_frac(const char* num, const char* den) {
  DafnyReal r = dafny_real_alloc();
  mpz_set_str(r->num, num, 10);
  mpz_set_str(r->den, den, 10);
  return r;
}

// C#'s BigRational.Normalize: reduce the two denominators by their gcd first, so
// the common denominator is den_a * (den_b/gcd), NOT the raw product. This is what
// makes 0.5+0.5 print "1.0" (den 10, not 100) and 0.75-0.25 print "0.50" (den 100,
// not 10000). If either operand is 0, the other's num/den is used as-is.
//   aa = a.num*yy, bb = b.num*xx, dd = a.den*yy   where xx=a.den/g, yy=b.den/g.
static inline void dafny__real_normalize(DafnyReal a, DafnyReal b, mpz_t aa, mpz_t bb, mpz_t dd) {
  if (mpz_sgn(a->num) == 0) {
    mpz_set(aa, a->num); mpz_set(bb, b->num); mpz_set(dd, b->den);
  } else if (mpz_sgn(b->num) == 0) {
    mpz_set(aa, a->num); mpz_set(dd, a->den); mpz_set(bb, b->num);
  } else {
    mpz_t g, xx, yy; mpz_init(g); mpz_init(xx); mpz_init(yy);
    mpz_gcd(g, a->den, b->den);
    mpz_divexact(xx, a->den, g);
    mpz_divexact(yy, b->den, g);
    mpz_mul(aa, a->num, yy);
    mpz_mul(bb, b->num, xx);
    mpz_mul(dd, a->den, yy);
    mpz_clear(g); mpz_clear(xx); mpz_clear(yy);
  }
}
static inline DafnyReal dafny_real_add(DafnyReal a, DafnyReal b) {
  DafnyReal r = dafny_real_alloc();
  mpz_t aa, bb, dd; mpz_init(aa); mpz_init(bb); mpz_init(dd);
  dafny__real_normalize(a, b, aa, bb, dd);
  mpz_add(r->num, aa, bb);
  mpz_set(r->den, dd);
  mpz_clear(aa); mpz_clear(bb); mpz_clear(dd);
  return r;
}
static inline DafnyReal dafny_real_sub(DafnyReal a, DafnyReal b) {
  DafnyReal r = dafny_real_alloc();
  mpz_t aa, bb, dd; mpz_init(aa); mpz_init(bb); mpz_init(dd);
  dafny__real_normalize(a, b, aa, bb, dd);
  mpz_sub(r->num, aa, bb);
  mpz_set(r->den, dd);
  mpz_clear(aa); mpz_clear(bb); mpz_clear(dd);
  return r;
}
static inline DafnyReal dafny_real_mul(DafnyReal a, DafnyReal b) {
  // BigRational: num*num / den*den, UNREDUCED.
  DafnyReal r = dafny_real_alloc();
  mpz_mul(r->num, a->num, b->num);
  mpz_mul(r->den, a->den, b->den);
  return r;
}
static inline DafnyReal dafny_real_div(DafnyReal a, DafnyReal b) {
  // BigRational: a * reciprocal(b), keeping den >= 1. reciprocal(b) = b.den/b.num
  // (with the sign moved onto the numerator when b.num < 0). UNREDUCED.
  DafnyReal r = dafny_real_alloc();
  if (mpz_sgn(b->num) > 0) {
    mpz_mul(r->num, a->num, b->den);
    mpz_mul(r->den, a->den, b->num);
  } else {
    // b.num < 0: reciprocal is (-b.den)/(-b.num) to keep den positive.
    mpz_t nden, nnum; mpz_init(nden); mpz_init(nnum);
    mpz_neg(nden, b->den);            // -b.den
    mpz_neg(nnum, b->num);            // -b.num (> 0)
    mpz_mul(r->num, a->num, nden);
    mpz_mul(r->den, a->den, nnum);
    mpz_clear(nden); mpz_clear(nnum);
  }
  return r;
}
// int -> real: num/1 from an unbounded integer.
static inline DafnyReal dafny_real_from_int(DafnyInt a) {
  DafnyReal r = dafny_real_alloc();
  mpz_set(r->num, a->v);
  mpz_set_ui(r->den, 1);
  return r;
}

// real -> int, flooring toward negative infinity (Dafny's `.Floor`, and the
// target for `r as int` when r is provably integral). Works on the unreduced
// fraction: floor(num/den) with den >= 1.
static inline DafnyInt dafny_int_from_real(DafnyReal a) {
  DafnyInt r = dafny_int_alloc();
  mpz_fdiv_q(r->v, a->num, a->den);   // floor division, den >= 1
  return r;
}

// Compare a<->b by cross-multiplication: sign(a.num*b.den - b.num*a.den), valid
// because both denominators are >= 1 (positive). Matches BigRational.CompareTo.
static inline int dafny_real_cmp(DafnyReal a, DafnyReal b) {
  mpz_t l, rr; mpz_init(l); mpz_init(rr);
  mpz_mul(l, a->num, b->den);
  mpz_mul(rr, b->num, a->den);
  int c = mpz_cmp(l, rr);
  mpz_clear(l); mpz_clear(rr);
  return c;
}
static inline bool dafny_real_eq(DafnyReal a, DafnyReal b) { return dafny_real_cmp(a, b) == 0; }
static inline bool dafny_real_ne(DafnyReal a, DafnyReal b) { return dafny_real_cmp(a, b) != 0; }
static inline bool dafny_real_lt(DafnyReal a, DafnyReal b) { return dafny_real_cmp(a, b) <  0; }
static inline bool dafny_real_le(DafnyReal a, DafnyReal b) { return dafny_real_cmp(a, b) <= 0; }
static inline bool dafny_real_gt(DafnyReal a, DafnyReal b) { return dafny_real_cmp(a, b) >  0; }
static inline bool dafny_real_ge(DafnyReal a, DafnyReal b) { return dafny_real_cmp(a, b) >= 0; }

// Print a real EXACTLY the way Dafny's BigRational.ToString does:
//   * whole numbers ("num == 0 || den == 1")           -> "<num>.0"
//   * terminating decimals (den has only 2/5 factors)  -> "<int>.<frac>"
//   * everything else (non-terminating, e.g. 1/3)      -> "(<num>.0 / <den>.0)"
// The last case is the one that matters most: never print a wrong finite value
// for a non-terminating rational. Because the GMP mpq_t value is always kept in
// lowest terms, num/den here is the reduced fraction (matches Dafny for coprime
// inputs; for terminating decimals the trailing-zero count may differ from
// Dafny's unreduced form, but the numeric value is always exact).
//
// DividesAPowerOf10: returns nonzero if `d` (>=1) divides some 10^k, i.e. it has
// only factors 2 and 5. On success *factor and *log10 satisfy
//   10^log10 == (*factor) * d
static inline int dafny__divides_pow10(const mpz_t d_in, mpz_t factor, int *log10) {
  mpz_t i; mpz_init_set(i, d_in);
  mpz_set_ui(factor, 1);
  *log10 = 0;
  if (mpz_sgn(i) <= 0) { mpz_clear(i); return 0; }
  while (mpz_divisible_ui_p(i, 10)) { mpz_divexact_ui(i, i, 10); (*log10)++; }
  while (mpz_divisible_ui_p(i, 5))  { mpz_divexact_ui(i, i, 5);  mpz_mul_ui(factor, factor, 2); (*log10)++; }
  while (mpz_divisible_ui_p(i, 2))  { mpz_divexact_ui(i, i, 2);  mpz_mul_ui(factor, factor, 5); (*log10)++; }
  int ok = (mpz_cmp_ui(i, 1) == 0);
  mpz_clear(i);
  return ok;
}

static inline void dafny_print_real(DafnyReal a) {
  // Print EXACTLY like Dafny's BigRational.ToString, operating on the UNREDUCED
  // num/den (so trailing-zero precision matches C#, e.g. 150/50 -> "3.00").
  mpz_t num, den; mpz_init_set(num, a->num); mpz_init_set(den, a->den);

  if (mpz_sgn(num) == 0 || mpz_cmp_ui(den, 1) == 0) {
    gmp_printf("%Zd.0", num);
    mpz_clear(num); mpz_clear(den);
    return;
  }

  mpz_t factor; mpz_init(factor);
  int log10;
  if (dafny__divides_pow10(den, factor, &log10)) {
    // n = num * factor; digits of |n|; place decimal point log10 from the right.
    mpz_t n; mpz_init(n);
    mpz_mul(n, num, factor);
    int neg = (mpz_sgn(n) < 0);
    if (neg) { mpz_neg(n, n); }
    char *digits = mpz_get_str(NULL, 10, n);   // malloc'd, no sign
    int dlen = (int)strlen(digits);
    if (neg) { putchar('-'); }
    if (log10 < dlen) {
      int digitCount = dlen - log10;
      for (int k = 0; k < digitCount; k++) { putchar(digits[k]); }
      putchar('.');
      for (int k = digitCount; k < dlen; k++) { putchar(digits[k]); }
    } else {
      // 0.<zeros><digits>
      putchar('0'); putchar('.');
      for (int k = 0; k < log10 - dlen; k++) { putchar('0'); }
      for (int k = 0; k < dlen; k++) { putchar(digits[k]); }
    }
    free(digits);
    mpz_clear(n);
  } else {
    // Non-terminating: exact fraction form "(num.0 / den.0)".
    gmp_printf("(%Zd.0 / %Zd.0)", num, den);
  }

  mpz_clear(factor);
  mpz_clear(num); mpz_clear(den);
}
#endif // DAFNY_C_EXTENDED

// ---------------------------------------------------------------------------
// Sequences (seq<T>) and strings (seq<char>)
//
// C has no templates, so – exactly like the generic methods/functions in this
// backend – sequences are monomorphised: the code generator emits one concrete
// struct + set of helpers per element type actually used, via the macros below.
// A sequence value is a (data pointer, length) pair. The backing store is never
// freed (arena/leak model): correctness over cleanup.
//
// For an element type ELEM with a C-identifier-safe suffix NAME the generator
// emits DAFNY_SEQ_DECL(NAME, ELEM) into the header and DAFNY_SEQ_DEFINE(NAME,
// ELEM) into the source. This produces:
//
//   typedef struct { ELEM* data; size_t len; } DafnySequence_NAME;
//   DafnySequence_NAME dafny_seq_NAME_create(size_t n, const ELEM* items);
//   size_t             dafny_seq_NAME_size(DafnySequence_NAME s);
//   ELEM               dafny_seq_NAME_select(DafnySequence_NAME s, size_t i);
//   DafnySequence_NAME dafny_seq_NAME_concat(DafnySequence_NAME a, DafnySequence_NAME b);
//   bool               dafny_seq_NAME_equals(DafnySequence_NAME a, DafnySequence_NAME b);
//   DafnySequence_NAME dafny_seq_NAME_take(DafnySequence_NAME s, size_t hi);
//   DafnySequence_NAME dafny_seq_NAME_drop(DafnySequence_NAME s, size_t lo);
//   DafnySequence_NAME dafny_seq_NAME_from_array(ELEM* a, size_t n);
// ---------------------------------------------------------------------------

// EQFN(a, b) is the VALUE equality on the element type; HASHFN(x) is its value
// hash. For primitive elements the generator passes the DAFNY_PRIM_HASH/EQ macros;
// for pointer-backed elements (int, real, nested seq) it passes value ones, so
// seq equality/hashing is correct for any element type.
#define DAFNY_SEQ_DECL(NAME, ELEM) \
  typedef struct { ELEM* data; size_t len; } DafnySequence_##NAME; \
  DafnySequence_##NAME dafny_seq_##NAME##_create(size_t n, const ELEM* items); \
  size_t dafny_seq_##NAME##_size(DafnySequence_##NAME s); \
  ELEM dafny_seq_##NAME##_select(DafnySequence_##NAME s, size_t i); \
  DafnySequence_##NAME dafny_seq_##NAME##_concat(DafnySequence_##NAME a, DafnySequence_##NAME b); \
  bool dafny_seq_##NAME##_equals(DafnySequence_##NAME a, DafnySequence_##NAME b); \
  uint64_t dafny_seq_##NAME##_hash(DafnySequence_##NAME s); \
  DafnySequence_##NAME dafny_seq_##NAME##_take(DafnySequence_##NAME s, size_t hi); \
  DafnySequence_##NAME dafny_seq_##NAME##_drop(DafnySequence_##NAME s, size_t lo); \
  DafnySequence_##NAME dafny_seq_##NAME##_from_array(ELEM* a, size_t n); \
  bool dafny_seq_##NAME##_contains(DafnySequence_##NAME s, ELEM x); \
  bool dafny_seq_##NAME##_is_prefix(DafnySequence_##NAME a, DafnySequence_##NAME b); \
  bool dafny_seq_##NAME##_is_proper_prefix(DafnySequence_##NAME a, DafnySequence_##NAME b); \
  DafnySequence_##NAME dafny_seq_##NAME##_update(DafnySequence_##NAME s, size_t i, ELEM x);

#define DAFNY_SEQ_DEFINE(NAME, ELEM, HASHFN, EQFN) \
  DafnySequence_##NAME dafny_seq_##NAME##_create(size_t n, const ELEM* items) { \
    DafnySequence_##NAME s; \
    s.len = n; \
    s.data = n == 0 ? NULL : (ELEM*)malloc(n * sizeof(ELEM)); \
    for (size_t _i = 0; _i < n; _i++) { s.data[_i] = items[_i]; } \
    return s; \
  } \
  size_t dafny_seq_##NAME##_size(DafnySequence_##NAME s) { return s.len; } \
  ELEM dafny_seq_##NAME##_select(DafnySequence_##NAME s, size_t i) { return s.data[i]; } \
  DafnySequence_##NAME dafny_seq_##NAME##_concat(DafnySequence_##NAME a, DafnySequence_##NAME b) { \
    DafnySequence_##NAME s; \
    s.len = a.len + b.len; \
    s.data = s.len == 0 ? NULL : (ELEM*)malloc(s.len * sizeof(ELEM)); \
    for (size_t _i = 0; _i < a.len; _i++) { s.data[_i] = a.data[_i]; } \
    for (size_t _i = 0; _i < b.len; _i++) { s.data[a.len + _i] = b.data[_i]; } \
    return s; \
  } \
  bool dafny_seq_##NAME##_equals(DafnySequence_##NAME a, DafnySequence_##NAME b) { \
    if (a.len != b.len) { return false; } \
    for (size_t _i = 0; _i < a.len; _i++) { if (!(EQFN(a.data[_i], b.data[_i]))) { return false; } } \
    return true; \
  } \
  uint64_t dafny_seq_##NAME##_hash(DafnySequence_##NAME s) { \
    uint64_t _h = 1469598103934665603ULL; \
    for (size_t _i = 0; _i < s.len; _i++) { _h = dafny_hash_combine(_h, (HASHFN(s.data[_i]))); } \
    return _h; \
  } \
  DafnySequence_##NAME dafny_seq_##NAME##_take(DafnySequence_##NAME s, size_t hi) { \
    return dafny_seq_##NAME##_create(hi, s.data); \
  } \
  DafnySequence_##NAME dafny_seq_##NAME##_drop(DafnySequence_##NAME s, size_t lo) { \
    return dafny_seq_##NAME##_create(s.len - lo, s.data + lo); \
  } \
  DafnySequence_##NAME dafny_seq_##NAME##_from_array(ELEM* a, size_t n) { \
    return dafny_seq_##NAME##_create(n, a); \
  } \
  bool dafny_seq_##NAME##_contains(DafnySequence_##NAME s, ELEM x) { \
    for (size_t _i = 0; _i < s.len; _i++) { if (EQFN(s.data[_i], x)) { return true; } } \
    return false; \
  } \
  bool dafny_seq_##NAME##_is_prefix(DafnySequence_##NAME a, DafnySequence_##NAME b) { \
    /* a <= b : a is a prefix of b (a.len <= b.len and elements match). */ \
    if (a.len > b.len) { return false; } \
    for (size_t _i = 0; _i < a.len; _i++) { if (!(EQFN(a.data[_i], b.data[_i]))) { return false; } } \
    return true; \
  } \
  bool dafny_seq_##NAME##_is_proper_prefix(DafnySequence_##NAME a, DafnySequence_##NAME b) { \
    return a.len < b.len && dafny_seq_##NAME##_is_prefix(a, b); \
  } \
  /* s[i := x] : a fresh copy of s with element i replaced by x. */ \
  DafnySequence_##NAME dafny_seq_##NAME##_update(DafnySequence_##NAME s, size_t i, ELEM x) { \
    DafnySequence_##NAME r = dafny_seq_##NAME##_create(s.len, s.data); \
    if (i < r.len) { r.data[i] = x; } \
    return r; \
  }

// ---------------------------------------------------------------------------
// Arrays (array<T>, 1-dimensional)
//
// C has no templates, so – exactly like seq/set/map above – a 1-D array<T> is
// monomorphised: the generator emits one concrete struct + helpers per element
// type actually used, via the macros below. An array value is a (data pointer,
// length) pair, mirroring how the reference (C#/Java) backends model an array
// reference. The backing store is never freed (arena/leak model).
//
// Unlike a seq, an array is MUTABLE in place: a[i] := v writes data[i]. The
// generator emits struct-member expressions ((a).data[i]) directly for reads,
// writes and .Length; the only helper is the allocator, which zero-initialises
// the backing store to the element default (matching how C#/Dafny zero-init a
// freshly-allocated array: 0 / false / null / default(struct)).
//
// For an element type ELEM with a C-identifier-safe suffix NAME the generator
// emits DAFNY_ARRAY_DECL(NAME, ELEM) into the header and DAFNY_ARRAY_DEFINE(
// NAME, ELEM, DEFAULT) into the source. This produces:
//
//   typedef struct { ELEM* data; size_t len; } DafnyArray_NAME;
//   DafnyArray_NAME dafny_array_NAME_new(size_t n);   // zero-init to DEFAULT
// ---------------------------------------------------------------------------
#define DAFNY_ARRAY_DECL(NAME, ELEM) \
  typedef struct { ELEM* data; size_t len; } DafnyArray_##NAME; \
  DafnyArray_##NAME dafny_array_##NAME##_new(size_t n);

#define DAFNY_ARRAY_DEFINE(NAME, ELEM, DEFAULT) \
  DafnyArray_##NAME dafny_array_##NAME##_new(size_t n) { \
    DafnyArray_##NAME a; \
    a.len = n; \
    a.data = n == 0 ? NULL : (ELEM*)malloc(n * sizeof(ELEM)); \
    for (size_t _i = 0; _i < n; _i++) { a.data[_i] = (DEFAULT); } \
    return a; \
  }

// ---------------------------------------------------------------------------
// Hash helper for set/map keys.
//
// Set elements and map keys are primitive C values (bool, native integers,
// char). We hash the raw object bytes with 64-bit FNV-1a. This is used by the
// open-addressing (linear-probing) hash tables the set and map macros below
// build. Equality still uses plain C `==` on the value, which is correct for
// these primitive types.
static inline uint64_t dafny_hash_bytes(const void* p, size_t n) {
  const unsigned char* b = (const unsigned char*)p;
  uint64_t h = 1469598103934665603ULL; /* FNV offset basis */
  for (size_t _i = 0; _i < n; _i++) {
    h ^= (uint64_t)b[_i];
    h *= 1099511628211ULL;             /* FNV prime */
  }
  return h;
}

// Combine two 64-bit hashes (used to fold element hashes into a sequence hash).
static inline uint64_t dafny_hash_combine(uint64_t h, uint64_t x) {
  h ^= x + 0x9e3779b97f4a7c15ULL + (h << 6) + (h >> 2);
  return h;
}

// ---------------------------------------------------------------------------
// Value-based hash/eq for set elements and map keys.
//
// Open-addressing hash tables (the set/map/multiset macros below) locate slots
// by a HASH of the element/key and confirm identity with an EQ. Hashing the raw
// object bytes and comparing with C `==` is correct ONLY for primitive value
// types (bool, native ints, char): two equal such values have identical bytes.
//
// It is WRONG for types whose C representation is a pointer or a struct with
// pointers, where two logically-equal values live at different addresses:
//   * Dafny `int`  -> DafnyInt  (pointer to an mpz wrapper)
//   * Dafny `real` -> DafnyReal (pointer to an mpq wrapper)
//   * seq<T>/string -> { T* data; size_t len; }
// For those we must hash/compare by VALUE. The generator instantiates each set/
// map/multiset with an explicit HASH/EQ pair chosen per element type: the
// DAFNY_PRIM_HASH / DAFNY_PRIM_EQ macros (byte hash / `==`) for primitive types,
// dafny_hash_int / dafny_int_eq (etc.) for int/real, and the recursive
// dafny_seq_<NAME>_hash / _equals for sequences. No per-type wrapper is emitted.
// ---------------------------------------------------------------------------

#ifdef DAFNY_C_EXTENDED
// Value hash/eq for Dafny `int` (mpz). Two equal mpz values must hash equally,
// so we hash the canonical exported limbs (mpz_export writes the value's
// magnitude big-endian) plus the sign.
static inline uint64_t dafny_hash_int(DafnyInt a) {
  size_t count = 0;
  size_t size = (mpz_sizeinbase(a->v, 2) + 7) / 8;
  unsigned char* buf = (unsigned char*)malloc(size + 1);
  buf[0] = 0;
  if (size > 0) { mpz_export(buf, &count, 1, 1, 0, 0, a->v); }
  uint64_t h = dafny_hash_bytes(buf, count);
  int sgn = mpz_sgn(a->v);
  h = dafny_hash_combine(h, (uint64_t)(sgn < 0 ? 1 : 0));
  free(buf);
  return h;
}

// Value hash for Dafny `real`. The stored num/den are UNREDUCED, so equal values
// (e.g. 3/1 and 150/50) have different num/den; we must hash the REDUCED form so
// they collide, matching dafny_real_eq (which compares by value). Reduce a copy
// via the gcd, keeping den > 0, then hash num and den.
static inline uint64_t dafny_hash_real(DafnyReal a) {
  mpz_t num, den, g; mpz_init_set(num, a->num); mpz_init_set(den, a->den); mpz_init(g);
  if (mpz_sgn(num) == 0) {
    mpz_set_ui(den, 1);               // 0/d normalizes to 0/1
  } else {
    mpz_gcd(g, num, den);
    mpz_divexact(num, num, g);
    mpz_divexact(den, den, g);
    if (mpz_sgn(den) < 0) { mpz_neg(num, num); mpz_neg(den, den); }
  }
  struct DafnyInt_s n; n.v[0] = num[0];
  struct DafnyInt_s d; d.v[0] = den[0];
  uint64_t h = dafny_hash_int(&n);
  h = dafny_hash_combine(h, dafny_hash_int(&d));
  mpz_clear(num); mpz_clear(den); mpz_clear(g);
  return h;
}
#endif // DAFNY_C_EXTENDED

// ---------------------------------------------------------------------------
// Sets (set<T>)
//
// Like sequences, sets are monomorphised: the generator emits one concrete
// struct + set of helpers per element type actually used, via the macros below.
// A set value is an open-addressing hash table (linear probing) with dedup on
// insert:
//   struct { T* slots; bool* used; size_t cap; size_t len; }
// Element equality uses plain C `==`, which is correct for the reachable element
// types (bool, native integers, char); slots are located by hashing the raw
// element bytes (dafny_hash_bytes) and probing. Lookup/insert/membership are
// amortized O(1). The backing store is never freed (arena/leak model), and every
// operation that "changes" a set returns a NEW table (value semantics).
//
// For an element type ELEM with a C-identifier-safe suffix NAME the generator
// emits DAFNY_SET_DECL(NAME, ELEM) into the header and DAFNY_SET_DEFINE(NAME,
// ELEM) into the source. This produces:
//
//   typedef struct { ELEM* data; size_t len; } DafnySet_NAME;
//   DafnySet_NAME dafny_set_NAME_create(size_t n, const ELEM* items); // dedups
//   size_t        dafny_set_NAME_size(DafnySet_NAME s);
//   bool          dafny_set_NAME_contains(DafnySet_NAME s, ELEM x);
//   DafnySet_NAME dafny_set_NAME_union(DafnySet_NAME a, DafnySet_NAME b);
//   DafnySet_NAME dafny_set_NAME_intersection(DafnySet_NAME a, DafnySet_NAME b);
//   DafnySet_NAME dafny_set_NAME_difference(DafnySet_NAME a, DafnySet_NAME b);
//   bool          dafny_set_NAME_subset(DafnySet_NAME a, DafnySet_NAME b);   // a<=b
//   bool          dafny_set_NAME_equals(DafnySet_NAME a, DafnySet_NAME b);
// ---------------------------------------------------------------------------

#define DAFNY_SET_DECL(NAME, ELEM) \
  typedef struct { ELEM* slots; bool* used; size_t cap; size_t len; } DafnySet_##NAME; \
  DafnySet_##NAME dafny_set_##NAME##_create(size_t n, const ELEM* items); \
  size_t dafny_set_##NAME##_size(DafnySet_##NAME s); \
  bool dafny_set_##NAME##_contains(DafnySet_##NAME s, ELEM x); \
  DafnySet_##NAME dafny_set_##NAME##_union(DafnySet_##NAME a, DafnySet_##NAME b); \
  DafnySet_##NAME dafny_set_##NAME##_intersection(DafnySet_##NAME a, DafnySet_##NAME b); \
  DafnySet_##NAME dafny_set_##NAME##_difference(DafnySet_##NAME a, DafnySet_##NAME b); \
  bool dafny_set_##NAME##_subset(DafnySet_##NAME a, DafnySet_##NAME b); \
  bool dafny_set_##NAME##_proper_subset(DafnySet_##NAME a, DafnySet_##NAME b); \
  bool dafny_set_##NAME##_equals(DafnySet_##NAME a, DafnySet_##NAME b); \
  bool dafny_set_##NAME##_disjoint(DafnySet_##NAME a, DafnySet_##NAME b);

#define DAFNY_SET_DEFINE(NAME, ELEM, HASHFN, EQFN) \
  /* Pick a power-of-two capacity strictly greater than n (>= 8), leaving spare \
     slots so linear probing always terminates on an empty slot. */ \
  static size_t dafny_set_##NAME##_cap_for(size_t n) { \
    size_t _c = 8; \
    while (_c <= n * 2) { _c <<= 1; } \
    return _c; \
  } \
  static DafnySet_##NAME dafny_set_##NAME##_alloc(size_t cap) { \
    DafnySet_##NAME s; s.cap = cap; s.len = 0; \
    s.slots = (ELEM*)malloc(cap * sizeof(ELEM)); \
    s.used = (bool*)calloc(cap, sizeof(bool)); \
    return s; \
  } \
  /* Insert x if absent (dedup). Assumes at least one free slot. */ \
  static void dafny_set_##NAME##_insert(DafnySet_##NAME* s, ELEM x) { \
    size_t _mask = s->cap - 1; \
    size_t _i = (size_t)(HASHFN(x)) & _mask; \
    while (s->used[_i]) { \
      if (EQFN(s->slots[_i], x)) { return; } \
      _i = (_i + 1) & _mask; \
    } \
    s->used[_i] = true; s->slots[_i] = x; s->len++; \
  } \
  bool dafny_set_##NAME##_contains(DafnySet_##NAME s, ELEM x) { \
    if (s.cap == 0) { return false; } \
    size_t _mask = s.cap - 1; \
    size_t _i = (size_t)(HASHFN(x)) & _mask; \
    while (s.used[_i]) { \
      if (EQFN(s.slots[_i], x)) { return true; } \
      _i = (_i + 1) & _mask; \
    } \
    return false; \
  } \
  DafnySet_##NAME dafny_set_##NAME##_create(size_t n, const ELEM* items) { \
    DafnySet_##NAME s = dafny_set_##NAME##_alloc(dafny_set_##NAME##_cap_for(n)); \
    for (size_t _i = 0; _i < n; _i++) { dafny_set_##NAME##_insert(&s, items[_i]); } \
    return s; \
  } \
  size_t dafny_set_##NAME##_size(DafnySet_##NAME s) { return s.len; } \
  DafnySet_##NAME dafny_set_##NAME##_union(DafnySet_##NAME a, DafnySet_##NAME b) { \
    DafnySet_##NAME r = dafny_set_##NAME##_alloc(dafny_set_##NAME##_cap_for(a.len + b.len)); \
    for (size_t _i = 0; _i < a.cap; _i++) { if (a.used[_i]) { dafny_set_##NAME##_insert(&r, a.slots[_i]); } } \
    for (size_t _i = 0; _i < b.cap; _i++) { if (b.used[_i]) { dafny_set_##NAME##_insert(&r, b.slots[_i]); } } \
    return r; \
  } \
  DafnySet_##NAME dafny_set_##NAME##_intersection(DafnySet_##NAME a, DafnySet_##NAME b) { \
    DafnySet_##NAME r = dafny_set_##NAME##_alloc(dafny_set_##NAME##_cap_for(a.len)); \
    for (size_t _i = 0; _i < a.cap; _i++) { \
      if (a.used[_i] && dafny_set_##NAME##_contains(b, a.slots[_i])) { dafny_set_##NAME##_insert(&r, a.slots[_i]); } \
    } \
    return r; \
  } \
  DafnySet_##NAME dafny_set_##NAME##_difference(DafnySet_##NAME a, DafnySet_##NAME b) { \
    DafnySet_##NAME r = dafny_set_##NAME##_alloc(dafny_set_##NAME##_cap_for(a.len)); \
    for (size_t _i = 0; _i < a.cap; _i++) { \
      if (a.used[_i] && !dafny_set_##NAME##_contains(b, a.slots[_i])) { dafny_set_##NAME##_insert(&r, a.slots[_i]); } \
    } \
    return r; \
  } \
  bool dafny_set_##NAME##_subset(DafnySet_##NAME a, DafnySet_##NAME b) { \
    for (size_t _i = 0; _i < a.cap; _i++) { \
      if (a.used[_i] && !dafny_set_##NAME##_contains(b, a.slots[_i])) { return false; } \
    } \
    return true; \
  } \
  bool dafny_set_##NAME##_equals(DafnySet_##NAME a, DafnySet_##NAME b) { \
    return a.len == b.len && dafny_set_##NAME##_subset(a, b); \
  } \
  bool dafny_set_##NAME##_proper_subset(DafnySet_##NAME a, DafnySet_##NAME b) { \
    /* a < b : a subset of b AND a != b (strictly fewer elements). */ \
    return a.len < b.len && dafny_set_##NAME##_subset(a, b); \
  } \
  bool dafny_set_##NAME##_disjoint(DafnySet_##NAME a, DafnySet_##NAME b) { \
    /* a !! b : no shared element. */ \
    for (size_t _i = 0; _i < a.cap; _i++) { \
      if (a.used[_i] && dafny_set_##NAME##_contains(b, a.slots[_i])) { return false; } \
    } \
    return true; \
  }

// ---------------------------------------------------------------------------
// Maps (map<K,V>)
//
// Monomorphised per (key, value) type pair. A map value is an open-addressing
// hash table (linear probing) with unique keys:
//   struct { K* keys; V* vals; bool* used; size_t cap; size_t len; }
// Key equality uses plain C `==` (correct for bool / native ints / char); keys
// are located by hashing their raw bytes (dafny_hash_bytes) and probing.
// Lookup/insert/membership are amortized O(1). The backing store is never freed
// (arena/leak model), and every operation that "changes" a map returns a NEW
// table (value semantics).
//
// For a key type KEY / value type VAL with C-identifier-safe suffix NAME the
// generator emits DAFNY_MAP_DECL(NAME, KEY, VAL) into the header and
// DAFNY_MAP_DEFINE(NAME, KEY, VAL) into the source. This produces:
//
//   typedef struct { KEY* keys; VAL* vals; size_t len; } DafnyMap_NAME;
//   DafnyMap_NAME dafny_map_NAME_create(size_t n, const KEY* ks, const VAL* vs);
//   size_t        dafny_map_NAME_size(DafnyMap_NAME m);
//   bool          dafny_map_NAME_contains_key(DafnyMap_NAME m, KEY k);
//   VAL           dafny_map_NAME_get(DafnyMap_NAME m, KEY k);
//   DafnyMap_NAME dafny_map_NAME_update(DafnyMap_NAME m, KEY k, VAL v);
//   bool          dafny_map_NAME_equals(DafnyMap_NAME a, DafnyMap_NAME b);
// ---------------------------------------------------------------------------

#define DAFNY_MAP_DECL(NAME, KEY, VAL) \
  typedef struct { KEY* keys; VAL* vals; bool* used; size_t cap; size_t len; } DafnyMap_##NAME; \
  DafnyMap_##NAME dafny_map_##NAME##_create(size_t n, const KEY* ks, const VAL* vs); \
  size_t dafny_map_##NAME##_size(DafnyMap_##NAME m); \
  bool dafny_map_##NAME##_contains_key(DafnyMap_##NAME m, KEY k); \
  VAL dafny_map_##NAME##_get(DafnyMap_##NAME m, KEY k); \
  DafnyMap_##NAME dafny_map_##NAME##_update(DafnyMap_##NAME m, KEY k, VAL v); \
  bool dafny_map_##NAME##_equals(DafnyMap_##NAME a, DafnyMap_##NAME b); \
  DafnyMap_##NAME dafny_map_##NAME##_merge(DafnyMap_##NAME a, DafnyMap_##NAME b);

/* KHASHFN(k)/KEQFN(a,b): value hash/eq on the KEY type. VEQFN(a,b): value eq on \
   the VAL type (used only by map equality). Both key and value equality are \
   value-based, so int/real/seq keys AND values work correctly. */ \
#define DAFNY_MAP_DEFINE(NAME, KEY, VAL, KHASHFN, KEQFN, VEQFN) \
  static size_t dafny_map_##NAME##_cap_for(size_t n) { \
    size_t _c = 8; \
    while (_c <= n * 2) { _c <<= 1; } \
    return _c; \
  } \
  static DafnyMap_##NAME dafny_map_##NAME##_alloc(size_t cap) { \
    DafnyMap_##NAME m; m.cap = cap; m.len = 0; \
    m.keys = (KEY*)malloc(cap * sizeof(KEY)); \
    m.vals = (VAL*)malloc(cap * sizeof(VAL)); \
    m.used = (bool*)calloc(cap, sizeof(bool)); \
    return m; \
  } \
  /* Insert k->v, last-wins on duplicate key. Assumes at least one free slot. */ \
  static void dafny_map_##NAME##_put(DafnyMap_##NAME* m, KEY k, VAL v) { \
    size_t _mask = m->cap - 1; \
    size_t _i = (size_t)(KHASHFN(k)) & _mask; \
    while (m->used[_i]) { \
      if (KEQFN(m->keys[_i], k)) { m->vals[_i] = v; return; } \
      _i = (_i + 1) & _mask; \
    } \
    m->used[_i] = true; m->keys[_i] = k; m->vals[_i] = v; m->len++; \
  } \
  DafnyMap_##NAME dafny_map_##NAME##_create(size_t n, const KEY* ks, const VAL* vs) { \
    DafnyMap_##NAME m = dafny_map_##NAME##_alloc(dafny_map_##NAME##_cap_for(n)); \
    for (size_t _i = 0; _i < n; _i++) { dafny_map_##NAME##_put(&m, ks[_i], vs[_i]); } \
    return m; \
  } \
  size_t dafny_map_##NAME##_size(DafnyMap_##NAME m) { return m.len; } \
  bool dafny_map_##NAME##_contains_key(DafnyMap_##NAME m, KEY k) { \
    if (m.cap == 0) { return false; } \
    size_t _mask = m.cap - 1; \
    size_t _i = (size_t)(KHASHFN(k)) & _mask; \
    while (m.used[_i]) { \
      if (KEQFN(m.keys[_i], k)) { return true; } \
      _i = (_i + 1) & _mask; \
    } \
    return false; \
  } \
  VAL dafny_map_##NAME##_get(DafnyMap_##NAME m, KEY k) { \
    if (m.cap != 0) { \
      size_t _mask = m.cap - 1; \
      size_t _i = (size_t)(KHASHFN(k)) & _mask; \
      while (m.used[_i]) { \
        if (KEQFN(m.keys[_i], k)) { return m.vals[_i]; } \
        _i = (_i + 1) & _mask; \
      } \
    } \
    VAL _zero; memset(&_zero, 0, sizeof(VAL)); return _zero; \
  } \
  DafnyMap_##NAME dafny_map_##NAME##_update(DafnyMap_##NAME m, KEY k, VAL v) { \
    DafnyMap_##NAME r = dafny_map_##NAME##_alloc(dafny_map_##NAME##_cap_for(m.len + 1)); \
    for (size_t _i = 0; _i < m.cap; _i++) { \
      if (m.used[_i]) { dafny_map_##NAME##_put(&r, m.keys[_i], m.vals[_i]); } \
    } \
    dafny_map_##NAME##_put(&r, k, v); \
    return r; \
  } \
  bool dafny_map_##NAME##_equals(DafnyMap_##NAME a, DafnyMap_##NAME b) { \
    if (a.len != b.len) { return false; } \
    for (size_t _i = 0; _i < a.cap; _i++) { \
      if (!a.used[_i]) { continue; } \
      if (!dafny_map_##NAME##_contains_key(b, a.keys[_i])) { return false; } \
      if (!(VEQFN(dafny_map_##NAME##_get(b, a.keys[_i]), a.vals[_i]))) { return false; } \
    } \
    return true; \
  } \
  /* a + b : union of entries; on a shared key, b's value wins (Dafny map merge). */ \
  DafnyMap_##NAME dafny_map_##NAME##_merge(DafnyMap_##NAME a, DafnyMap_##NAME b) { \
    DafnyMap_##NAME r = dafny_map_##NAME##_alloc(dafny_map_##NAME##_cap_for(a.len + b.len)); \
    for (size_t _i = 0; _i < a.cap; _i++) { if (a.used[_i]) { dafny_map_##NAME##_put(&r, a.keys[_i], a.vals[_i]); } } \
    for (size_t _i = 0; _i < b.cap; _i++) { if (b.used[_i]) { dafny_map_##NAME##_put(&r, b.keys[_i], b.vals[_i]); } } \
    return r; \
  }

// ---------------------------------------------------------------------------
// Multisets (multiset<T>)
//
// Monomorphised per element type, exactly like sets and maps. A multiset is a
// map from element to a positive multiplicity (count). It is backed by an
// open-addressing hash table (linear probing) storing, for each distinct
// element, its count:
//   struct { ELEM* slots; uint64_t* counts; bool* used;
//            size_t cap; size_t len /*distinct*/; size_t total /*sum counts*/; }
// Element equality uses plain C `==` (correct for bool / native ints / char);
// slots are located by hashing the raw element bytes (dafny_hash_bytes) and
// probing. The backing store is never freed (arena/leak model), and every
// operation that "changes" a multiset returns a NEW table (value semantics).
//
// For an element type ELEM with a C-identifier-safe suffix NAME the generator
// emits DAFNY_MULTISET_DECL(NAME, ELEM) into the header and
// DAFNY_MULTISET_DEFINE(NAME, ELEM) into the source. This produces:
//
//   typedef struct { ... } DafnyMultiset_NAME;
//   DafnyMultiset_NAME dafny_multiset_NAME_create(size_t n, const ELEM* items);
//   size_t             dafny_multiset_NAME_card(DafnyMultiset_NAME m); // |m|
//   size_t             dafny_multiset_NAME_count(DafnyMultiset_NAME m, ELEM x);
//   bool               dafny_multiset_NAME_contains(DafnyMultiset_NAME m, ELEM x);
//   DafnyMultiset_NAME dafny_multiset_NAME_union(a, b);        // counts add
//   DafnyMultiset_NAME dafny_multiset_NAME_intersection(a, b); // counts min
//   DafnyMultiset_NAME dafny_multiset_NAME_difference(a, b);   // sub, floor 0
//   bool               dafny_multiset_NAME_subset(a, b);       // a[x] <= b[x]
//   bool               dafny_multiset_NAME_equals(a, b);
// ---------------------------------------------------------------------------

#define DAFNY_MULTISET_DECL(NAME, ELEM) \
  typedef struct { ELEM* slots; uint64_t* counts; bool* used; size_t cap; size_t len; size_t total; } DafnyMultiset_##NAME; \
  DafnyMultiset_##NAME dafny_multiset_##NAME##_create(size_t n, const ELEM* items); \
  size_t dafny_multiset_##NAME##_card(DafnyMultiset_##NAME m); \
  size_t dafny_multiset_##NAME##_count(DafnyMultiset_##NAME m, ELEM x); \
  bool dafny_multiset_##NAME##_contains(DafnyMultiset_##NAME m, ELEM x); \
  DafnyMultiset_##NAME dafny_multiset_##NAME##_union(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b); \
  DafnyMultiset_##NAME dafny_multiset_##NAME##_intersection(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b); \
  DafnyMultiset_##NAME dafny_multiset_##NAME##_difference(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b); \
  bool dafny_multiset_##NAME##_subset(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b); \
  bool dafny_multiset_##NAME##_proper_subset(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b); \
  bool dafny_multiset_##NAME##_disjoint(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b); \
  bool dafny_multiset_##NAME##_equals(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b);

#define DAFNY_MULTISET_DEFINE(NAME, ELEM, HASHFN, EQFN) \
  /* Power-of-two capacity strictly greater than the distinct count (>= 8), so \
     linear probing always terminates on an empty slot. */ \
  static size_t dafny_multiset_##NAME##_cap_for(size_t n) { \
    size_t _c = 8; \
    while (_c <= n * 2) { _c <<= 1; } \
    return _c; \
  } \
  static DafnyMultiset_##NAME dafny_multiset_##NAME##_alloc(size_t cap) { \
    DafnyMultiset_##NAME m; m.cap = cap; m.len = 0; m.total = 0; \
    m.slots = (ELEM*)malloc(cap * sizeof(ELEM)); \
    m.counts = (uint64_t*)malloc(cap * sizeof(uint64_t)); \
    m.used = (bool*)calloc(cap, sizeof(bool)); \
    return m; \
  } \
  /* Add `c` copies of x. Assumes at least one free slot. No-op if c == 0. */ \
  static void dafny_multiset_##NAME##_add(DafnyMultiset_##NAME* m, ELEM x, uint64_t c) { \
    if (c == 0) { return; } \
    size_t _mask = m->cap - 1; \
    size_t _i = (size_t)(HASHFN(x)) & _mask; \
    while (m->used[_i]) { \
      if (EQFN(m->slots[_i], x)) { m->counts[_i] += c; m->total += c; return; } \
      _i = (_i + 1) & _mask; \
    } \
    m->used[_i] = true; m->slots[_i] = x; m->counts[_i] = c; m->len++; m->total += c; \
  } \
  size_t dafny_multiset_##NAME##_count(DafnyMultiset_##NAME m, ELEM x) { \
    if (m.cap == 0) { return 0; } \
    size_t _mask = m.cap - 1; \
    size_t _i = (size_t)(HASHFN(x)) & _mask; \
    while (m.used[_i]) { \
      if (EQFN(m.slots[_i], x)) { return (size_t)m.counts[_i]; } \
      _i = (_i + 1) & _mask; \
    } \
    return 0; \
  } \
  bool dafny_multiset_##NAME##_contains(DafnyMultiset_##NAME m, ELEM x) { \
    return dafny_multiset_##NAME##_count(m, x) > 0; \
  } \
  DafnyMultiset_##NAME dafny_multiset_##NAME##_create(size_t n, const ELEM* items) { \
    DafnyMultiset_##NAME m = dafny_multiset_##NAME##_alloc(dafny_multiset_##NAME##_cap_for(n)); \
    for (size_t _i = 0; _i < n; _i++) { dafny_multiset_##NAME##_add(&m, items[_i], 1); } \
    return m; \
  } \
  size_t dafny_multiset_##NAME##_card(DafnyMultiset_##NAME m) { return m.total; } \
  DafnyMultiset_##NAME dafny_multiset_##NAME##_union(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b) { \
    DafnyMultiset_##NAME r = dafny_multiset_##NAME##_alloc(dafny_multiset_##NAME##_cap_for(a.len + b.len)); \
    for (size_t _i = 0; _i < a.cap; _i++) { if (a.used[_i]) { dafny_multiset_##NAME##_add(&r, a.slots[_i], a.counts[_i]); } } \
    for (size_t _i = 0; _i < b.cap; _i++) { if (b.used[_i]) { dafny_multiset_##NAME##_add(&r, b.slots[_i], b.counts[_i]); } } \
    return r; \
  } \
  DafnyMultiset_##NAME dafny_multiset_##NAME##_intersection(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b) { \
    DafnyMultiset_##NAME r = dafny_multiset_##NAME##_alloc(dafny_multiset_##NAME##_cap_for(a.len)); \
    for (size_t _i = 0; _i < a.cap; _i++) { \
      if (a.used[_i]) { \
        uint64_t _bc = (uint64_t)dafny_multiset_##NAME##_count(b, a.slots[_i]); \
        uint64_t _ac = a.counts[_i]; \
        uint64_t _min = _ac < _bc ? _ac : _bc; \
        dafny_multiset_##NAME##_add(&r, a.slots[_i], _min); \
      } \
    } \
    return r; \
  } \
  DafnyMultiset_##NAME dafny_multiset_##NAME##_difference(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b) { \
    DafnyMultiset_##NAME r = dafny_multiset_##NAME##_alloc(dafny_multiset_##NAME##_cap_for(a.len)); \
    for (size_t _i = 0; _i < a.cap; _i++) { \
      if (a.used[_i]) { \
        uint64_t _bc = (uint64_t)dafny_multiset_##NAME##_count(b, a.slots[_i]); \
        uint64_t _ac = a.counts[_i]; \
        if (_ac > _bc) { dafny_multiset_##NAME##_add(&r, a.slots[_i], _ac - _bc); } \
      } \
    } \
    return r; \
  } \
  bool dafny_multiset_##NAME##_subset(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b) { \
    for (size_t _i = 0; _i < a.cap; _i++) { \
      if (a.used[_i] && a.counts[_i] > (uint64_t)dafny_multiset_##NAME##_count(b, a.slots[_i])) { return false; } \
    } \
    return true; \
  } \
  bool dafny_multiset_##NAME##_proper_subset(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b) { \
    /* a < b : a subset of b AND a != b (strictly smaller total multiplicity). */ \
    return a.total < b.total && dafny_multiset_##NAME##_subset(a, b); \
  } \
  bool dafny_multiset_##NAME##_disjoint(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b) { \
    /* a !! b : no element occurs in both. */ \
    for (size_t _i = 0; _i < a.cap; _i++) { \
      if (a.used[_i] && dafny_multiset_##NAME##_count(b, a.slots[_i]) > 0) { return false; } \
    } \
    return true; \
  } \
  bool dafny_multiset_##NAME##_equals(DafnyMultiset_##NAME a, DafnyMultiset_##NAME b) { \
    return a.total == b.total && a.len == b.len && dafny_multiset_##NAME##_subset(a, b); \
  }

// Primitive value hash/eq: hash the raw bytes, compare with C `==`. Correct for
// bool, native integers and char (equal values have identical bytes). The
// generator passes these macros directly as the HASH/EQ arguments when
// instantiating a set/map/multiset over a primitive element type.
#define DAFNY_PRIM_HASH(x) dafny_hash_bytes(&(x), sizeof(x))
#define DAFNY_PRIM_EQ(a, b) ((a) == (b))

// The char element type is fundamental (strings). Its value hash/eq are the
// primitive ones; they are named so the seq_char / set_char etc. instantiations
// can reference dafny_hash_char / dafny_eq_char like any other element type.
static inline uint64_t dafny_hash_char(dafny_char x) { return dafny_hash_bytes(&x, sizeof(x)); }
static inline bool dafny_eq_char(dafny_char a, dafny_char b) { return a == b; }

// Strings are seq<char>, i.e. DafnySequence_char. Because the char sequence type
// is fundamental (string literals, print, Main's args element type) and the
// dafny_print_seq_char helper below needs it, it is instantiated here in the
// runtime header rather than by the generator. The code generator therefore
// SKIPS emitting DAFNY_SEQ_DECL/DEFINE for the "char" element type. The element
// type is dafny_char (a 32-bit code point), so |s| counts characters and the
// stored value survives non-ASCII code points losslessly.
DAFNY_SEQ_DECL(char, dafny_char)
DAFNY_SEQ_DEFINE(char, dafny_char, dafny_hash_char, dafny_eq_char)

// UTF-8-encode a single Dafny char (code point) to stdout.
static inline void dafny_putchar_utf8(dafny_char c) {
  if (c < 0x80) {
    putchar((int)c);
  } else if (c < 0x800) {
    putchar((int)(0xC0 | (c >> 6)));
    putchar((int)(0x80 | (c & 0x3F)));
  } else if (c < 0x10000) {
    putchar((int)(0xE0 | (c >> 12)));
    putchar((int)(0x80 | ((c >> 6) & 0x3F)));
    putchar((int)(0x80 | (c & 0x3F)));
  } else {
    putchar((int)(0xF0 | (c >> 18)));
    putchar((int)(0x80 | ((c >> 12) & 0x3F)));
    putchar((int)(0x80 | ((c >> 6) & 0x3F)));
    putchar((int)(0x80 | (c & 0x3F)));
  }
}

// print a Dafny char. Matches the other backends: `print c` emits the character
// itself, WITHOUT surrounding quotes or escaping (e.g. 'A' -> A, newline char ->
// an actual newline). UTF-8-encoded. (A standalone char is a single BMP code
// unit here; surrogate pairing only matters for strings, see dafny_print_seq_char.)
static inline void dafny_print_char(dafny_char c) {
  dafny_putchar_utf8(c);
}

// Print a char sequence (a Dafny string): UTF-8-encode each code point. The
// sequence need not be null-terminated; the explicit length is used.
//
// With --unicode-char:false a Dafny char is a 16-bit UTF-16 code UNIT, so a code
// point > U+FFFF arrives as a surrogate PAIR (high 0xD800-0xDBFF, low
// 0xDC00-0xDFFF). We must combine the pair into the single code point before
// UTF-8-encoding, otherwise each surrogate is encoded separately as 3 bytes
// (CESU-8/WTF-8) — mangling e.g. emoji. A lone/unpaired surrogate prints U+FFFD.
static inline void dafny_print_seq_char(DafnySequence_char s) {
  for (size_t _i = 0; _i < s.len; _i++) {
    dafny_char c = s.data[_i];
    if (c >= 0xD800 && c <= 0xDBFF && _i + 1 < s.len) {
      dafny_char lo = s.data[_i + 1];
      if (lo >= 0xDC00 && lo <= 0xDFFF) {
        dafny_putchar_utf8(((c - 0xD800) << 10) + (lo - 0xDC00) + 0x10000);
        _i++;  // consume the low surrogate
        continue;
      }
      dafny_putchar_utf8(0xFFFD);  // unpaired high surrogate
      continue;
    }
    if (c >= 0xDC00 && c <= 0xDFFF) {
      dafny_putchar_utf8(0xFFFD);  // unpaired low surrogate
      continue;
    }
    dafny_putchar_utf8(c);
  }
}

// The command-line arguments passed to Main. Main's argument type is
// seq<seq<char>>, but Main ignores it, so an opaque placeholder struct with the
// expected spelling is enough. (Kept distinct from the monomorphised
// DafnySequence_* types, which are the ones used for real sequence values.)
typedef struct { int argc; char **argv; } DafnySequence;

static inline DafnySequence dafny_get_args(int argc, char **argv) {
  DafnySequence s;
  s.argc = argc;
  s.argv = argv;
  return s;
}

// print a scalar. C has no overloading, so dispatch on the static type with
// C11 _Generic. Covers the native integer and bool types the subset can print.
#define dafny_print(x) _Generic((x), \
    bool:     printf("%s", (x) ? "true" : "false"), \
    uint8:    printf("%" PRIu8,  (uint8)(x)), \
    uint16:   printf("%" PRIu16, (uint16)(x)), \
    uint32:   printf("%" PRIu32, (uint32)(x)), \
    uint64:   printf("%" PRIu64, (uint64)(x)), \
    int8:     printf("%" PRId8,  (int8)(x)), \
    int16:    printf("%" PRId16, (int16)(x)), \
    int32:    printf("%" PRId32, (int32)(x)), \
    int64:    printf("%" PRId64, (int64)(x)), \
    char:     printf("%c", (char)(x)), \
    default:  printf("%" PRId64, (int64)(x)))

#endif // DAFNY_RUNTIME_C_H
