using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MapperPillow.Generator.Tests;

/// <summary>
/// Runs <see cref="MapToInterceptorGenerator"/> in-memory over a source string and
/// returns its result, so tests can assert on generated code and diagnostics
/// without compiling or executing anything.
/// </summary>
internal static class GeneratorHarness
{
    public static GeneratorRunResult Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));

        // Reference the running framework's assemblies plus the MapperPillow runtime,
        // so `MapTo` resolves to MapperPillow.MapperPillowExtensions inside the harness.
        var trusted = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = trusted
            .Split(Path.PathSeparator)
            .Where(path => path.Length > 0)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(global::MapperPillow.MapperPillowExtensions).Assembly.Location))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            assemblyName: "MapperPillowHarness",
            syntaxTrees: new[] { tree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new MapToInterceptorGenerator().AsSourceGenerator());

        driver = driver.RunGenerators(compilation);

        return driver.GetRunResult().Results.Single();
    }
}
