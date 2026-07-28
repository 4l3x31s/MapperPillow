# MapperPillow — Design

> A comfortable, convention-first object mapper for .NET.
> Easy to migrate to from AutoMapper, but not a clone: simpler, intuitive, and
> without heavy configuration. Free and open source (MIT).

This document captures the decisions made while planning MapperPillow so the
intent survives across sessions. It is the source of truth for *why* the library
is shaped the way it is.

## 1. Why this exists

AutoMapper became commercially licensed at v15 (dual RPL-1.5 / paid). MapperPillow
is a free alternative for people who want to leave that licensing behind — with a
migration path that feels natural, not a forced rewrite.

## 2. Legal ground rules (non-negotiable)

MapperPillow is a **clean-room** implementation:

- **No AutoMapper code, comments, or docs are copied** — not from the RPL v15+
  sources, not translated, not paraphrased line-by-line.
- Reimplementing a *similar public API shape* is allowed (API reimplementation is
  lawful — cf. Google v. Oracle, 2021), but the implementation is our own.
- MapperPillow does **not** use the `AutoMapper` name, namespace, logo, or branding.
- This project lives in its own repository, physically separate from any
  AutoMapper checkout, so no RPL-licensed file can leak into it.

## 3. Product principles

The whole point is to fix what makes people *hate* a mapper:

1. **Compile-time, not runtime magic.** Mapping is resolved by a source generator.
   Mistakes fail the **build**, with the exact property named — not at runtime in
   production. (AutoMapper's `AssertConfigurationIsValid()` band-aid is designed
   away.)
2. **Convention by default, explicit by exception.** Same-name / assignable
   properties map with zero configuration. You only declare something for the
   genuinely unusual cases.
3. **No mandatory ceremony.** No required `Profile` classes, no forced DI
   registration to map two objects.
4. **Auditable.** The generated mapping is real, readable C# the user can open and
   step through — important for regulated contexts (e.g. banking).
5. **AOT-friendly.** No runtime reflection or expression compilation in the
   endgame; works under Native AOT and trimming.

## 4. Public API (chosen)

Primary surface is a **fluent extension** — the most discoverable, zero-ceremony
option:

```csharp
var dto  = user.MapTo<UserDto>();
var dtos = users.MapTo<List<UserDto>>();   // (collections: milestone 3)
var q    = query.ProjectTo<UserDto>();     // IQueryable projection (milestone 7)
```

A courtesy `Map<TDestination>()` alias is provided so code migrating from
AutoMapper keeps reading naturally. `MapTo` is the recommended form in new code.

## 5. Engine architecture

Chosen engine: **Roslyn incremental source generator + C# interceptors.**

- **Discovery.** Because the API is a generic extension call with no attribute,
  `ForAttributeWithMetadataName` does not apply. The generator uses
  `SyntaxProvider.CreateSyntaxProvider` with a cheap syntactic predicate
  (`<expr>.MapTo<...>(...)`) and resolves the `(source, destination)` type pair
  from the semantic model. This is the deliberate cost of an ergonomic,
  attribute-free API.
- **Emission.** Each discovered call site is redirected to generated code via a
  compile-time **interceptor**. Use the modern, refactor-safe API:
  `semanticModel.GetInterceptableLocation(invocation)` → emit
  `[InterceptsLocation(location.Version, location.Data)]` on the generated method.
  Do **not** use the deprecated (file, line, column) form.
- **Opt-in.** Consuming projects enable interceptors by adding the generated
  namespace to `<InterceptorsNamespaces>` in their csproj (the generator documents
  this).
- **Caching.** Pipeline models must have value equality (records / value tuples)
  so the IDE stays fast.

### Hard constraints

- The **generator project must target `netstandard2.0`** (Roslyn requirement).
- Runtime library, samples, and tests target `net10.0` (only the .NET 10 SDK is
  installed here; interceptors are stable on .NET 9+).

## 6. Roadmap

