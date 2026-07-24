using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

public class ConversionTests
{
    public enum Color { Red = 1, Green = 2, Blue = 3 }

    public sealed class HasColor { public Color Color { get; set; } }
    public sealed class HasColorNumber { public int Color { get; set; } }

    [Fact]
    public void MapTo_maps_enum_to_its_numeric_value()
    {
        var src = new HasColor { Color = Color.Green };

        var dto = src.MapTo<HasColorNumber>();

        Assert.Equal(2, dto.Color);
    }

    [Fact]
    public void MapTo_maps_numeric_value_to_enum()
    {
        var src = new HasColorNumber { Color = 3 };

        var dto = src.MapTo<HasColor>();

        Assert.Equal(Color.Blue, dto.Color);
    }

    public sealed class HasNullableCount { public int? Count { get; set; } }
    public sealed class HasCount { public int Count { get; set; } }

    [Fact]
    public void MapTo_unwraps_a_nullable_value()
    {
        var src = new HasNullableCount { Count = 5 };

        var dto = src.MapTo<HasCount>();

        Assert.Equal(5, dto.Count);
    }

    [Fact]
    public void MapTo_maps_a_null_nullable_to_default()
    {
        var src = new HasNullableCount { Count = null };

        var dto = src.MapTo<HasCount>();

        Assert.Equal(0, dto.Count);
    }
}
