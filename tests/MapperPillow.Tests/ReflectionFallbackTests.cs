using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

/// <summary>
/// Covers the trimming/Native AOT escape hatch: the reflection fallback must still
/// work by default, and must fail loudly — never silently half-map — once a trimmed
/// or Native AOT build has switched it off.
/// </summary>
public class ReflectionFallbackTests
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

    // An open generic call site: the generator cannot produce code for it, so this
    // deliberately reaches the runtime fallback. MP0002 flagging it is the point.
#pragma warning disable MP0002
    private static TDestination NotIntercepted<TDestination>(object source) => source.MapTo<TDestination>();
#pragma warning restore MP0002

    [Fact]
    public void Fallback_maps_when_it_is_enabled()
    {
        MapperPillowTelemetry.Reset();

        var dto = NotIntercepted<PersonDto>(new Person { Id = 3, Name = "Barbara" });

        Assert.Equal(3, dto.Id);
        Assert.Equal("Barbara", dto.Name);
        // It really did go through reflection, not a generated interceptor.
        Assert.Equal(0, MapperPillowTelemetry.InterceptedCount);
    }

    [Fact]
    public void Fallback_throws_instead_of_half_mapping_when_it_is_disabled()
    {
        Assert.True(ReflectionFallback.IsEnabled, "the fallback is enabled unless a build switches it off");
        AppContext.SetSwitch("MapperPillow.EnableReflectionFallback", false);
        try
        {
            Assert.False(ReflectionFallback.IsEnabled);

            var error = Assert.Throws<NotSupportedException>(
                () => NotIntercepted<PersonDto>(new Person { Id = 3, Name = "Barbara" }));

            Assert.Contains("MP0002", error.Message);
        }
        finally
        {
            AppContext.SetSwitch("MapperPillow.EnableReflectionFallback", true);
        }
    }

    [Fact]
    public void Intercepted_call_sites_are_unaffected_by_the_switch()
    {
        // The whole point: turning the fallback off must not break generated mappings.
        AppContext.SetSwitch("MapperPillow.EnableReflectionFallback", false);
        try
        {
            MapperPillowTelemetry.Reset();

            var dto = new Person { Id = 9, Name = "Margaret" }.MapTo<PersonDto>();

            Assert.Equal(9, dto.Id);
            Assert.Equal("Margaret", dto.Name);
            Assert.Equal(1, MapperPillowTelemetry.InterceptedCount);
        }
        finally
        {
            AppContext.SetSwitch("MapperPillow.EnableReflectionFallback", true);
        }
    }
}
