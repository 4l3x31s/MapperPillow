using Xunit;

namespace MapperPillow.Generator.Tests;

public class GeneratorTests
{
    [Fact]
    public void Warns_MP0001_naming_the_unmapped_member()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            namespace Demo;
            public class Src { public int Id { get; set; } }
            public class Dst { public int Id { get; set; } public string Notes { get; set; } = ""; }
            public static class Run { public static Dst Do(Src s) => s.MapTo<Dst>(); }
            """);

        var diagnostic = result.Diagnostics.Single(d => d.Id == "MP0001");
        Assert.Contains("Notes", diagnostic.GetMessage());
    }

    [Fact]
    public void No_diagnostic_when_every_member_is_mapped()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            namespace Demo;
            public class Src { public int Id { get; set; } public string Name { get; set; } = ""; }
            public class Dst { public int Id { get; set; } public string Name { get; set; } = ""; }
            public static class Run { public static Dst Do(Src s) => s.MapTo<Dst>(); }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "MP0001");
    }

    [Fact]
    public void Generates_a_scalar_interceptor()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            namespace Demo;
            public class Src { public int Id { get; set; } }
            public class Dst { public int Id { get; set; } }
            public static class Run { public static Dst Do(Src s) => s.MapTo<Dst>(); }
            """);

        var generated = result.GeneratedSources.Single().SourceText.ToString();
        Assert.Contains("InterceptsLocation", generated);
        Assert.Contains("Id = typed.Id", generated);
    }

    [Fact]
    public void Generates_a_foreach_for_collections()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            using System.Collections.Generic;
            namespace Demo;
            public class Src { public int Id { get; set; } }
            public class Dst { public int Id { get; set; } }
            public static class Run { public static List<Dst> Do(List<Src> s) => s.MapTo<List<Dst>>(); }
            """);

        var generated = result.GeneratedSources.Single().SourceText.ToString();
        Assert.Contains("foreach (var item in typed)", generated);
    }

    [Fact]
    public void MapIgnore_suppresses_the_MP0001_diagnostic()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            namespace Demo;
            public class Src { public int Id { get; set; } }
            public class Dst { public int Id { get; set; } [MapIgnore] public string Notes { get; set; } = ""; }
            public static class Run { public static Dst Do(Src s) => s.MapTo<Dst>(); }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "MP0001");
    }

    [Fact]
    public void MapFrom_emits_the_explicit_source_path()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            namespace Demo;
            public class Customer { public string Name { get; set; } = ""; }
            public class Src { public Customer Customer { get; set; } = new(); }
            public class Dst { [MapFrom("Customer.Name")] public string Buyer { get; set; } = ""; }
            public static class Run { public static Dst Do(Src s) => s.MapTo<Dst>(); }
            """);

        var generated = result.GeneratedSources.Single().SourceText.ToString();
        Assert.Contains("Buyer =", generated);
        Assert.Contains(".Customer", generated);
    }

    [Fact]
    public void Generates_a_constructor_call_for_positional_records()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            namespace Demo;
            public class Src { public string Name { get; set; } = ""; public int Age { get; set; } }
            public record Dst(string Name, int Age);
            public static class Run { public static Dst Do(Src s) => s.MapTo<Dst>(); }
            """);

        var generated = result.GeneratedSources.Single().SourceText.ToString();
        Assert.Contains("new global::Demo.Dst(", generated);
    }

    [Fact]
    public void Leaves_open_generics_to_the_runtime_fallback()
    {
        // The MapTo<T> inside a user's own generic method must NOT be intercepted.
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            namespace Demo;
            public static class Run { public static T To<T>(object s) => s.MapTo<T>(); }
            """);

        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Intercepts_the_Map_alias_like_MapTo()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            namespace Demo;
            public class Src { public int Id { get; set; } }
            public class Dst { public int Id { get; set; } }
            public static class Run { public static Dst Do(Src s) => s.Map<Dst>(); }
            """);

        var generated = result.GeneratedSources.Single().SourceText.ToString();
        Assert.Contains("InterceptsLocation", generated);
        Assert.Contains("Id = typed.Id", generated);
    }

    [Fact]
    public void Ignores_Map_methods_that_are_not_MapperPillows()
    {
        // A user's own extension method named Map<T> must not be touched at all.
        var result = GeneratorHarness.Run(
            """
            namespace Demo;
            public class Src { public int Id { get; set; } }
            public class Dst { public int Id { get; set; } }
            public static class Mine { public static T Map<T>(this object s) => default!; }
            public static class Run { public static Dst Do(Src s) => s.Map<Dst>(); }
            """);

        Assert.Empty(result.GeneratedSources);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "MP0002");
    }

    [Fact]
    public void Warns_MP0002_when_a_call_site_falls_back_to_reflection()
    {
        // No compile-time mapping is possible: Dst has no parameterless constructor
        // and no constructor that the source can satisfy.
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            namespace Demo;
            public class Src { public int Id { get; set; } }
            public class Dst { public Dst(string unmatched) { } public int Id { get; set; } }
            public static class Run { public static Dst Do(Src s) => s.MapTo<Dst>(); }
            """);

        var diagnostic = result.Diagnostics.Single(d => d.Id == "MP0002");
        Assert.Contains("MapTo<Dst>", diagnostic.GetMessage());
    }

    [Fact]
    public void Warns_MP0002_for_open_generic_call_sites()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            namespace Demo;
            public static class Run { public static T To<T>(object s) => s.MapTo<T>(); }
            """);

        Assert.Contains(result.Diagnostics, d => d.Id == "MP0002");
    }

    [Fact]
    public void ProjectTo_emits_a_Queryable_Select_the_compiler_turns_into_an_expression_tree()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            using System.Linq;
            namespace Demo;
            public class Order { public int Id { get; set; } }
            public class OrderDto { public int Id { get; set; } }
            public static class Run { public static IQueryable<OrderDto> Do(IQueryable<Order> q) => q.ProjectTo<OrderDto>(); }
            """);

        var generated = result.GeneratedSources.Single().SourceText.ToString();
        // Queryable.Select takes Expression<Func<,>>, so the emitted lambda becomes an
        // expression tree at compile time — nothing is composed at runtime.
        Assert.Contains("global::System.Linq.Queryable.Select(typed, src =>", generated);
        Assert.Contains("new global::Demo.OrderDto { Id = src.Id }", generated);
    }

    [Fact]
    public void ProjectTo_interceptor_takes_IQueryable_not_object()
    {
        // The interceptor's signature has to match the intercepted method's, and
        // ProjectTo is declared on IQueryable rather than object.
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            using System.Linq;
            namespace Demo;
            public class Order { public int Id { get; set; } }
            public class OrderDto { public int Id { get; set; } }
            public static class Run { public static IQueryable<OrderDto> Do(IQueryable<Order> q) => q.ProjectTo<OrderDto>(); }
            """);

        var generated = result.GeneratedSources.Single().SourceText.ToString();
        Assert.Contains("global::System.Linq.IQueryable<global::Demo.OrderDto> Map_0(this global::System.Linq.IQueryable source)", generated);
    }

    [Fact]
    public void ProjectTo_flattens_like_MapTo_does()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            using System.Linq;
            namespace Demo;
            public class Customer { public string City { get; set; } = ""; }
            public class Order { public Customer Customer { get; set; } = new(); }
            public class OrderDto { public string CustomerCity { get; set; } = ""; }
            public static class Run { public static IQueryable<OrderDto> Do(IQueryable<Order> q) => q.ProjectTo<OrderDto>(); }
            """);

        var generated = result.GeneratedSources.Single().SourceText.ToString();
        Assert.Contains("src.Customer", generated);
        Assert.Contains("CustomerCity =", generated);
    }

    [Fact]
    public void Warns_MP0003_when_a_projection_uses_a_value_converter()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            using System.Linq;
            namespace Demo;
            public class Cents : IValueConverter<int, string> { public string Convert(int s) => s.ToString(); }
            public class Order { public int Total { get; set; } }
            public class OrderDto { [MapConvert(typeof(Cents))] public string Total { get; set; } = ""; }
            public static class Run { public static IQueryable<OrderDto> Do(IQueryable<Order> q) => q.ProjectTo<OrderDto>(); }
            """);

        var diagnostic = result.Diagnostics.Single(d => d.Id == "MP0003");
        Assert.Contains("Total", diagnostic.GetMessage());
        Assert.Contains("MapConvert", diagnostic.GetMessage());
    }

    [Fact]
    public void Warns_MP0003_when_a_projection_parses_a_string_into_an_enum()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            using System.Linq;
            namespace Demo;
            public enum Status { New, Shipped }
            public class Order { public string Status { get; set; } = ""; }
            public class OrderDto { public Status Status { get; set; } }
            public static class Run { public static IQueryable<OrderDto> Do(IQueryable<Order> q) => q.ProjectTo<OrderDto>(); }
            """);

        var diagnostic = result.Diagnostics.Single(d => d.Id == "MP0003");
        Assert.Contains("Status", diagnostic.GetMessage());
    }

    [Fact]
    public void No_MP0003_for_constructs_a_provider_can_translate()
    {
        // enum -> string is emitted as ToString(), which EF Core translates — flagging
        // it would be a false positive. Verified in MapperPillow.EfCore.Tests.
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            using System.Linq;
            namespace Demo;
            public enum Status { New, Shipped }
            public class Customer { public string City { get; set; } = ""; }
            public class Order { public Status Status { get; set; } public int? Total { get; set; } public Customer Customer { get; set; } = new(); }
            public class OrderDto { public string Status { get; set; } = ""; public int Total { get; set; } public string CustomerCity { get; set; } = ""; }
            public static class Run { public static IQueryable<OrderDto> Do(IQueryable<Order> q) => q.ProjectTo<OrderDto>(); }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "MP0003");
    }

    [Fact]
    public void No_MP0003_for_MapTo_which_runs_in_memory()
    {
        // The same converter is perfectly fine outside a query.
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            namespace Demo;
            public class Cents : IValueConverter<int, string> { public string Convert(int s) => s.ToString(); }
            public class Order { public int Total { get; set; } }
            public class OrderDto { [MapConvert(typeof(Cents))] public string Total { get; set; } = ""; }
            public static class Run { public static OrderDto Do(Order o) => o.MapTo<OrderDto>(); }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "MP0003");
    }

    [Fact]
    public void No_MP0002_when_the_call_site_is_intercepted()
    {
        var result = GeneratorHarness.Run(
            """
            using MapperPillow;
            namespace Demo;
            public class Src { public int Id { get; set; } }
            public class Dst { public int Id { get; set; } }
            public static class Run { public static Dst Do(Src s) => s.MapTo<Dst>(); }
            """);

        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "MP0002");
    }
}
