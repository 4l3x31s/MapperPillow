using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

public class EnumMappingTests
{
    public enum SourceStatus { Pending = 0, Shipped = 1, Delivered = 2 }
    public enum TargetStatus { Pending = 0, Shipped = 1, Delivered = 2 }

    public sealed class WithSourceEnum { public SourceStatus Status { get; set; } }
    public sealed class WithTargetEnum { public TargetStatus Status { get; set; } }
    public sealed class WithStringStatus { public string Status { get; set; } = ""; }

    [Fact]
    public void MapTo_maps_enum_to_a_different_enum_by_value()
    {
        var src = new WithSourceEnum { Status = SourceStatus.Shipped };

        var dto = src.MapTo<WithTargetEnum>();

        Assert.Equal(TargetStatus.Shipped, dto.Status);
    }

    [Fact]
    public void MapTo_maps_enum_to_string()
    {
        var src = new WithSourceEnum { Status = SourceStatus.Delivered };

        var dto = src.MapTo<WithStringStatus>();

        Assert.Equal("Delivered", dto.Status);
    }

    [Fact]
    public void MapTo_maps_string_to_enum()
    {
        var src = new WithStringStatus { Status = "Shipped" };

        var dto = src.MapTo<WithSourceEnum>();

        Assert.Equal(SourceStatus.Shipped, dto.Status);
    }
}
