# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Engram memory bootstrap (read first)

This repo is its own git project (`mapperpillow`), physically separate from the
AutoMapper clone it was inspired by. **The project history from the initial build
sessions was saved to the Engram project `automapper`** (those sessions ran with the
AutoMapper repo as the working directory), not `mapperpillow`.

At session start, to recover that history:

1. Call `mem_search` for MapperPillow topics; if this project (`mapperpillow`) returns
   nothing, the memories are under the `automapper` project — recall from there
   (topic key `mapperpillow/architecture`, plus the session summaries).
2. From now on, save MapperPillow memories to **this** project. This CLAUDE.md plus
   `DESIGN.md` and `ROADMAP.md` are the authoritative in-repo context and do not
   depend on Engram.

## What this is

MapperPillow — a free (MIT) **compile-time** object mapper for .NET, built as an
easy-migration alternative to the now-commercial AutoMapper. A clean-room project:
no AutoMapper code, its own name/namespace.

- **Architecture & rationale:** [DESIGN.md](DESIGN.md)
- **What's done and what's left:** [ROADMAP.md](ROADMAP.md)
- **User-facing docs:** [README.md](README.md) / [README.es.md](README.es.md) /
  [docs/guia-de-uso.md](docs/guia-de-uso.md)

## Build & test

```bash
dotnet build MapperPillow.slnx
dotnet test  MapperPillow.slnx
dotnet run   --project samples/MapperPillow.Sample
```

Requires the .NET 10 SDK. The library and the behavioral tests multi-target
`net8.0;net9.0;net10.0`, so `dotnet test` runs the suite three times — a failure
report names the TFM. Run a single test with
`dotnet test tests/MapperPillow.Tests --filter "FullyQualifiedName~<name>"`.

Changes to packaging, the shipped `build/*.targets`, the feature switch, or the
generator's output shape are **not** covered by `dotnet test` — a package can ship
without its generator, or degrade every call site to reflection, and stay green.
Run `pwsh ./eng/Verify-Package.ps1` (add `-SkipAot` to skip the slow native link),
which consumes the real `.nupkg` and asserts on behaviour. CI runs it per push.

`net8.0` has no `FeatureSwitchDefinitionAttribute`; `src/MapperPillow/Polyfills.cs`
declares an internal one under `#if !NET9_0_OR_GREATER`. The trimmer matches it by
full type name, so do not rename or move it out of
`System.Diagnostics.CodeAnalysis` — the reflection branch would stop being trimmed
away on `net8.0`, silently.

## The core

Everything of substance is the source generator:
`src/MapperPillow.Generator/MapToInterceptorGenerator.cs`. It works in three stages —
**discover** (`GetCallSite`: find `MapTo<T>()` calls, resolve types, get the
interceptable location), **plan** (`BuildBody`/`BuildConstruction`/`BuildValue`/
`BuildFlattenedValue`: decide each member's value expression), **emit** (`Emit`:
write the `[InterceptsLocation]` interceptor). `MapTo`/`Map` live in
`src/MapperPillow/MapperPillowExtensions.cs`; the attributes and `IValueConverter` in
`src/MapperPillow/Attributes.cs`; a reflection fallback in `ReflectionMapper.cs`.

## Non-obvious constraints

- **The generator project MUST target `netstandard2.0`** (Roslyn requirement). Needs
  an `IsExternalInit` polyfill for `record`; declared diagnostics need
  `AnalyzerReleases.*.md` (RS2008).
- **Interceptors are mandatory, not opt-in.** The generator always emits its
  interceptor file; without `MapperPillow.Generated` in `<InterceptorsNamespaces>` the
  compiler rejects it with **CS9137** and the build fails — it does *not* degrade to
  reflection. `src/MapperPillow/build/MapperPillow.targets` sets the namespace
  automatically (NuGet consumers, plus the sample and tests, which import it), so do
  not re-add the property by hand. `<MapperPillowEnableInterceptors>false</>` opts out
  and must also unload the generator, or the emitted file breaks the build.
- The generator skips **open generics** (`ITypeParameterSymbol`) and **`file`-local
  types** (can't be referenced from the generated file) — both fall back to reflection
  and report **MP0002**. Do not remove those guards.
- **Never put `[RequiresUnreferencedCode]` on the public `MapTo`/`Map`.** Verified: the
  trim analyzer reads the original call site, not the interceptor, so it fires IL2026
  on fully generated call sites too. Trim-safety comes from the
  `MapperPillow.EnableReflectionFallback` feature switch instead.
- Tests: `MapperPillow.Generator.Tests` references the generator as a **normal**
  assembly (not analyzer) so it can be instantiated in-memory.

## Conventions

- **Strict TDD:** new generator behavior gets a failing test first (behavioral in
  `MapperPillow.Tests` and/or in-memory in `MapperPillow.Generator.Tests`), then the
  implementation.
- **Conventional commits, no AI attribution** (no `Co-Authored-By`). Commit per
  feature: code + tests + docs together.
- Docs are bilingual; Spanish docs use neutral/professional Spanish. Code,
  identifiers, and commit messages are English.
- Two API decisions are settled (one-way doors): the fluent `MapTo<T>()` surface, and
  **attributes** (not runtime config) for per-member configuration.
