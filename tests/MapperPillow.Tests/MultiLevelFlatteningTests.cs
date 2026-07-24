using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

public class MultiLevelFlatteningTests
{
    public sealed class Address { public string City { get; set; } = ""; }
    public sealed class Customer { public Address Address { get; set; } = new(); }
    public sealed class Order { public Customer Customer { get; set; } = new(); }

    public sealed class OrderDto
    {
        public string CustomerAddressCity { get; set; } = "";   // Order.Customer.Address.City
    }

    [Fact]
    public void MapTo_flattens_multiple_levels()
    {
        var order = new Order
        {
            Customer = new Customer { Address = new Address { City = "London" } },
        };

        var dto = order.MapTo<OrderDto>();

        Assert.Equal("London", dto.CustomerAddressCity);
    }

    [Fact]
    public void MapTo_multi_level_null_intermediate_yields_default()
    {
        var order = new Order { Customer = null! };

        var dto = order.MapTo<OrderDto>();

        Assert.Null(dto.CustomerAddressCity);
    }
}
