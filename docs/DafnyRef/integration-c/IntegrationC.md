---
title: Integrating Dafny and C code
---

## THIS FILE IS A WORK IN PROGRESS, IT CAN BE MODIFIED AT ANY TIME WITHOUT NOTICE

**The Dafny-to-C compiler is experimental and not an officially supported
backend yet.** It is derived from the C++ backend and, like it, has a minimal
target (`c`) and an extended one (`c-extended`); see the Status section below for
the exact split. Notably, function values / lambdas are unsupported by *both* C
targets (C has no expression-level lambdas), and codatatypes and traits are not
supported.

`dafny translate c <program>.dfy` emits `<program>.c` and `<program>.h`.
`dafny build -t:c <program>.dfy` additionally compiles them with `gcc -std=c11`.
Pass `--unicode-char:false`, as the backend does not support Unicode chars.

```bash
dafny translate c Program.dfy --unicode-char:false
dafny build     -t:c Program.dfy --unicode-char:false
```

## Relationship to the C++ backend

The C backend was derived from the C++ code generator and shares its statement-
and expression-translation core, but it has its own model for the constructs C
does not have:

- **Namespaces** — C++ wraps each module in `namespace M { … }` and qualifies
  names with `M::N`. C has neither, so modules are emitted flat and names are
  flattened (`M::N` → `M_N`).
- **Classes** — instead of `class X { … };`, members are emitted as flat
  free functions.
- **Templates / generics** — C has no generics, so generic methods and functions
  are *monomorphised*: one concrete copy is emitted per instantiation actually
  used, with the concrete type arguments mangled into the name
  (e.g. `Id<bool>` → `Id_bool`).
- **Entry point / runtime** — a plain C `main` (no exceptions) and a dedicated
  `DafnyRuntimeC` header instead of the C++ runtime.

## Matching Dafny and C types

| Dafny | C |
|-------|---|
| `bool` | `bool` (`<stdbool.h>`) |
| bounded ints via `newtype` with `{:nativeType}` | `int8_t` … `uint64_t` |
| unbounded `int` | GMP `mpz_t` (arbitrary precision) |
| `real` (exact rational p/q) | GMP `mpq_t` |
| type parameters `T` | monomorphised — one concrete copy per instantiation |
| `seq<T>`, string (`seq<char>`) | `struct { T* data; size_t len; }`, per element type |
| `set<T>`, `map<K,V>`, `multiset<T>` | open-addressing hash tables (like C++ `std::unordered_*`), per type, value hash/eq |
| `datatype` (incl. recursive) | tag `enum` + `struct { tag; union{…} }`; recursive fields boxed as `NAME*` |
| reference `class` (incl. generic) | heap `struct` + `NAME*`; methods take an explicit `this` |

Because `real` is an exact rational (not a float), the **extended** C target
(`c-extended`) links **GMP** (`gcc … -lgmp`) for unbounded `int` / exact `real`.
The minimal `c` target does not use GMP and is not linked against it. When GMP is
not on the default search path, set `DAFNY_C_GMP_PREFIX` to its install prefix
(the build falls back to `/opt/homebrew` if present, else the default paths).

## Status

Experimental. There are two targets:

- **`c`** (minimal) — compiles **GMP-free** (`gcc -std=c11`, no `-lgmp`). Like the
  C++ backend it does *not* support unbounded `int`, exact `real`, `multiset`, or
  `array.Length` (all of which need unbounded integers); each is rejected cleanly.
- **`c-extended`** — adds unbounded `int` and exact `real` (via GMP `mpz`/rational),
  `multiset`, and `array.Length`. Its output is compiled with `-lgmp`.

**Both targets** compile & run: strings, sequences, sets, maps (with
`int`/`real`/`seq`/string keys handled by value); datatypes with `match` (incl.
generic `Option<T>` and recursive `List`/`Tree`); reference classes with
fields/methods/constructors/`new` (incl. generic `Box<T>`).

**Not supported by either target:** function values / lambdas (C has no
expression-level lambdas), traits, iterators, codatatypes, `set`/`map` with datatype/class
keys, mutual recursion between distinct datatypes, and multi-dimensional arrays.
Memory is arena/leak (never freed).
