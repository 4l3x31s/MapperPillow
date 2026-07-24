using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

public class ConstructorMappingTests
{
    public sealed class Customer { public string Name { get; set; } = ""; public int Age { get; set; } }

    // Positional record: has a constructor and no parameterless ctor.
    public sealed record CustomerDto(string Name, int Age);

    [Fact]
    public void MapTo_maps_to_a_positional_record()
    {
        var customer = new Customer { Name = "Ada", Age = 36 };

        var dto = customer.MapTo<CustomerDto>();

        Assert.Equal("Ada", dto.Name);
        Assert.Equal(36, dto.Age);
    }

    public sealed class Src2 { public string Name { get; set; } = ""; public string Note { get; set; } = ""; }

    // Constructor sets a read-only property; the rest is set via initializer.
    public sealed class Dst2
    {
        public Dst2(string name) => Name = name;
        public string Name { get; }
        public string Note { get; set; } = "";
    }

    [Fact]
    public void MapTo_maps_via_constructor_plus_initializer()
    {
        var src = new Src2 { Name = "Ada", Note = "hi" };

        var dto = src.MapTo<Dst2>();

        Assert.Equal("Ada", dto.Name);
        Assert.Equal("hi", dto.Note);
    }
}
