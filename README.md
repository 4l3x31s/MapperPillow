# MapperPillow

A comfortable, convention-first object mapper for .NET — compile-time and
zero-ceremony. A free (MIT) alternative with an easy migration path away from
commercially-licensed mappers.

```csharp
using MapperPillow;

var dto = user.MapTo<UserDto>();
```

No mapper instance. No DI. No `Profile`. No startup configuration. Properties that
match by name map on their own; you only configure the unusual cases.

## Status

Early days — **v0 walking skeleton**. Today mapping runs on a reflection baseline;
the source generator already discovers every `MapTo<T>()` call site, and the next
milestone turns those into compile-time interceptors (zero reflection, AOT-safe).

See [DESIGN.md](DESIGN.md) for the architecture and roadmap.

## Layout

```
src/MapperPillow            The runtime surface (MapTo / Map)   — net10.0
src/MapperPillow.Generator  Roslyn incremental source generator — netstandard2.0
tests/MapperPillow.Tests    xUnit tests                         — net10.0
samples/MapperPillow.Sample Runnable example                    — net10.0
```

## Build & test

```bash
dotnet build MapperPillow.slnx
dotnet test  MapperPillow.slnx
dotnet run   --project samples/MapperPillow.Sample
```

## License

MIT. MapperPillow is an independent, clean-room project and is not affiliated with
or derived from AutoMapper.
