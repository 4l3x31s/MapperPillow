using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

public class FlatteningTests
{
    public sealed class Customer
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    public sealed class Order
    {
        public int Id { get; set; }
        public Customer Customer { get; set; } = new();
    }

    public sealed class OrderDto
    {
        public int Id { get; set; }
        public string CustomerName { get; set; } = "";
        public int CustomerAge { get; set; }
    }

    [Fact]
    public void MapTo_flattens_nested_member_paths()
    {
        MapperPillowTelemetry.Reset();
        var order = new Order
        {
            Id = 7,
            Customer = new Customer { Name = "Ada", Age = 36 },
        };

        var dto = order.MapTo<OrderDto>();

        Assert.Equal(7, dto.Id);
        Assert.Equal("Ada", dto.CustomerName);
        Assert.Equal(36, dto.CustomerAge);
    }

    [Fact]
    public void MapTo_flattens_null_intermediate_to_default()
    {
        var order = new Order { Id = 1, Customer = null! };

        var dto = order.MapTo<OrderDto>();

        Assert.Equal("", dto.CustomerName == null ? "" : dto.CustomerName);
        Assert.Equal(0, dto.CustomerAge);
    }
}
