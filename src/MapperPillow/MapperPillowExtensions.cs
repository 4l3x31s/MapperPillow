namespace MapperPillow;

/// <summary>
/// The MapperPillow surface: fluent, zero-ceremony mapping extensions.
/// </summary>
/// <remarks>
/// The MapperPillow source generator discovers each call site and replaces it with
/// a compile-time interceptor, so no reflection runs at all. These bodies are only
/// reached by call sites the generator could not handle — which it reports as
/// MP0002 warnings.
/// </remarks>
public static class MapperPillowExtensions
{
    /// <summary>
    /// Maps <paramref name="source"/> to a new instance of
    /// <typeparamref name="TDestination"/>, copying properties that match by name.
    /// </summary>
    public static TDestination MapTo<TDestination>(this object source) => Fallback<TDestination>(source);

    /// <summary>
    /// AutoMapper-style courtesy alias for <see cref="MapTo{TDestination}"/>,
    /// so migrating code keeps reading naturally. Both are intercepted identically.
    /// </summary>
    public static TDestination Map<TDestination>(this object source) => Fallback<TDestination>(source);

    /// <summary>
    /// The non-intercepted path. Reaching this at runtime means the generator could
    /// not produce code for the call site (see MP0002) or interceptors are not
    /// enabled for the consuming project.
    /// </summary>
    private static TDestination Fallback<TDestination>(object source)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        return (TDestination)ReflectionMapper.Map(source, typeof(TDestination));
    }
}
