using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

public class BasicMappingTests
{
    public sealed class Source
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Computed => "read-only, should be ignored";
    }

    public sealed class Destination
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    [Fact]
    public void MapTo_copies_matching_properties_by_name()
    {
        var src = new Source { Id = 42, Name = "Grace" };

        var dto = src.MapTo<Destination>();

        Assert.Equal(42, dto.Id);
        Assert.Equal("Grace", dto.Name);
    }

    [Fact]
    public void Map_courtesy_alias_behaves_like_MapTo()
    {
        var src = new Source { Id = 1, Name = "Edsger" };

        var dto = src.Map<Destination>();

        Assert.Equal(1, dto.Id);
        Assert.Equal("Edsger", dto.Name);
    }

    [Fact]
    public void MapTo_throws_on_null_source()
    {
        Source? src = null;

        Assert.Throws<ArgumentNullException>(() => src!.MapTo<Destination>());
    }
}
