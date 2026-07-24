# MapperPillow

**Un mapeador de objetos cómodo y basado en convenciones para .NET — en tiempo de compilación y sin ceremonia.**

📖 Idiomas: [English](README.md) · **Español** — Guía completa: [docs/guia-de-uso.md](docs/guia-de-uso.md)

MapperPillow copia un objeto a otro sin código repetitivo. Las propiedades que
coinciden por nombre se mapean solas; solo configuras los casos poco comunes. Por
dentro es un **generador de código** de Roslyn + **interceptores de C#**, así que el
código de mapeo se genera en tiempo de compilación: sin reflexión en tiempo de
ejecución, compatible con AOT, y puedes abrir y leer exactamente lo que se ejecuta.

```csharp
using MapperPillow;

var dto = user.MapTo<UserDto>();
```

Sin instancia de mapeador. Sin inyección de dependencias. Sin clases `Profile`. Sin
llamada de arranque.

---

## Por qué existe

AutoMapper pasó a licencia comercial en la versión 15. MapperPillow es una
alternativa libre (MIT) con una ruta de migración sencilla: familiar de usar, pero
más simple, y con algo que AutoMapper no puede hacer: **te avisa de los miembros sin
mapear en tiempo de compilación**, no en tiempo de ejecución.

## Características

- **Sin ceremonia** — una única extensión `MapTo<T>()`, descubierta automáticamente.
- **Tiempo de compilación** — el mapeo es código generado; sin reflexión en la ruta
  caliente.
- **Convenciones que funcionan** — propiedades con el mismo nombre, colecciones,
  arrays, objetos anidados, propiedades que son colecciones, enums, records
  posicionales y aplanamiento (`Order.Customer.Name` → `CustomerName`).
- **Seguridad en compilación** — el diagnóstico `MP0001` nombra cualquier miembro del
  destino que quede sin mapear. Puedes tratarlo como error en proyectos estrictos.
- **Auditable** — el mapeo generado es C# normal que puedes inspeccionar y depurar.

## Requisitos

- Un SDK de .NET con **C# 12 o superior** (interceptores de C#). Esta versión
  temprana apunta a `net10.0`.

---

## Primeros pasos

### 1. Referencia el proyecto

> **Estado:** MapperPillow es temprano (v0) y aún no está en NuGet. Por ahora se usa
> referenciando el proyecto directamente. Consulta la
> [guía de uso](docs/guia-de-uso.md#1-instalar-desde-el-proyecto) para el detalle
> exacto del `.csproj`.

### 2. Habilita los interceptores (una vez, por proyecto)

Agrega el espacio de nombres generado por MapperPillow al `.csproj` de tu proyecto:

```xml
<PropertyGroup>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);MapperPillow.Generated</InterceptorsNamespaces>
</PropertyGroup>
```

> Si omites este paso, `MapTo` sigue funcionando —recurre a reflexión— pero pierdes la
> generación en tiempo de compilación y el diagnóstico `MP0001`. Haz este paso para
> obtener los beneficios reales.

### 3. Mapea

```csharp
using MapperPillow;

var source = new User { Id = 7, Name = "Ada Lovelace", Email = "ada@calculus.dev" };

UserDto dto = source.MapTo<UserDto>();
```

Eso es todo. No hay nada que registrar.

---

## Uso

### Mapeo básico

Las propiedades se emparejan por nombre; los tipos asignables se copian.

```csharp
public sealed class User    { public int Id { get; set; } public string Name { get; set; } }
public sealed class UserDto { public int Id { get; set; } public string Name { get; set; } }

var dto = user.MapTo<UserDto>();
```

### Migrar desde AutoMapper: el alias `Map<T>`

Si tu código usa `mapper.Map<T>(x)`, MapperPillow ofrece un `Map<T>()` familiar para
que el cambio sea mínimo:

```csharp
var dto = user.Map<UserDto>();   // mismo resultado que MapTo<UserDto>()
```

`MapTo` es la forma recomendada en código nuevo.

### Colecciones y arrays

La misma llamada mapea secuencias — `List<T>`, arrays y destinos `IEnumerable`/
`IList`/`ICollection`/`IReadOnlyList`/`IReadOnlyCollection<T>`:

```csharp
List<UserDto> dtos  = users.MapTo<List<UserDto>>();
UserDto[]     array = users.MapTo<UserDto[]>();
```

### Objetos anidados

Una propiedad cuyo tipo es otro objeto mapeable se mapea de forma recursiva, con
protección contra nulos:

```csharp
public sealed class Customer    { public string Name { get; set; } public Address Address { get; set; } }
public sealed class CustomerDto { public string Name { get; set; } public AddressDto Address { get; set; } }

var dto = customer.MapTo<CustomerDto>();
// dto.Address es un AddressDto mapeado — o null si el Address de origen era null.
```

### Aplanamiento (flattening)

Un miembro del destino sin coincidencia directa se resuelve contra una ruta anidada
del origen dividiendo su nombre en PascalCase:

```csharp
public sealed class Order    { public int Id { get; set; } public Customer Customer { get; set; } }
public sealed class OrderDto { public int Id { get; set; } public string CustomerName { get; set; } } // ← Customer.Name

var dto = order.MapTo<OrderDto>();
// dto.CustomerName == order.Customer.Name   (los intermedios nulos dan default)
```

### Configuración por miembro

Las convenciones cubren los casos comunes; para el resto, anota el tipo de destino —
la llamada sigue limpia y todo se mantiene en tiempo de compilación.

```csharp
public sealed class OrderDto
{
    public int Id { get; set; }

    [MapFrom("Customer.Name")]        // ruta de origen explícita (puede ser anidada)
    public string Buyer { get; set; }

    [MapIgnore]                       // nunca se mapea; no lo reporta MP0001
    public string Notes { get; set; }

    [MapConvert(typeof(CentsToDollars))]   // IValueConverter<int, string> propio
    public string Price { get; set; }
}
```

### Seguridad en compilación: el diagnóstico `MP0001`

Si un miembro del destino no se puede mapear, obtienes una advertencia en la llamada a
`MapTo` —nombrando el miembro— en lugar de un hueco silencioso descubierto en
ejecución:

```
warning MP0001: MapTo<OrderDto> leaves destination member(s) unmapped: 'Notes'
```

Para convertir los miembros sin mapear en un error de compilación, agrega a tu
`.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.MP0001.severity = error
```

---

## Cómo funciona

Cada llamada `source.MapTo<TDestination>()` es descubierta por un generador
incremental de Roslyn, que planifica el mapeo (directo, anidado o aplanado) y emite un
**interceptor** en tiempo de compilación que reemplaza la llamada con código de
asignación tipado. No hay reflexión en tiempo de ejecución para las llamadas
cubiertas. Consulta [DESIGN.md](DESIGN.md) para la arquitectura completa.

## Limitaciones actuales

MapperPillow es joven. El hueco principal es `ProjectTo` para `IQueryable`
(traducción a EF Core), que es un pipeline de árboles de expresión aparte. Lo que el
generador no puede manejar recurre a un mapeador basado en reflexión, así que sigue
funcionando — solo que no en tiempo de compilación.

Para el panorama completo de lo hecho y lo pendiente (incluido el empaquetado
NuGet), consulta [ROADMAP.md](ROADMAP.md).

## Licencia

MIT — consulta [LICENSE](LICENSE). MapperPillow es un proyecto independiente,
desarrollado desde cero (clean-room), sin afiliación ni derivación de AutoMapper.
