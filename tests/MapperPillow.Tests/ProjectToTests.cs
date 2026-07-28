using System.Diagnostics.CodeAnalysis;
using System.Linq;
using MapperPillow;
using Xunit;

namespace MapperPillow.Tests;

/// <summary>
/// <c>ProjectTo</c> must produce a real <see cref="IQueryable{T}"/> projection — a
/// <c>Select</c> whose lambda the C# compiler turns into an expression tree — so a
/// LINQ provider can translate it instead of materialising rows and mapping them.
/// </summary>
/// <remarks>
/// The trim/AOT suppressions cover <c>AsQueryable</c>, not MapperPillow: turning an
/// in-memory array into an <see cref="IQueryable{T}"/> needs dynamic code, which is
/// why it is a test-only construct. Real callers get their queryable from a provider
/// (an EF Core <c>DbSet</c>, say), and the projection MapperPillow emits is a plain
/// compiler-built expression tree either way.
/// </remarks>
[SuppressMessage("Trimming", "IL2026", Justification = "AsQueryable over an in-memory array is a test-only construct.")]
[SuppressMessage("AOT", "IL3050", Justification = "AsQueryable over an in-memory array is a test-only construct.")]
public class ProjectToTests
{
    public sealed class Order
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class OrderDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    [Fact]
    public void ProjectTo_maps_through_a_compile_time_interceptor()
    {
        MapperPillowTelemetry.Reset();
        var orders = new[]
        {
            new Order { Id = 1, Name = "first" },
            new Order { Id = 2, Name = "second" },
        }.AsQueryable();

        var dtos = orders.ProjectTo<OrderDto>().ToList();

        Assert.Equal(2, dtos.Count);
        Assert.Equal(1, dtos[0].Id);
        Assert.Equal("first", dtos[0].Name);
        Assert.Equal(2, dtos[1].Id);
        Assert.Equal(1, MapperPillowTelemetry.InterceptedCount);
    }

    [Fact]
    public void ProjectTo_returns_a_queryable_that_is_still_composable()
    {
        // The point of ProjectTo: the provider sees the projection, so further
        // operators compose into the same query rather than running in memory.
        MapperPillowTelemetry.Reset();
        var orders = new[]
        {
            new Order { Id = 1, Name = "first" },
            new Order { Id = 2, Name = "second" },
        }.AsQueryable();

        var projected = orders.ProjectTo<OrderDto>();
        var filtered = projected.Where(d => d.Id == 2).ToList();

        Assert.Single(filtered);
        Assert.Equal("second", filtered[0].Name);
        Assert.Equal(IQueryableExpressionKind, projected.Expression.NodeType.ToString());
    }

    // A projected queryable is a Call node (the Select), not a Constant — proof the
    // projection went into the expression tree rather than being applied in memory.
    private const string IQueryableExpressionKind = "Call";
}
