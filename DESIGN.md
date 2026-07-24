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
var q    = query.ProjectTo<UserDto>();     // IQueryable projection: later milestone
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
- **Milestone 3 — Richer mapping.** Collections/arrays, nested objects, flattening
  (`Order.Customer.Name` → `CustomerName`), nullable handling, enums.
- **Milestone 4 — Escape hatches.** Minimal, opt-in customization for the unusual
  cases (member remap, ignore, custom converter) — kept small on purpose.
- **Milestone 5 — `ProjectTo` for `IQueryable`** (EF Core translation).
- **Milestone 6 — Compile-time diagnostics.** Report unmapped destination members
  as build warnings/errors with the exact member name.

## 7. Current status

Direct `MapTo<TDestination>()` calls on concrete types are served by generated
compile-time interceptors (no reflection). `ReflectionMapper` now survives only as
a fallback for call sites the generator does not cover — notably the `Map<T>`
courtesy alias (open generic) and any dynamically-typed source. Retiring those
remaining reflection paths is future work (see milestones 3+).