- **Milestone 0 — Walking skeleton (DONE).** Solution scaffold; fluent `MapTo` /
  `Map` surface; reflection baseline so it runs today; generator wired in,
  discovering real `MapTo<T>()` call sites; passing tests + runnable sample.
- **Milestone 1 — Discovery pipeline (DONE).** Generator finds every `MapTo<T>()`
  call and resolves its type pair. Currently emits a summary of discovered maps.
- **Milestone 2 — Interceptor emission (DONE).** Each discovered `MapTo<T>()` call
  site is replaced with a generated interceptor (`GetInterceptableLocation` →
  `[InterceptsLocation]`) that constructs the destination and assigns same-name,
  implicitly-convertible properties — zero runtime reflection. Open generics (the
  `MapTo<T>` inside the `Map<T>` alias) are skipped and left to the fallback. This
  is where "no runtime reflection" became real. Verified by a telemetry-based test
  that fails if a call is served by reflection instead of the interceptor.
- **Milestone 3 — Richer mapping (in progress).**
  - Collections/arrays (DONE): `List<T>`, arrays, and `IEnumerable/IList/ICollection/
    IReadOnlyList/IReadOnlyCollection<T>` destinations, mapping each element by the
    scalar rules. Source may be any `IEnumerable<T>` or array.
  - Nested complex objects (DONE): a destination property whose type is another
    mappable reference type is mapped recursively (`accessor is null ? null : new
    Dest { ... }`), with cycle protection (visited set) and a depth cap.
  - Flattening (DONE): a destination member with no direct match is resolved
    against a nested source path by splitting its PascalCase name (longest matching
    prefix wins at every level), now **multi-level** (`Customer.Address.City` →
    `CustomerAddressCity`), null-guarding each object hop with `default(T)`.
  - Collection-valued properties (DONE): a property whose type is a collection maps
    each element via `Enumerable.Select(...).ToList()/.ToArray()` (null -> null),
    reusing the element mapping rules; unique lambda variable per nesting level.
  - Enums (DONE): enum <-> enum by value, enum <-> integral (`(Dest)src`), enum ->
    string (`.ToString()`), string -> enum (`Enum.Parse`).
  - Nullables (DONE): a `Nullable<U>` source to a non-nullable value destination
    unwraps with `GetValueOrDefault()` (null -> default); wrapping is already an
    implicit conversion.
  - Constructor-based destinations (DONE): types without a parameterless constructor
    (e.g. positional records) map via the richest satisfiable public constructor
    (`new Dest(a, b) { ... }`), matching parameters to source members by name
    (case-insensitive) and filling remaining settable members via the initializer.
    Unified `BuildConstruction` is used for scalar, nested, and collection elements.
  - Custom value converters (DONE): `[MapConvert(typeof(C))]` where
    `C : IValueConverter<TSource, TDestination>` — emits `new C().Convert(source.Member)`
    after validating the interface, a parameterless ctor, and type compatibility.

- **Milestone 4 — Compile-time diagnostics (DONE).** `MP0001` warns at the call
  site when a destination member stays unmapped, naming it — the replacement for
  AutoMapper's runtime `AssertConfigurationIsValid()`. Warning by default; escalate
  to error per project via `dotnet_diagnostic.MP0001.severity = error`. File-local
  types are skipped to the runtime fallback (they can't be referenced from the
  generated file). Analyzer release tracking files ship the rule.
- **Milestone 5 — Escape hatches (DONE).** Opt-in per-member configuration via
  attributes on the destination type (chosen over runtime config to stay
  compile-time): `[MapIgnore]` (skip a member; also excluded from `MP0001`),
  `[MapFrom("path")]` (map from an explicit, possibly nested, source path), and
  `[MapConvert(typeof(C))]` with `C : IValueConverter<TSource, TDestination>`.
- **Milestone 6 — trimming / Native AOT readiness.** Closed the gap between "the
  generator emits no reflection" and "the *library* is AOT-safe". Three parts:
  (a) the `Map<T>` alias is now intercepted like `MapTo<T>`, so migrated AutoMapper
  code stops running entirely on reflection; (b) `MP0002` turns every call site the
  generator declines into a build warning, because the fallback is a safety net and
  not a feature-equivalent second implementation; (c) the fallback sits behind the
  `MapperPillow.EnableReflectionFallback` feature switch, which `PublishTrimmed` and
  `PublishAot` builds default to `false` so the trimmer removes the branch outright.
