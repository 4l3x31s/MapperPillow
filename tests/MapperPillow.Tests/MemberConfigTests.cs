using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

public class MemberConfigTests
{
    public sealed class Customer { public string Name { get; set; } = ""; }

    public sealed class Order
    {
        public int Id { get; set; }
        public Customer Customer { get; set; } = new();
        public string Secret { get; set; } = "";
    }

    public sealed class OrderDto
    {
        public int Id { get; set; }

        [MapFrom("Customer.Name")]
        public string Buyer { get; set; } = "";

        [MapFrom("Secret")]
        public string Hidden { get; set; } = "";

        [MapIgnore]
        public string Notes { get; set; } = "";
    }

    [Fact]
    public void MapFrom_maps_from_a_nested_path()
    {
        var order = new Order { Id = 1, Customer = new Customer { Name = "Ada" } };

        var dto = order.MapTo<OrderDto>();

        Assert.Equal("Ada", dto.Buyer);
    }

    [Fact]
    public void MapFrom_maps_from_a_renamed_member()
    {
        var order = new Order { Id = 1, Secret = "s3cr3t" };

        var dto = order.MapTo<OrderDto>();

        Assert.Equal("s3cr3t", dto.Hidden);
    }

    [Fact]
    public void MapIgnore_leaves_the_member_at_its_default()
    {
        var order = new Order { Id = 1 };

        var dto = order.MapTo<OrderDto>();

        Assert.Equal("", dto.Notes);
    }
}
