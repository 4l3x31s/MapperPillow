# MapperPillow — Roadmap

Where the project stands and what is left. See [DESIGN.md](DESIGN.md) for the
architecture behind these items.

## Status

Feature-complete for compile-time object mapping, **except `ProjectTo`**. All mapping
runs through generated interceptors (no runtime reflection) for covered call sites;
anything not covered falls back to a reflection-based mapper so it still works.
Not yet packaged for NuGet — consumed today via project references.

Tests: 27 behavioral + 8 generator, green. Build clean, 0 warnings.

## Done

- [x] Compile-time interceptors for `MapTo<T>()` (via `GetInterceptableLocation`)
- [x] `Map<T>()` courtesy alias (AutoMapper migration)
- [x] Same-name / implicitly-convertible properties
- [x] Collections & arrays (`List<T>`, arrays, `IEnumerable/IList/ICollection/IReadOnlyList/IReadOnlyCollection<T>`)
- [x] Nested objects (recursive, null-guarded, cycle-safe)
- [x] Collection-valued properties (`Order.Items`)
- [x] Flattening, multi-level (`Order.Customer.Address.City` → `CustomerAddressCity`)
- [x] Enums: enum↔enum, enum↔integral, enum→string, string→enum
- [x] Nullable value unwrap (`int?` → `int`)
- [x] Constructor-based destinations / positional records
- [x] Per-member attributes: `[MapIgnore]`, `[MapFrom("path")]`, `[MapConvert(typeof(C))]`
- [x] `MP0001` unmapped-member diagnostic (escalatable to error)
- [x] Two-tier tests (behavioral + in-memory generator harness)
- [x] README (EN/ES) + usage guide

## Pending

### 1. `ProjectTo<T>()` for `IQueryable` (the big one)

EF Core translation. This is **not** an interceptor feature — it needs a separate
expression-tree pipeline that builds a provider-translatable `Expression` (like
AutoMapper's `ProjectTo` / the `QueryableExtensions` in the original). Largest
remaining piece; effectively its own milestone.

### 2. Packaging & release (infrastructure, not features)

- [ ] `dotnet pack` for `MapperPillow`, bundling the generator as an analyzer in the
      same package (`analyzers/dotnet/cs`)
- [ ] Ship a `build/MapperPillow.props` that auto-adds
      `MapperPillow.Generated` to `<InterceptorsNamespaces>` — removes the manual
      setup step (guide §2)
- [ ] Multi-target the runtime (`net8.0;net9.0;net10.0`) instead of `net10.0` only
- [ ] Versioning (e.g. MinVer), a git remote, and CI (build + test)
- [ ] Publish to NuGet

### 3. Nice-to-have mapping features

- [ ] Map into an existing instance — `Map(source, existingDestination)`
- [ ] Dictionaries (`IDictionary<K,V>`) as source/destination
- [ ] Before/after map hooks
- [ ] Configurable naming conventions / opt-in case-insensitive matching
- [ ] Diagnostics for invalid `[MapFrom]` / `[MapConvert]` (today they silently fall
      back and surface as `MP0001`)
- [ ] Constructor tie-breaking when several constructors are equally satisfiable

## Known characteristics (by design, not bugs)

- Uncovered call sites use the **reflection fallback** — correct, just not
  compile-time. Enable interceptors (`<InterceptorsNamespaces>`) to avoid it.
- `file`-local types are never intercepted (they can't be referenced from the
  generated file) — they use the fallback.
- The `Map<T>()` alias is a generic method, so it is not intercepted; prefer
  `MapTo<T>()` for the compile-time path.
