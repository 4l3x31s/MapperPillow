using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace MapperPillow;

/// <summary>
/// The runtime fallback mapper. Copies public instance properties that match by
/// name and have an assignable type — and nothing else. It deliberately does NOT
/// match the generated code: no flattening, no <c>[MapFrom]</c>, no
/// <c>[MapConvert]</c>, no constructor-based destinations. Reaching it means a call
/// site was not intercepted, which the generator reports as MP0002.
/// </summary>
internal static class ReflectionMapper
{
    [RequiresUnreferencedCode(ReflectionFallback.TrimMessage)]
    public static object Map(object source, Type destinationType)
    {
        if (destinationType.IsArray)
        {
            throw new NotSupportedException(
                $"MapperPillow: mapping to array type '{destinationType}' requires the source generator. " +
                "Enable it with <InterceptorsNamespaces>$(InterceptorsNamespaces);MapperPillow.Generated</InterceptorsNamespaces>.");
        }

        var destination = Activator.CreateInstance(destinationType)
            ?? throw new InvalidOperationException($"Could not create an instance of '{destinationType}'.");

        var sourceProps = source.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .ToDictionary(p => p.Name);

        foreach (var destProp in destinationType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!destProp.CanWrite)
            {
                continue;
            }

            if (!sourceProps.TryGetValue(destProp.Name, out var srcProp))
            {
                continue;
            }

            if (!destProp.PropertyType.IsAssignableFrom(srcProp.PropertyType))
            {
                continue;
            }

            destProp.SetValue(destination, srcProp.GetValue(source));
        }

        return destination;
    }
}
