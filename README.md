# MapperPillow

**A comfortable, convention-first object mapper for .NET — compile-time and zero-ceremony.**

📖 Languages: **English** · [Español](https://github.com/4l3x31s/MapperPillow/blob/master/README.es.md) — Full guide: [docs/guia-de-uso.md](https://github.com/4l3x31s/MapperPillow/blob/master/docs/guia-de-uso.md)

MapperPillow maps one object to another without the boilerplate. Properties that
match by name map on their own; you only touch configuration for the unusual cases.
Under the hood it is a Roslyn **source generator** + **C# interceptors**, so mapping
code is generated at compile time — no runtime reflection, AOT-friendly, and you can
open and read exactly what runs.

```csharp
using MapperPillow;

var dto = user.MapTo<UserDto>();
```

No mapper instance. No dependency injection. No `Profile` classes. No startup call.

---

## Why

AutoMapper became commercially licensed at v15. MapperPillow is a free (MIT)
alternative with an easy migration path — familiar to use, but simpler, and with one
thing AutoMapper can't do: **it tells you about unmapped members at build time**, not
at runtime.

## Features

- **Zero ceremony** — a single `MapTo<T>()` extension, discovered automatically.
- **Compile-time** — mapping is generated code; no reflection on the hot path.
- **Conventions that just work** — same-name properties, collections, arrays,
  nested objects, collection-valued properties, enums, positional records, and
  flattening (`Order.Customer.Name` → `CustomerName`).
- **Build-time safety** — the `MP0001` diagnostic names any destination member left
  unmapped. Opt into treating it as an error for strict projects.
- **Auditable** — the generated mapping is plain C# you can inspect and step through.

## Requirements

- **`net8.0`, `net9.0` or `net10.0`.** All three are verified end-to-end, including
  trimmed and Native AOT publishes.
- A .NET SDK with **C# 12 or later** (C# interceptors).

---

## Getting started

### 1. Add the package

```bash
dotnet add package MapperPillow
```

The package carries the source generator with it (`analyzers/dotnet/cs`), so one
reference is all you need — no separate analyzer wiring.

> **Status:** 1.0.0 is the first stable release. It targets `net8.0`, `net9.0` and
> `net10.0`, and every release is verified against the real `.nupkg` end-to-end —
> including trimmed and Native AOT publishes. See
> [docs/RELEASING.md](https://github.com/4l3x31s/MapperPillow/blob/master/docs/RELEASING.md)
> for how a release is cut — every version is published from CI through NuGet
> Trusted Publishing, so no long-lived credential can ship a package under this name.

### 2. Enable interceptors — nothing to do

The package opts your project into the interceptors namespace for you, from the
`build/MapperPillow.targets` it ships. There is no setup step.

If you reference the **projects** instead of the package, add it by hand:

```xml
<PropertyGroup>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);MapperPillow.Generated</InterceptorsNamespaces>
</PropertyGroup>
```

> This is not optional. The generator always emits its interceptor file, and without
> the namespace the compiler rejects it with **CS9137** — the build fails. It does not
> quietly fall back to reflection.

To disable compile-time generation entirely (an escape hatch, not a supported mode —
you get the non-equivalent reflection fallback, and trimmed/AOT builds will throw):

```xml
<MapperPillowEnableInterceptors>false</MapperPillowEnableInterceptors>
```

### 3. Map

```csharp
using MapperPillow;

var source = new User { Id = 7, Name = "Ada Lovelace", Email = "ada@calculus.dev" };

UserDto dto = source.MapTo<UserDto>();
```

That's it. There is nothing to register.

---

## Usage

### Basic mapping

Properties are matched by name; assignable types are copied.

```csharp
public sealed class User    { public int Id { get; set; } public string Name { get; set; } }
public sealed class UserDto { public int Id { get; set; } public string Name { get; set; } }

var dto = user.MapTo<UserDto>();
```

### Migrating from AutoMapper: the `Map<T>` alias

If your code reads `mapper.Map<T>(x)`, MapperPillow offers a familiar `Map<T>()` so
the change is minimal:

```csharp
var dto = user.Map<UserDto>();   // same result as MapTo<UserDto>()
```

Both are intercepted identically at compile time — migrated code gets the generated
path, not reflection. `MapTo` is the recommended form in new code.

### Collections and arrays

The same call maps sequences — `List<T>`, arrays, and `IEnumerable`/`IList`/
`ICollection`/`IReadOnlyList`/`IReadOnlyCollection<T>` destinations:

```csharp
List<UserDto> dtos  = users.MapTo<List<UserDto>>();
UserDto[]     array = users.MapTo<UserDto[]>();
```

### Nested objects

A property whose type is another mappable object is mapped recursively, with a null
guard:

```csharp
public sealed class Customer    { public string Name { get; set; } public Address Address { get; set; } }
public sealed class CustomerDto { public string Name { get; set; } public AddressDto Address { get; set; } }

var dto = customer.MapTo<CustomerDto>();
// dto.Address is a mapped AddressDto — or null if the source Address was null.
```

### Flattening

A destination member with no direct match is resolved against a nested source path by
splitting its PascalCase name:

```csharp
public sealed class Order    { public int Id { get; set; } public Customer Customer { get; set; } }
public sealed class OrderDto { public int Id { get; set; } public string CustomerName { get; set; } } // ← Customer.Name

var dto = order.MapTo<OrderDto>();
// dto.CustomerName == order.Customer.Name   (null intermediates yield default)
```

### Per-member configuration

Conventions cover the common cases; for the rest, annotate the destination type —
the call stays clean and everything stays compile-time.

```csharp
public sealed class OrderDto
{
    public int Id { get; set; }

    [MapFrom("Customer.Name")]        // explicit source path (may be nested)
    public string Buyer { get; set; }

    [MapIgnore]                       // never mapped; not reported by MP0001
    public string Notes { get; set; }

    [MapConvert(typeof(CentsToDollars))]   // custom IValueConverter<int, string>
    public string Price { get; set; }
}
```

### Build-time safety: the `MP0001` diagnostic

If a destination member can't be mapped, you get a warning at the `MapTo` call —
naming the member — instead of a silent gap discovered at runtime:

```
warning MP0001: MapTo<OrderDto> leaves destination member(s) unmapped: 'Notes'
```

To make unmapped members a hard build error, add to your `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.MP0001.severity = error
```

### Build-time safety: the `MP0002` diagnostic

A compile-time mapper should never quietly become a runtime one. When the generator
cannot produce code for a call site, it says so — and says why:

```
warning MP0002: MapTo<Dst> cannot be generated at compile time (no compile-time
mapping could be built from 'Src' to 'Dst'); it falls back to runtime reflection,
which is not trimming/Native AOT safe and supports neither flattening, [MapFrom],
[MapConvert], nor constructor-based destinations
```

That fallback is a safety net, **not** a second implementation: it only copies public
properties matching by name with an assignable type. Treat `MP0002` as a bug report
about the call site, and escalate it if you want a guarantee:

```ini
[*.cs]
dotnet_diagnostic.MP0002.severity = error
```

### `ProjectTo` for `IQueryable`

Mapping entities you already loaded is the expensive way to build a DTO. `ProjectTo`
pushes the mapping into the query, so the database returns only the columns you asked
for:

```csharp
var dtos = db.Orders
    .Where(o => o.Total > 100)
    .ProjectTo<OrderDto>()
    .ToList();
```

The generator emits `Queryable.Select(q, src => new OrderDto { ... })`, which the C#
compiler turns into the expression tree — so there is no runtime pipeline building
expressions, and flattening becomes a `JOIN` rather than a second round trip.
Operators composed after `ProjectTo` stay in the same query.

`ProjectTo` has **no reflection fallback** — it needs the generator, and says so with
`MP0002`.

### Build-time safety: the `MP0003` diagnostic

A few things that map fine with `MapTo` are not translatable by a database, and the
failure is not always loud. Measured against EF Core:

| Construct | What the provider actually does |
|---|---|
| `[MapConvert]` converter | **Silently evaluates it on the client.** The query works, but the database never computes the member — so nothing composed after the `ProjectTo` can filter or order by it. |
| `string` → `enum` | **Throws.** `Enum.Parse` is not client-evaluated at all. |
| `enum` → `string` | Translated fine. Not flagged. |

`MP0003` warns at the call site, naming the member and which of the two it is:

```
warning MP0003: ProjectTo<OrderDto> projects member(s) the query provider cannot
translate: 'Total' (a [MapConvert] converter is evaluated on the client, so the
database never computes the member)
```

The member is still emitted — dropping it would hand you a DTO with a silently
missing value, which is worse. Map it after materialising with `MapTo`, or exclude it
with `[MapIgnore]`.

### Trimming and Native AOT

MapperPillow is built for it — the generated interceptors are plain typed assignments,
so there is nothing for a trimmer to get wrong.

The one hazard is the reflection fallback: a trimmer cannot see through reflection over
an `object`, so it may remove the properties the fallback needs and leave you with a
silently half-mapped object. So `PublishTrimmed` and `PublishAot` builds **remove the
fallback entirely**. A call site that needed it throws immediately instead — and
`MP0002` already told you about it at build time.

To keep the fallback in a trimmed build (you then own preserving the mapped types):

```xml
<MapperPillowEnableReflectionFallback>true</MapperPillowEnableReflectionFallback>
```

---

## How it works

Every `source.MapTo<TDestination>()` call is discovered by a Roslyn incremental
generator, which plans the mapping (direct, nested, or flattened) and emits a
compile-time **interceptor** that replaces the call with typed assignment code. There
is no runtime reflection for covered call sites. See
[DESIGN.md](https://github.com/4l3x31s/MapperPillow/blob/master/DESIGN.md) for the full architecture and roadmap.

## Current limitations

MapperPillow is young. `ProjectTo` works and is verified against EF Core, but its
translatable subset is not yet enforced at build time: `[MapConvert]` converters and
`string` → `enum` compile into the projection and then fail in the provider.

Anything the generator can't handle falls back to a reflection-based mapper. That
fallback is intentionally minimal — name + assignable type only — so it does **not**
reproduce flattening, `[MapFrom]`, `[MapConvert]`, conversions, or constructor-based
destinations. Every call site that lands on it is reported as `MP0002`; treat those
warnings as work to do, not as a supported mode.

For the full picture of what's done and what's left (including NuGet packaging), see
[ROADMAP.md](https://github.com/4l3x31s/MapperPillow/blob/master/ROADMAP.md).

---

## Building from source

```bash
git clone <your-fork-url> MapperPillow
cd MapperPillow

dotnet build MapperPillow.slnx
dotnet test  MapperPillow.slnx
dotnet run   --project samples/MapperPillow.Sample
```

Project layout:

```
src/MapperPillow             Runtime surface (MapTo / Map)       — net8.0/9.0/10.0
src/MapperPillow.Generator   Roslyn source generator             — netstandard2.0
tests/MapperPillow.Tests             Behavioral tests            — net8.0/9.0/10.0
tests/MapperPillow.Generator.Tests   In-memory generator tests   — net10.0
samples/MapperPillow.Sample  Runnable example                    — net10.0
eng/Verify-Package.ps1       End-to-end package verification
```

### Verifying the package

`dotnet test` cannot tell you that the package shipped without its generator, or
that every call site quietly fell back to reflection — both stay green until a
consumer publishes trimmed. This does check that, by consuming the real `.nupkg`
the way a user would:

```bash
pwsh ./eng/Verify-Package.ps1              # every target framework, including Native AOT
pwsh ./eng/Verify-Package.ps1 -SkipAot     # skip the slow native link step
```

CI runs it on every push (`.github/workflows/ci.yml`).

## License

MIT — see [LICENSE](https://github.com/4l3x31s/MapperPillow/blob/master/LICENSE). MapperPillow is an independent, clean-room project and
is not affiliated with or derived from AutoMapper.
