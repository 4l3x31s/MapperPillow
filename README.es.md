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

- **`net8.0`, `net9.0` o `net10.0`.** Los tres están verificados de punta a punta,
  incluidas publicaciones recortadas y Native AOT.
- Un SDK de .NET con **C# 12 o superior** (interceptores de C#).

---

## Primeros pasos

### 1. Agrega el paquete

```bash
dotnet add package MapperPillow
```

El paquete incluye el generador (`analyzers/dotnet/cs`), así que con una sola
referencia alcanza: no hay que cablear el analizador aparte.

> **Estado:** MapperPillow es temprano (v0) y todavía no está en nuget.org. El paquete
> ya se construye (`dotnet pack src/MapperPillow -c Release`) y está verificado de
> punta a punta, incluidas publicaciones recortadas y Native AOT. Hasta que se
> publique, consume ese `.nupkg` desde un feed local o referencia los proyectos
> directamente. Consulta la
> [guía de uso](docs/guia-de-uso.md#1-instalar-desde-el-proyecto) para el detalle.

### 2. Habilitar los interceptores — nada que hacer

El paquete inscribe tu proyecto en el espacio de nombres de los interceptores por vos,
desde el `build/MapperPillow.targets` que incluye. No hay paso de configuración.

Si referencias los **proyectos** en lugar del paquete, agrégalo a mano:

```xml
<PropertyGroup>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);MapperPillow.Generated</InterceptorsNamespaces>
</PropertyGroup>
```

> Esto no es opcional. El generador siempre emite su archivo de interceptores, y sin el
> espacio de nombres el compilador lo rechaza con **CS9137**: la compilación falla. No
> recurre silenciosamente a reflexión.

Para desactivar del todo la generación en compilación (una vía de escape, no un modo
soportado — obtienes el fallback por reflexión, que no es equivalente, y las
publicaciones recortadas o AOT lanzarán excepción):

```xml
<MapperPillowEnableInterceptors>false</MapperPillowEnableInterceptors>
```

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

Ambos se interceptan igual en tiempo de compilación: el código migrado obtiene la ruta
generada, no reflexión. `MapTo` es la forma recomendada en código nuevo.

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

### Seguridad en compilación: el diagnóstico `MP0002`

Un mapeador de tiempo de compilación nunca debería convertirse en uno de tiempo de
ejecución en silencio. Cuando el generador no puede producir código para una llamada,
lo dice — y dice por qué:

```
warning MP0002: MapTo<Dst> cannot be generated at compile time (no compile-time
mapping could be built from 'Src' to 'Dst'); it falls back to runtime reflection,
which is not trimming/Native AOT safe and supports neither flattening, [MapFrom],
[MapConvert], nor constructor-based destinations
```

Ese fallback es una red de seguridad, **no** una segunda implementación: solo copia
propiedades públicas que coincidan por nombre con un tipo asignable. Trata `MP0002`
como un reporte de error sobre la llamada, y escálalo si quieres una garantía:

```ini
[*.cs]
dotnet_diagnostic.MP0002.severity = error
```

### `ProjectTo` para `IQueryable`

Mapear entidades ya cargadas es la forma cara de construir un DTO. `ProjectTo` empuja
el mapeo dentro de la consulta, así la base devuelve sólo las columnas que pediste:

```csharp
var dtos = db.Orders
    .Where(o => o.Total > 100)
    .ProjectTo<OrderDto>()
    .ToList();
```

El generador emite `Queryable.Select(q, src => new OrderDto { ... })`, que el
compilador de C# convierte en el árbol de expresión — así que no hay pipeline en
tiempo de ejecución construyendo expresiones, y el aplanamiento se resuelve con un
`JOIN` en lugar de un segundo viaje a la base. Los operadores que compongas después de
`ProjectTo` siguen dentro de la misma consulta.

Dos advertencias que conviene saber. `ProjectTo` **no tiene fallback por reflexión**:
necesita el generador, y lo avisa con `MP0002`. Y algunas cosas que `MapTo` mapea sin
problema no las puede traducir una base de datos: los conversores `[MapConvert]` y
`string` → `enum`, entre otras. Hoy aparecen como error del proveedor al ejecutar la
consulta, no como advertencia de compilación.

### Trimming y Native AOT

MapperPillow está construido para eso: los interceptores generados son asignaciones
tipadas comunes, así que no hay nada que un trimmer pueda arruinar.

El único riesgo es el fallback por reflexión: un trimmer no puede analizar reflexión
sobre un `object`, así que puede eliminar las propiedades que el fallback necesita y
dejarte un objeto mapeado a medias, sin error. Por eso las publicaciones con
`PublishTrimmed` o `PublishAot` **eliminan el fallback por completo**. Una llamada que
lo necesitaba lanza una excepción inmediata en su lugar — y `MP0002` ya te lo había
advertido en compilación.

Para conservar el fallback en una compilación recortada (asumiendo tú la preservación
de los tipos mapeados):

```xml
<MapperPillowEnableReflectionFallback>true</MapperPillowEnableReflectionFallback>
```

---

## Cómo funciona

Cada llamada `source.MapTo<TDestination>()` es descubierta por un generador
incremental de Roslyn, que planifica el mapeo (directo, anidado o aplanado) y emite un
**interceptor** en tiempo de compilación que reemplaza la llamada con código de
asignación tipado. No hay reflexión en tiempo de ejecución para las llamadas
cubiertas. Consulta [DESIGN.md](DESIGN.md) para la arquitectura completa.

## Limitaciones actuales

MapperPillow es joven. `ProjectTo` funciona y está verificado contra EF Core, pero su
subconjunto traducible todavía no se valida en compilación: los conversores
`[MapConvert]` y `string` → `enum` entran en la proyección y después fallan en el
proveedor.

Lo que el generador no puede manejar recurre a un mapeador basado en reflexión. Ese
fallback es intencionalmente mínimo —solo nombre y tipo asignable—, así que **no**
reproduce aplanamiento, `[MapFrom]`, `[MapConvert]`, conversiones ni destinos por
constructor. Cada llamada que cae ahí se reporta como `MP0002`; trata esas
advertencias como trabajo pendiente, no como un modo soportado.

Para el panorama completo de lo hecho y lo pendiente (incluido el empaquetado
NuGet), consulta [ROADMAP.md](ROADMAP.md).

## Licencia

MIT — consulta [LICENSE](LICENSE). MapperPillow es un proyecto independiente,
desarrollado desde cero (clean-room), sin afiliación ni derivación de AutoMapper.
