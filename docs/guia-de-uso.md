# Guía de uso de MapperPillow

Esta guía explica, paso a paso, cómo instalar MapperPillow desde el proyecto, qué
métodos ofrece y cómo usar cada uno. Está pensada para alguien que recién empieza.

> Idiomas del README: [English](../README.md) · [Español](../README.es.md)

---

## Índice

1. [Instalar desde el proyecto](#1-instalar-desde-el-proyecto)
2. [Habilitar los interceptores](#2-habilitar-los-interceptores)
3. [Tu primer mapeo](#3-tu-primer-mapeo)
4. [Los métodos](#4-los-métodos)
5. [Cómo decide MapperPillow qué mapear](#5-cómo-decide-mapperpillow-qué-mapear)
6. [El diagnóstico MP0001](#6-el-diagnóstico-mp0001)
7. [Qué pasa cuando algo no se puede mapear](#7-qué-pasa-cuando-algo-no-se-puede-mapear)
8. [Errores comunes (y cómo evitarlos)](#8-errores-comunes-y-cómo-evitarlos)
9. [Limitaciones actuales](#9-limitaciones-actuales)

---

## 1. Instalar desde el proyecto

MapperPillow todavía no está publicado en NuGet. Mientras tanto, se consume
**referenciando los proyectos** desde tu solución. Necesitas **dos** referencias:

1. La librería en tiempo de ejecución (`MapperPillow`).
2. El generador de código, referenciado **como analizador** (`MapperPillow.Generator`).

En el `.csproj` de tu proyecto agrega:

```xml
<ItemGroup>
  <!-- Ajusta la ruta según dónde tengas clonado MapperPillow. -->
  <ProjectReference Include="..\MapperPillow\src\MapperPillow\MapperPillow.csproj" />

  <ProjectReference Include="..\MapperPillow\src\MapperPillow.Generator\MapperPillow.Generator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

> **Importante:** la segunda referencia (el generador) es obligatoria. Sin ella,
> `MapTo` funciona por reflexión, pero no se genera código en tiempo de compilación
> ni aparece el diagnóstico `MP0001`. La referencia al generador **no** se hereda de
> forma automática desde la librería, por eso hay que agregarla explícitamente.

---

## 2. Habilitar los interceptores

Este es el paso que más se olvida. Agrega el espacio de nombres generado al
`PropertyGroup` de tu `.csproj`:

```xml
<PropertyGroup>
  <InterceptorsNamespaces>$(InterceptorsNamespaces);MapperPillow.Generated</InterceptorsNamespaces>
</PropertyGroup>
```

- Si lo agregas: los `MapTo` se reemplazan por código generado (sin reflexión).
- Si lo olvidas: `MapTo` sigue funcionando por reflexión (más lento, sin `MP0001`).

Requisito: un SDK de .NET con **C# 12 o superior**. Esta versión apunta a `net10.0`.

---

## 3. Tu primer mapeo

```csharp
using MapperPillow;

public sealed class User
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class UserDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

var user = new User { Id = 1, Name = "Ada" };
UserDto dto = user.MapTo<UserDto>();   // dto.Id == 1, dto.Name == "Ada"
```

No hay que registrar nada ni crear un mapeador. La llamada `MapTo` es suficiente.

---

## 4. Los métodos

MapperPillow expone dos métodos de extensión, ambos en el espacio de nombres
`MapperPillow`.

### `MapTo<TDestination>()`

```csharp
public static TDestination MapTo<TDestination>(this object source);
```

Crea una instancia nueva de `TDestination` y copia los miembros que coinciden.
Es la forma **recomendada** para código nuevo.

```csharp
var dto = user.MapTo<UserDto>();
var lista = usuarios.MapTo<List<UserDto>>();
var arreglo = usuarios.MapTo<UserDto[]>();
```

- Lanza `ArgumentNullException` si `source` es `null`.
- El destino necesita un constructor sin parámetros (salvo arrays y colecciones, que
  se construyen internamente).

### `Map<TDestination>()`

```csharp
public static TDestination Map<TDestination>(this object source);
```

Alias de cortesía con la misma semántica que `MapTo`. Existe para facilitar la
migración desde AutoMapper, donde el método se llama `Map`. Internamente llama a
`MapTo`.

```csharp
var dto = user.Map<UserDto>();   // idéntico a user.MapTo<UserDto>()
```

> Nota: en esta versión, `Map<T>` (por ser genérico) se resuelve por reflexión; la
> generación en tiempo de compilación se aplica a las llamadas directas a `MapTo<T>`
> con tipos concretos. Para el camino óptimo, usa `MapTo`.

---

## 5. Cómo decide MapperPillow qué mapear

La regla general: **por cada propiedad del destino que tenga `set` público**, se busca
un valor en el origen. El orden de búsqueda es:

### a) Coincidencia directa por nombre

Misma propiedad, tipo igual o convertible de forma implícita.

```csharp
class Src { public int Id { get; set; } public string Name { get; set; } = ""; }
class Dst { public int Id { get; set; } public string Name { get; set; } = ""; }

src.MapTo<Dst>();   // Id y Name se copian
```

### b) Colecciones y arrays

Si el destino es una colección, se mapea elemento por elemento.

```csharp
List<Dst> lista   = origenes.MapTo<List<Dst>>();
Dst[]     arreglo = origenes.MapTo<Dst[]>();
```

Destinos soportados: `List<T>`, arrays, `IEnumerable<T>`, `IList<T>`, `ICollection<T>`,
`IReadOnlyList<T>`, `IReadOnlyCollection<T>`. El origen puede ser cualquier
`IEnumerable<T>` o array.

### c) Objetos anidados

Si una propiedad es otro objeto mapeable, se mapea de forma recursiva, protegiendo los
nulos.

```csharp
class Address    { public string City { get; set; } = ""; }
class AddressDto { public string City { get; set; } = ""; }
class Customer    { public string Name { get; set; } = ""; public Address Address { get; set; } = new(); }
class CustomerDto { public string Name { get; set; } = ""; public AddressDto Address { get; set; } = new(); }

var dto = customer.MapTo<CustomerDto>();
// dto.Address.City se mapea; si customer.Address era null, dto.Address es null.
```

### d) Aplanamiento (flattening)

Si un miembro del destino no tiene coincidencia directa, se intenta resolver contra una
ruta anidada del origen dividiendo su nombre en PascalCase.

```csharp
class Customer { public string Name { get; set; } = ""; public int Age { get; set; } }
class Order    { public int Id { get; set; } public Customer Customer { get; set; } = new(); }
class OrderDto
{
    public int Id { get; set; }
    public string CustomerName { get; set; } = "";   // ← Customer.Name
    public int CustomerAge { get; set; }              // ← Customer.Age
}

var dto = order.MapTo<OrderDto>();
// CustomerName = order.Customer.Name, CustomerAge = order.Customer.Age
// Si order.Customer fuera null, se asigna el valor por defecto (null o 0).
```

Detalles: gana el prefijo **más largo** que coincida (para desambiguar `Customer` de
`CustomerAccount`, por ejemplo), y por ahora resuelve **un nivel** de anidamiento.

### e) Propiedades que son colecciones

Si una propiedad del destino es una colección, se mapea elemento por elemento
(protegiendo los nulos).

```csharp
class Item    { public int Id { get; set; } public string Name { get; set; } = ""; }
class ItemDto { public int Id { get; set; } public string Name { get; set; } = ""; }
class Order    { public int Id { get; set; } public List<Item> Items { get; set; } = new(); }
class OrderDto { public int Id { get; set; } public List<ItemDto> Items { get; set; } = new(); }

var dto = order.MapTo<OrderDto>();
// dto.Items tiene un ItemDto por cada Item; si Items era null, dto.Items es null.
```

### f) Enums

Se soportan tres conversiones habituales:

```csharp
// enum -> otro enum (por valor)
class Src { public SourceStatus Status { get; set; } }
class Dst { public TargetStatus Status { get; set; } }   // Status = (TargetStatus)src.Status

// enum -> string
class DstStr { public string Status { get; set; } = ""; } // Status = src.Status.ToString()

// string -> enum
class SrcStr { public string Status { get; set; } = ""; } // Status = Enum.Parse(...)
```

---

## 6. El diagnóstico MP0001

Cuando una propiedad del destino no se puede mapear por ninguna de las reglas
anteriores, MapperPillow emite una **advertencia en tiempo de compilación**, en la
línea de tu `MapTo`, nombrando el miembro:

```
warning MP0001: MapTo<OrderDto> leaves destination member(s) unmapped: 'Notes'
```

Esto reemplaza al `AssertConfigurationIsValid()` de AutoMapper, que solo falla en
ejecución. Aquí te enteras antes de compilar.

Para que sea un **error** de compilación (recomendado en proyectos estrictos), agrega
a tu `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.MP0001.severity = error
```

---

## 7. Qué pasa cuando algo no se puede mapear

MapperPillow nunca deja tu programa sin funcionar. Si el generador no puede manejar una
llamada (por ejemplo, un tipo `file`-local, o un caso todavía no soportado), esa
llamada **recurre a un mapeador por reflexión** en tiempo de ejecución. Funciona igual;
solo que no se benefició de la generación en compilación.

---

## 8. Errores comunes (y cómo evitarlos)

| Síntoma | Causa | Solución |
|---|---|---|
| `MapTo` funciona pero parece lento / no veo código generado | Falta `<InterceptorsNamespaces>` | Agrégalo (paso 2) |
| El generador no corre en mi proyecto | Falta la referencia al generador como `Analyzer` | Agrégala (paso 1) |
| Advertencia `MP0001` inesperada | Una propiedad del destino no tiene origen | Agrega la propiedad al origen, o renómbrala para que coincida |
| `ArgumentNullException` al mapear | El `source` era `null` | Verifica el origen antes de mapear |

---

## 9. Limitaciones actuales

Planificado, aún no disponible: aplanamiento de varios niveles, conversiones
enum↔numérico, configuración por miembro (ignorar, convertidores personalizados) y
`ProjectTo` para `IQueryable`. Consulta el [DESIGN.md](../DESIGN.md) para la hoja de
ruta completa.