- **Milestone 7 — `ProjectTo` for `IQueryable`.** This milestone was planned as the
  largest one in the project, on the assumption that it needed a separate
  expression-tree pipeline like AutoMapper's `QueryableExtensions`. That assumption
  was wrong, and the reason is the whole thesis of this library.

  A runtime mapper *must* compose the projection as `Expression` objects, because its
  configuration only exists at runtime. MapperPillow already knows both types when it
  runs, so it emits the projection as ordinary C#:

  ```csharp
  var typed  = (IQueryable<Order>)source;
  var result = Queryable.Select(typed, src => new OrderDto { ... });
  ```

  `Queryable.Select` takes an `Expression<Func<,>>`, so the *compiler* builds the
  expression tree. No pipeline, no expression cache, no `MakeGenericMethod` — and
  nothing that needs dynamic code under Native AOT. The whole feature is one extra
  body shape (`BuildProjectionBody`) reusing the same `BuildConstruction` planner,
  plus an interceptor whose parameter is `IQueryable` instead of `object`.

  The genuinely hard part is not building the projection but **restricting** it: some
  expressions are valid trees that no provider can translate (`[MapConvert]`
  converters, `Enum.Parse`). Diagnosing those at the call site is the open work.

  `ProjectTo` deliberately has no reflection fallback: building the projection at
  runtime is exactly the cost this library exists to avoid.

## Testing strategy

Two tiers:

- **`MapperPillow.Tests`** — behavioral tests. Real `MapTo` calls run through the
  generated interceptors (verified via `MapperPillowTelemetry`, which only the
  generated code increments). Proves the mapping *result* and that reflection is
  bypassed.
- **`MapperPillow.EfCore.Tests`** — provider tests. `ProjectTo` against EF Core over
  in-memory SQLite, asserting on `ToQueryString()` that the projection reaches SQL
  instead of being evaluated on the client. Separate project on purpose: EF Core is
  not trim-safe, so enabling the AOT analyzers next to it would bury the main suite's
  zero-warning guarantee.
- **`MapperPillow.Generator.Tests`** — generator unit tests. `GeneratorHarness`
  runs the generator in-memory over a source string (`CSharpGeneratorDriver`) and
  asserts on the emitted code and diagnostics — no compilation or execution. Proves
  the generator's *decisions* (interceptor shape, `MP0001`, open-generic fallback).
  New generator behavior gets a red/green test here first.

## 7. Current status

`MapTo<TDestination>()` and its `Map<TDestination>()` alias are served by generated
compile-time interceptors (no reflection) for every concrete-typed call site.

`ReflectionMapper` survives only for what genuinely cannot be generated: open generic
call sites, `file`-local types, and dynamically-typed sources. Those are no longer
silent — the generator reports each one as `MP0002` — and they are no longer a hidden
AOT hazard: the reflection branch is gated on the
`MapperPillow.EnableReflectionFallback` feature switch and is removed from trimmed and
Native AOT builds.

Two consequences worth stating plainly, because they contradict how a "fallback" is
usually read:

1. **The fallback is not equivalent to the generated code.** It copies public
   properties by name with an assignable type and nothing else — no flattening, no
   `[MapFrom]`, no `[MapConvert]`, no conversions, no constructor destinations. A call
   site that degrades to it can produce a *different result*, not just a slower one.
   That is why `MP0002` exists.
2. **`[RequiresUnreferencedCode]` must not go on the public `MapTo`/`Map` methods.**
   Verified empirically: the trim analyzer reads the original call site, not the
   interceptor that replaces it, so annotating them raises IL2026 on every call site
   including fully generated ones. The feature switch is what makes the fallback
   trim-safe; the suppression lives on the internal `Fallback<T>` helper.
