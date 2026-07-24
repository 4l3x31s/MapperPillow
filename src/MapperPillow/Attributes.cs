namespace MapperPillow;

/// <summary>
/// Excludes a destination property from mapping. The property is left at its default
/// value and is not reported by the <c>MP0001</c> unmapped-member diagnostic.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MapIgnoreAttribute : Attribute
{
}

/// <summary>
/// Maps a destination property from an explicit source member path instead of by
/// name convention. The path is dot-separated and may cross nested objects, for
/// example <c>[MapFrom("Customer.Name")]</c>. Null intermediates yield the default.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class MapFromAttribute : Attribute
{
    public MapFromAttribute(string sourcePath) => SourcePath = sourcePath;

    /// <summary>The dot-separated source member path (e.g. <c>Customer.Name</c>).</summary>
    public string SourcePath { get; }
}
