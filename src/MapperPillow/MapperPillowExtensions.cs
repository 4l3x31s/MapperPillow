namespace MapperPillow;

/// <summary>
/// The MapperPillow surface: fluent, zero-ceremony mapping extensions.
/// </summary>
/// <remarks>
/// In v0 these methods map by reflection. The MapperPillow source generator
/// discovers each call site and (from milestone 2 onward) replaces it with a
/// compile-time interceptor, so no reflection runs at all — while these bodies
/// remain a correctness fallback for any call site not yet intercepted.
/// </remarks>
public static class MapperPillowExtensions
{
    /// <summary>
    /// Maps <paramref name="source"/> to a new instance of
    /// <typeparamref name="TDestination"/>, copying properties that match by name.
    /// </summary>
    public static TDestination MapTo<TDestination>(this object source)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        return (TDestination)ReflectionMapper.Map(source, typeof(TDestination));
    }

    /// <summary>
    /// AutoMapper-style courtesy alias for <see cref="MapTo{TDestination}"/>,
    /// so migrating code keeps reading naturally. Prefer <c>MapTo</c> in new code.
    /// </summary>
    public static TDestination Map<TDestination>(this object source)
        => source.MapTo<TDestination>();
}
