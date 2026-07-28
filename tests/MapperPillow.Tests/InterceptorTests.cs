using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

public class InterceptorTests
{
    public sealed class Person
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class PersonDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    [Fact]
    public void MapTo_is_served_by_a_compile_time_interceptor()
    {
        MapperPillowTelemetry.Reset();
        var person = new Person { Id = 5, Name = "Linus" };

        var dto = person.MapTo<PersonDto>();

        // Correct result...
        Assert.Equal(5, dto.Id);
        Assert.Equal("Linus", dto.Name);
        // ...and it went through generated code, not the reflection fallback.
        Assert.Equal(1, MapperPillowTelemetry.InterceptedCount);
    }

    [Fact]
    public void Map_alias_is_served_by_a_compile_time_interceptor()
    {
        // The AutoMapper-migration alias must get the same compile-time path as
        // MapTo — otherwise every migrating call site silently runs on reflection.
        MapperPillowTelemetry.Reset();
        var person = new Person { Id = 7, Name = "Ada" };

        var dto = person.Map<PersonDto>();

        Assert.Equal(7, dto.Id);
        Assert.Equal("Ada", dto.Name);
        Assert.Equal(1, MapperPillowTelemetry.InterceptedCount);
    }
}
