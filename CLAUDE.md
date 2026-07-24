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

Requires the .NET 10 SDK (only `net10.0` is targeted so far). Run a single test with
`dotnet test tests/MapperPillow.Tests --filter "FullyQualifiedName~<name>"`.

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
- **Consumers must enable interceptors**: add
  `<InterceptorsNamespaces>$(InterceptorsNamespaces);MapperPillow.Generated</InterceptorsNamespaces>`
  and reference the generator as an `Analyzer`. Without it, `MapTo` still works via
  reflection but nothing is generated.
- The generator skips **open generics** (`ITypeParameterSymbol`, e.g. the `MapTo<T>`
  inside the `Map<T>` alias) and **`file`-local types** (can't be referenced from the
  generated file) — both fall back to reflection. Do not remove those guards.
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
