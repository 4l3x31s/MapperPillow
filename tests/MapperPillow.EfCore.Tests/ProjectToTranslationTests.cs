using MapperPillow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MapperPillow.EfCore.Tests;

/// <summary>
/// The point of <c>ProjectTo</c> is that the database does the projection. These
/// tests assert that against a real EF Core provider: the generated <c>Select</c>
/// has to reach SQL, not be evaluated on the client after loading whole entities.
/// </summary>
/// <remarks>
/// This lives outside <c>MapperPillow.Tests</c> on purpose. EF Core is not
/// trim-safe, so enabling the AOT analyzers alongside it would drown the main
/// suite's zero-IL-warning guarantee in warnings that belong to EF.
/// </remarks>
public sealed class ProjectToTranslationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ShopContext _db;

    public ProjectToTranslationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new ShopContext(new DbContextOptionsBuilder<ShopContext>()
            .UseSqlite(_connection)
            .Options);
        _db.Database.EnsureCreated();

        _db.Customers.Add(new Customer { Id = 1, Name = "Ada", City = "London" });
        _db.Orders.Add(new Order { Id = 10, Reference = "A-1", CustomerId = 1 });
        _db.Orders.Add(new Order { Id = 11, Reference = "A-2", CustomerId = 1 });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    [Fact]
    public void ProjectTo_is_translated_to_SQL_and_not_evaluated_on_the_client()
    {
        var sql = _db.Orders.ProjectTo<OrderDto>().ToQueryString();

        // The projected columns are selected directly...
        Assert.Contains("\"o\".\"Reference\"", sql);
        // ...the flattened member became a join, not a second round trip...
        Assert.Contains("JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"City\"", sql);
        // ...and nothing pulls the whole entity back.
        Assert.DoesNotContain("SELECT \"o\".*", sql);
    }

    [Fact]
    public void ProjectTo_returns_the_projected_rows()
    {
        var dtos = _db.Orders.ProjectTo<OrderDto>().OrderBy(d => d.Id).ToList();

        Assert.Equal(2, dtos.Count);
        Assert.Equal("A-1", dtos[0].Reference);
        Assert.Equal("London", dtos[0].CustomerCity);
        Assert.Equal("Ada", dtos[0].CustomerName);
    }

    [Fact]
    public void ProjectTo_composes_with_further_operators_in_the_same_query()
    {
        // Filtering after the projection must still run in SQL — that is the whole
        // reason to project rather than map materialised entities.
        var query = _db.Orders.ProjectTo<OrderDto>().Where(d => d.Reference == "A-2");
        var sql = query.ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(query.ToList());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // --- model ---------------------------------------------------------------

    private sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<Customer> Customers => Set<Customer>();
    }

    public sealed class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
    }

    public sealed class Order
    {
        public int Id { get; set; }
        public string Reference { get; set; } = "";
        public int CustomerId { get; set; }
        public Customer Customer { get; set; } = null!;
    }

    public sealed class OrderDto
    {
        public int Id { get; set; }
        public string Reference { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string CustomerCity { get; set; } = "";
    }
}
