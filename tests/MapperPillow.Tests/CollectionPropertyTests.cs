using System.Collections.Generic;
using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

public class CollectionPropertyTests
{
    public sealed class Item { public int Id { get; set; } public string Name { get; set; } = ""; }
    public sealed class ItemDto { public int Id { get; set; } public string Name { get; set; } = ""; }

    public sealed class Order
    {
        public int Id { get; set; }
        public List<Item> Items { get; set; } = new();
    }

    public sealed class OrderDto
    {
        public int Id { get; set; }
        public List<ItemDto> Items { get; set; } = new();
    }

    [Fact]
    public void MapTo_maps_a_collection_valued_property()
    {
        MapperPillowTelemetry.Reset();
        var order = new Order
        {
            Id = 1,
            Items =
            {
                new Item { Id = 10, Name = "a" },
                new Item { Id = 20, Name = "b" },
            },
        };

        var dto = order.MapTo<OrderDto>();

        Assert.Equal(1, dto.Id);
        Assert.Equal(2, dto.Items.Count);
        Assert.Equal(10, dto.Items[0].Id);
        Assert.Equal("b", dto.Items[1].Name);
    }

    [Fact]
    public void MapTo_maps_a_null_collection_property_as_null()
    {
        var order = new Order { Id = 1, Items = null! };

        var dto = order.MapTo<OrderDto>();

        Assert.Null(dto.Items);
    }
}
