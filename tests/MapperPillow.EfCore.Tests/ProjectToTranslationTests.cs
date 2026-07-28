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
        _db.Orders.Add(new Order { Id = 10, Reference = "a-1", CustomerId = 1, Status = OrderStatus.Shipped, Discount = 15 });
        _db.Orders.Add(new Order { Id = 11, Reference = "A-2", CustomerId = 1 });
        _db.OrderItems.Add(new OrderItem { Id = 100, Sku = "SKU-1", OrderId = 10 });
        _db.OrderItems.Add(new OrderItem { Id = 101, Sku = "SKU-2", OrderId = 10 });
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
        Assert.Equal("a-1", dtos[0].Reference);
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

    [Fact]
    public void Enum_to_string_is_translated_by_the_provider()
    {
        // Decides whether the generator should flag enum -> string as untranslatable
        // in a projection. It emits `src.Status.ToString()`; if the provider handles
        // that, flagging it would be a false positive.
        var dtos = _db.Orders.ProjectTo<OrderStatusDto>().OrderBy(d => d.Id).ToList();

        Assert.Equal("Shipped", dtos[0].Status);
    }

    [Fact]
    public void A_value_converter_is_evaluated_on_the_client_not_by_the_database()
    {
        // Grounds the MP0003 wording. A converter does NOT throw: EF Core silently
        // evaluates it client-side. The real cost is that the database never computes
        // the member, so nothing composed afterwards can filter on it.
        // MP0003 is suppressed because triggering it is the point of the test.
#pragma warning disable MP0003
        var query = _db.Orders.ProjectTo<OrderConvertedDto>();
#pragma warning restore MP0003

        // The converter ran — 'a-1' came back upper-cased...
        var rows = query.OrderBy(d => d.Id).ToList();
        Assert.Equal("A-1", rows[0].Reference);

        // ...but the provider cannot filter on what it never computed.
        Assert.ThrowsAny<InvalidOperationException>(
            () => query.Where(d => d.Reference == "A-1").ToList());
    }

    [Fact]
    public void String_to_enum_is_rejected_by_the_provider_outright()
    {
        // The other half of MP0003, and it behaves differently from a converter:
        // Enum.Parse is not client-evaluated, it throws.
#pragma warning disable MP0003
        var query = _db.Orders.ProjectTo<OrderParsedDto>();
#pragma warning restore MP0003

        Assert.ThrowsAny<InvalidOperationException>(() => query.ToList());
    }

    [Fact]
    public void A_nested_object_is_projected_by_the_database()
    {
        // Regression guard for CS8122 as much as a translation test. The nested
        // branch used to emit `src.Customer is null ? ...`, and an expression tree
        // cannot contain a pattern — ProjectTo simply did not compile for this shape.
        var dtos = _db.Orders.ProjectTo<OrderNestedDto>().OrderBy(d => d.Id).ToList();

        Assert.Equal(2, dtos.Count);
        Assert.Equal("London", dtos[0].Customer.City);
        Assert.Equal("Ada", dtos[0].Customer.Name);
        Assert.Contains("JOIN", _db.Orders.ProjectTo<OrderNestedDto>().ToQueryString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_collection_valued_property_is_projected_by_the_database()
    {
        var dtos = _db.Orders.ProjectTo<OrderWithItemsDto>().OrderBy(d => d.Id).ToList();

        Assert.Equal(2, dtos.Count);
        Assert.Equal(2, dtos[0].Items.Count);
        Assert.Contains("SKU-1", dtos[0].Items.Select(i => i.Sku));
        Assert.Empty(dtos[1].Items);
    }

    [Fact]
    public void A_nullable_unwrap_is_translated_and_stays_filterable()
    {
        // Decides whether GetValueOrDefault() belongs in MP0003. The projection
        // running is not enough proof — a converter runs too, on the client. The
        // proof is that the provider can still FILTER on the member, which it cannot
        // do for anything it evaluated client-side.
        var dtos = _db.Orders.ProjectTo<OrderDiscountDto>().OrderBy(d => d.Id).ToList();
        Assert.Equal(15, dtos[0].Discount);
        Assert.Equal(0, dtos[1].Discount);   // NULL became default, not a crash

        Assert.Single(_db.Orders.ProjectTo<OrderDiscountDto>().Where(d => d.Discount > 10).ToList());
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
        public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    }

    public sealed class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
    }

    public enum OrderStatus { New, Shipped }

    public sealed class Order
    {
        public int Id { get; set; }
        public string Reference { get; set; } = "";
        public OrderStatus Status { get; set; }
        public int? Discount { get; set; }
        public int CustomerId { get; set; }
        public Customer Customer { get; set; } = null!;
        public List<OrderItem> Items { get; set; } = new();
    }

    public sealed class OrderItem
    {
        public int Id { get; set; }
        public string Sku { get; set; } = "";
        public int OrderId { get; set; }
    }

    // --- shapes for the remaining translation questions ---

    public sealed class CustomerDto
    {
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
    }

    public sealed class OrderItemDto
    {
        public string Sku { get; set; } = "";
    }

    /// <summary>A nested object inside a projection.</summary>
    public sealed class OrderNestedDto
    {
        public int Id { get; set; }
        public CustomerDto Customer { get; set; } = new();
    }

    /// <summary>A collection-valued property inside a projection.</summary>
    public sealed class OrderWithItemsDto
    {
        public int Id { get; set; }
        public List<OrderItemDto> Items { get; set; } = new();
    }

    /// <summary>A nullable source unwrapped with GetValueOrDefault().</summary>
    public sealed class OrderDiscountDto
    {
        public int Id { get; set; }
        public int Discount { get; set; }
    }

    public sealed class OrderStatusDto
    {
        public int Id { get; set; }
        public string Status { get; set; } = "";
    }

    public sealed class Shout : IValueConverter<string, string>
    {
        public string Convert(string source) => source.ToUpperInvariant();
    }

    public sealed class OrderConvertedDto
    {
        public int Id { get; set; }
        [MapConvert(typeof(Shout))]
        public string Reference { get; set; } = "";
    }

    public sealed class OrderParsedDto
    {
        public int Id { get; set; }
        public OrderStatus Reference { get; set; }
    }

    public sealed class OrderDto
    {
        public int Id { get; set; }
        public string Reference { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string CustomerCity { get; set; } = "";
    }
}
