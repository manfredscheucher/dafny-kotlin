---
title: Integrating Dafny and C++-extended code
---

## THIS FILE IS A WORK IN PROGRESS, IT CAN BE MODIFIED AT ANY TIME WITHOUT NOTICE

**The Dafny-to-C++-extended compiler is experimental and not an officially
supported backend yet.**

The `c++-extended` backend is the existing C++ backend extended with the features
the minimal `cpp` target rejects, so that programs using unbounded integers,
exact reals, multisets and function values can be
compiled to C++.

```
dafny translate c++-extended A.dfy --unicode-char:false
dafny build -t:c++-extended A.dfy --unicode-char:false
```

## Relation to the `cpp` backend

`CppExtendedBackend` subclasses `CppBackend` and swaps in
`CppExtendedCodeGenerator`, which overrides just the few type/literal sites
needed to enable the extended features. The plain `cpp` target is unchanged and
still rejects these features cleanly.

| feature | `cpp` | `c++-extended` |
|---------|:-----:|:--------------:|
| unbounded `int` | rejected | GMP `mpz_class` |
| exact `real` | rejected | `DafnyReal` (GMP `mpq`-style rational) |
| `multiset<T>` | rejected | `std::unordered_multiset` |
| function values / lambdas | rejected | `std::function` |

## Runtime

The extended features live in the shared C++ runtime header
`Source/DafnyRuntime/DafnyRuntimeCpp/DafnyRuntime.h`:

- `DafnyReal` — an exact rational mirroring Dafny `real` semantics;
- `DafnyMultiset<T>` — a multiset over `std::unordered_multiset`;
- the GMP include is guarded by `__has_include(<gmpxx.h>)`, so the plain `cpp`
  target stays GMP-free and only `c++-extended` links GMP.

Compiling extended output therefore requires a C++17 compiler with GMP
(`mpz_class` / `mpq_class`) available.

## Notes

- The backend is marked experimental (`IsStable = false`), so its
  `translate`/`build` subcommands are hidden from `--help` but fully usable.
- Exercised out-of-tree by the `dafny-examples` project, which also asserts that
  the plain `cpp` target still rejects the extended-only features.
