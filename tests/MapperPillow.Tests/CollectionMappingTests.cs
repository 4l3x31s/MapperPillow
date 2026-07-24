using System.Collections.Generic;
using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

public class CollectionMappingTests
{
    public sealed class Item
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class ItemDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    [Fact]
    public void MapTo_maps_a_list()
    {
        MapperPillowTelemetry.Reset();
        var items = new List<Item>
        {
            new() { Id = 1, Name = "one" },
            new() { Id = 2, Name = "two" },
        };

        var dtos = items.MapTo<List<ItemDto>>();

        Assert.Equal(2, dtos.Count);
        Assert.Equal(1, dtos[0].Id);
        Assert.Equal("two", dtos[1].Name);
        Assert.Equal(1, MapperPillowTelemetry.InterceptedCount);
    }

    [Fact]
    public void MapTo_maps_to_an_array()
    {
        MapperPillowTelemetry.Reset();
        var items = new List<Item>
        {
            new() { Id = 10, Name = "ten" },
        };

        var dtos = items.MapTo<ItemDto[]>();

        Assert.Single(dtos);
        Assert.Equal(10, dtos[0].Id);
        Assert.Equal("ten", dtos[0].Name);
        Assert.Equal(1, MapperPillowTelemetry.InterceptedCount);
    }
}
