using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

public class NestedMappingTests
{
    public sealed class Address
    {
        public string City { get; set; } = "";
        public string Zip { get; set; } = "";
    }

    public sealed class AddressDto
    {
        public string City { get; set; } = "";
        public string Zip { get; set; } = "";
    }

    public sealed class Customer
    {
        public string Name { get; set; } = "";
        public Address Address { get; set; } = new();
    }

    public sealed class CustomerDto
    {
        public string Name { get; set; } = "";
        public AddressDto Address { get; set; } = new();
    }

    [Fact]
    public void MapTo_maps_nested_objects()
    {
        MapperPillowTelemetry.Reset();
        var customer = new Customer
        {
            Name = "Ada",
            Address = new Address { City = "London", Zip = "EC1" },
        };

        var dto = customer.MapTo<CustomerDto>();

        Assert.Equal("Ada", dto.Name);
        Assert.NotNull(dto.Address);
        Assert.Equal("London", dto.Address.City);
        Assert.Equal("EC1", dto.Address.Zip);
        Assert.Equal(1, MapperPillowTelemetry.InterceptedCount);
    }

    [Fact]
    public void MapTo_maps_a_null_nested_object_as_null()
    {
        var customer = new Customer { Name = "Bob", Address = null! };

        var dto = customer.MapTo<CustomerDto>();

        Assert.Null(dto.Address);
    }
}
