# MapperPillow

**A comfortable, convention-first object mapper for .NET — compile-time and zero-ceremony.**

📖 Languages: **English** · [Español](README.es.md) — Full guide: [docs/guia-de-uso.md](docs/guia-de-uso.md)

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

- A .NET SDK with **C# 12 or later** (C# interceptors). This early build targets
  `net10.0`.

---

## Getting started

### 1. Add the package

> **Status:** MapperPillow is early (v0) and not on NuGet yet. For now, reference the
> project directly (see [Building from source](#building-from-source)). Once
> published, installation will be:

```bash
dotnet add package MapperPillow
```

### 2. Enable interceptors (one-time, per project)

Add the MapperPillow generated namespace to your consuming project's `.csproj`:

```xml
<PropertyGroup>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);MapperPillow.Generated</InterceptorsNamespaces>
</PropertyGroup>
```

> If you skip this step, `MapTo` still works — it falls back to reflection — but you
> lose the compile-time generation and the `MP0001` diagnostic. Do this step to get
> the real benefits.

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

`MapTo` is the recommended form in new code.

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

---

## How it works

Every `source.MapTo<TDestination>()` call is discovered by a Roslyn incremental
generator, which plans the mapping (direct, nested, or flattened) and emits a
compile-time **interceptor** that replaces the call with typed assignment code. There
is no runtime reflection for covered call sites. See
[DESIGN.md](DESIGN.md) for the full architecture and roadmap.

## Current limitations

MapperPillow is young. The main gap is `ProjectTo` for `IQueryable` (EF Core
translation), which is a separate expression-tree pipeline. Anything the generator
can't handle falls back to a reflection-based mapper, so it still works — just not at
compile time.

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
src/MapperPillow             Runtime surface (MapTo / Map)       — net10.0
src/MapperPillow.Generator   Roslyn source generator             — netstandard2.0
tests/MapperPillow.Tests             Behavioral tests            — net10.0
tests/MapperPillow.Generator.Tests   In-memory generator tests   — net10.0
samples/MapperPillow.Sample  Runnable example                    — net10.0
```

## License

MIT — see [LICENSE](LICENSE). MapperPillow is an independent, clean-room project and
is not affiliated with or derived from AutoMapper.
