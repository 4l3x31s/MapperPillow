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
}
