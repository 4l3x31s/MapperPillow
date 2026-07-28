using System.Diagnostics.CodeAnalysis;

namespace MapperPillow;

/// <summary>
/// The MapperPillow surface: fluent, zero-ceremony mapping extensions.
/// </summary>
/// <remarks>
/// The MapperPillow source generator discovers each call site and replaces it with
/// a compile-time interceptor, so no reflection runs at all. These bodies are only
/// reached by call sites the generator could not handle — which it reports as
/// MP0002 warnings — and they can be removed entirely from trimmed and Native AOT
/// builds via the <c>MapperPillowEnableReflectionFallback</c> property. See
/// <see cref="ReflectionFallback"/>.
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
    /// <remarks>
    /// The IL2026 suppression is deliberate and must not be moved onto the public
    /// <c>MapTo</c>/<c>Map</c> methods: the trim analyzer reads the original call
    /// site, not the interceptor that replaces it, so annotating them would warn on
    /// every call site — including the fully generated ones. Instead the reflection
    /// branch is gated on <see cref="ReflectionFallback.IsEnabled"/>, which the
    /// trimmer folds to a constant. Trimmed and Native AOT builds default it to
    /// false and drop this branch entirely, and any call site that needed it was
    /// already reported at build time as MP0002.
    /// </remarks>
    [UnconditionalSuppressMessage(
        "Trimming", "IL2026",
        Justification = "Guarded by the MapperPillow.EnableReflectionFallback feature switch, which trimmed and Native AOT builds set to false so this branch is removed. Call sites that need it are reported as MP0002 at build time.")]
    private static TDestination Fallback<TDestination>(object source)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (ReflectionFallback.IsEnabled)
        {
            return (TDestination)ReflectionMapper.Map(source, typeof(TDestination));
        }

        throw new NotSupportedException(ReflectionFallback.DisabledMessage);
    }
}
