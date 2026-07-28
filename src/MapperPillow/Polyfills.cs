#if !NET9_0_OR_GREATER

namespace System.Diagnostics.CodeAnalysis;

/// <summary>
/// Polyfill of the .NET 9+ attribute of the same name, for the <c>net8.0</c> target.
/// </summary>
/// <remarks>
/// The trimmer resolves this attribute by full type name, not by assembly identity, so
/// an internal declaration here is honoured exactly like the framework one: it lets
/// ILLink treat <see cref="MapperPillow.ReflectionFallback.IsEnabled"/> as a constant
/// and remove the reflection branch from trimmed <c>net8.0</c> apps.
/// Same trick as the <c>IsExternalInit</c> polyfill in the generator project.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
internal sealed class FeatureSwitchDefinitionAttribute : Attribute
{
    public FeatureSwitchDefinitionAttribute(string switchName) => SwitchName = switchName;

    /// <summary>The name of the feature switch that provides the value for the property.</summary>
    public string SwitchName { get; }
}

#endif
