using System.Reflection;

namespace MapperPillow;

/// <summary>
/// v0 baseline mapper. Copies public instance properties that match by name and
/// have an assignable type. This is intentionally simple: the endgame is for the
/// source generator to emit equivalent, allocation-lean code at each call site.
/// </summary>
internal static class ReflectionMapper
{
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
