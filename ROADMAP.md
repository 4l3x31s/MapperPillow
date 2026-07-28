# MapperPillow — Roadmap

Where the project stands and what is left. See [DESIGN.md](DESIGN.md) for the
architecture behind these items.

## Status

Feature-complete for compile-time object mapping, **except `ProjectTo`**. All mapping
runs through generated interceptors (no runtime reflection) for covered call sites.
Anything not covered is reported at build time as `MP0002` and falls back to a
reflection mapper that is **not** feature-equivalent (see "Known characteristics").
Trimming and Native AOT are supported: trimmed/AOT builds drop the fallback entirely.
The NuGet package is built and verified (it carries the generator as an analyzer), but
not yet published to nuget.org.

Tests: 31 behavioral + 13 generator, green. Build clean, 0 warnings, including under
the trim/AOT analyzers (`IsAotCompatible`).

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
- [x] `MP0002` reflection-fallback diagnostic — every call site the generator cannot
      handle is a build warning instead of a silent runtime degradation
- [x] Trimming / Native AOT readiness: `IsAotCompatible`, the
      `MapperPillow.EnableReflectionFallback` feature switch, and a `build/*.targets`
      that turns the fallback off by default for `PublishTrimmed`/`PublishAot`
- [x] Two-tier tests (behavioral + in-memory generator harness)
- [x] README (EN/ES) + usage guide

## Pending

### 1. `ProjectTo<T>()` for `IQueryable` (the big one)

EF Core translation. This is **not** an interceptor feature — it needs a separate
expression-tree pipeline that builds a provider-translatable `Expression` (like
AutoMapper's `ProjectTo` / the `QueryableExtensions` in the original). Largest
remaining piece; effectively its own milestone.

### 2. Packaging & release (infrastructure, not features)

- [x] `dotnet pack` for `MapperPillow`, bundling the generator as an analyzer in the
      same package (`analyzers/dotnet/cs`). Verified end-to-end: a clean project
      consuming the `.nupkg` from a local feed maps via the generated interceptor
      (flattening, enums, collections) and publishes both trimmed and Native AOT with
      zero IL warnings — a single 1.4 MB native binary that still reports
      `intercepted=1`. The pack target hard-fails if the generator cannot be resolved,
      so the package can never silently ship without it.
- [x] `build/MapperPillow.targets` auto-adds `MapperPillow.Generated` to
      `<InterceptorsNamespaces>`, so installing the package is the whole setup. Opting
      out with `<MapperPillowEnableInterceptors>false</MapperPillowEnableInterceptors>`
      also unloads the generator — dropping the namespace alone would leave the emitted
      interceptor file uncompilable (CS9137). The sample and the test suite now rely on
      these shipped targets rather than setting the property by hand, so a regression
      there fails the build.
- [x] Multi-target the runtime (`net8.0;net9.0;net10.0`). `net8.0` has no
      `FeatureSwitchDefinitionAttribute`, so it gets an internal polyfill; verified
      that the trimmer honours it and still removes the reflection branch (the trimmed
      `MapperPillow.dll` drops from 11.7 KB to 5.1 KB and no longer contains the
      fallback's strings). The behavioral suite runs on all three targets.
- [x] CI (`.github/workflows/ci.yml`): build and test on Linux and Windows, plus a
      package verification job. `eng/Verify-Package.ps1` packs, consumes the real
      `.nupkg` from a local feed with a bare `PackageReference`, and asserts the
      things `dotnet test` structurally cannot — that the generator shipped, that a
      call site is served by the interceptor rather than reflection, that a trimmed
      publish is IL-warning-free, that the trimmer really removed the reflection
      branch, and that a Native AOT binary still maps correctly. Runs locally too.
- [ ] Versioning (e.g. MinVer) and a git remote
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

- The **reflection fallback is not equivalent to the generated code**. It only copies
  public properties matching by name with an assignable type: no flattening, no
  `[MapFrom]`, no `[MapConvert]`, no enum/nullable conversions, and no
  constructor-based destinations (it needs a parameterless constructor, so positional
  records throw). It is a safety net, not a second implementation — which is why every
  call site that lands on it is reported as `MP0002`.
- `file`-local types are never intercepted (they can't be referenced from the
  generated file) — they use the fallback and report `MP0002`.
- Open generic call sites (`T To<T>(object s) => s.MapTo<T>()`) cannot be generated:
  there is no concrete type to generate for. They report `MP0002`.
- In trimmed / Native AOT builds the fallback is **removed**, so those call sites throw
  instead of returning a partially mapped object. Override with
  `<MapperPillowEnableReflectionFallback>true</MapperPillowEnableReflectionFallback>`
  if you would rather keep it and preserve the mapped types yourself.
