using System.Diagnostics.CodeAnalysis;

namespace MapperPillow;

/// <summary>
/// The trimming/Native AOT switch for MapperPillow's runtime reflection fallback.
/// </summary>
/// <remarks>
/// <para>
/// The fallback exists so that call sites the source generator cannot handle still
/// map at runtime. That convenience is incompatible with trimming and Native AOT:
/// the trimmer cannot see through <c>GetProperties</c>/<c>SetValue</c> on an
/// <see cref="object"/>, so it may remove the very members the fallback needs, and
/// the mapping then silently produces a partially populated object.
/// </para>
/// <para>
/// So <c>PublishTrimmed</c> and <c>PublishAot</c> builds turn it off by default:
/// <see cref="IsEnabled"/> becomes a compile-time <c>false</c> and the trimmer
/// removes the whole reflection branch. A call site the generator missed then throws
/// immediately instead of returning a half-mapped object — and the generator already
/// flagged it at build time with MP0002. Override with
/// <c>&lt;MapperPillowEnableReflectionFallback&gt;true&lt;/MapperPillowEnableReflectionFallback&gt;</c>
/// if you would rather keep the fallback and preserve the mapped types yourself.
/// </para>
/// </remarks>
public static class ReflectionFallback
{
    internal const string SwitchName = "MapperPillow.EnableReflectionFallback";

    internal const string TrimMessage =
        "MapperPillow's reflection fallback requires members that trimming may remove. " +
        "Enable interceptors so call sites are generated at compile time, and set " +
        "<MapperPillowEnableReflectionFallback>false</MapperPillowEnableReflectionFallback> " +
        "to remove the fallback from the published app.";

    internal const string ProjectToMessage =
        "MapperPillow: ProjectTo requires the source generator and has no runtime fallback. " +
        "Enable the generator with " +
        "<InterceptorsNamespaces>$(InterceptorsNamespaces);MapperPillow.Generated</InterceptorsNamespaces>, " +
        "and check the build for an MP0002 warning naming this call site.";

    internal const string DisabledMessage =
        "MapperPillow: this call site was not intercepted, and the reflection fallback is disabled. " +
        "Enable the generator with " +
        "<InterceptorsNamespaces>$(InterceptorsNamespaces);MapperPillow.Generated</InterceptorsNamespaces>, " +
        "and check the build for MP0002 warnings identifying the call sites that could not be generated.";

    /// <summary>
    /// Whether the runtime reflection fallback is available. Defaults to <c>true</c>;
    /// the trimmer substitutes a constant <c>false</c> when the fallback is disabled.
    /// </summary>
    [FeatureSwitchDefinition(SwitchName)]
    public static bool IsEnabled =>
        !AppContext.TryGetSwitch(SwitchName, out var enabled) || enabled;
}
