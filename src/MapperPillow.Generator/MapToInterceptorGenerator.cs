using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace MapperPillow.Generator;

/// <summary>
/// Milestone 2: replaces each <c>source.MapTo&lt;TDestination&gt;()</c> call with a
/// compile-time interceptor that constructs the destination and copies same-name,
/// assignable properties — no runtime reflection. Call sites the generator does not
/// cover fall back to the runtime reflection implementation.
/// </summary>
[Generator]
public sealed class MapToInterceptorGenerator : IIncrementalGenerator
{
    private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var callSites = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidateMapToCall(node),
                transform: static (ctx, ct) => GetCallSite(ctx, ct))
            .Where(static c => c is not null)
            .Select(static (c, _) => c!)
            .Collect();

        context.RegisterSourceOutput(callSites, static (spc, items) => Emit(spc, items));
    }

    // Cheap syntactic gate: `<expr>.MapTo<...>(...)`.
    private static bool IsCandidateMapToCall(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.ValueText: "MapTo" }
            }
        };

    private static CallSite? GetCallSite(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
        var generic = (GenericNameSyntax)memberAccess.Name;

        if (generic.TypeArgumentList.Arguments.Count != 1)
        {
            return null;
        }

        // Make sure this is really MapperPillow's MapTo, not an unrelated method.
        if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method ||
            method.Name != "MapTo" ||
            method.ContainingType?.ToDisplayString() != "MapperPillow.MapperPillowExtensions")
        {
            return null;
        }

        var destType = ctx.SemanticModel.GetTypeInfo(generic.TypeArgumentList.Arguments[0], ct).Type;
        var srcType = ctx.SemanticModel.GetTypeInfo(memberAccess.Expression, ct).Type;

        // Only intercept concrete, closed types. Open generics (e.g. the MapTo<T>
        // call inside the Map<T> alias) and error types are left to the runtime.
        if (destType is not INamedTypeSymbol || srcType is not INamedTypeSymbol ||
            destType.TypeKind == TypeKind.Error || srcType.TypeKind == TypeKind.Error)
        {
            return null;
        }

        var location = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);
        if (location is null)
        {
            return null;
        }

        var assignments = BuildAssignments(ctx.SemanticModel.Compilation, srcType, destType);

        return new CallSite(
            srcType.ToDisplayString(FullyQualified),
            destType.ToDisplayString(FullyQualified),
            string.Join(",", assignments),
            location.GetInterceptsLocationAttributeSyntax());
    }

    private static List<string> BuildAssignments(Compilation compilation, ITypeSymbol source, ITypeSymbol destination)
    {
        var sourceProps = ReadableProperties(source);
        var mapped = new List<string>();

        foreach (var dest in SettableProperties(destination))
        {
            if (!sourceProps.TryGetValue(dest.Name, out var src))
            {
                continue;
            }

            var conversion = compilation.ClassifyConversion(src.Type, dest.Type);
            if (conversion.IsIdentity || conversion.IsImplicit)
            {
                mapped.Add(dest.Name);
            }
        }

        return mapped;
    }

    private static Dictionary<string, IPropertySymbol> ReadableProperties(ITypeSymbol type)
    {
        var byName = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        foreach (var p in PublicInstanceProperties(type))
        {
            if (p.GetMethod is null || p.IsWriteOnly)
            {
                continue;
            }

            if (!byName.ContainsKey(p.Name))
            {
                byName[p.Name] = p;
            }
        }

        return byName;
    }

    private static IEnumerable<IPropertySymbol> SettableProperties(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in PublicInstanceProperties(type))
        {
            if (p.SetMethod is null || p.IsReadOnly || p.SetMethod.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            if (seen.Add(p.Name))
            {
                yield return p;
            }
        }
    }

    private static IEnumerable<IPropertySymbol> PublicInstanceProperties(ITypeSymbol type)
    {
        for (var t = type; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var member in t.GetMembers())
            {
                if (member is IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public } p)
                {
                    yield return p;
                }
            }
        }
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<CallSite> callSites)
    {
        if (callSites.IsDefaultOrEmpty)
        {
            return;
        }

        var groups = callSites
            .GroupBy(c => (c.SourceType, c.DestType, c.AssignmentsCsv))
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        // The interceptor marker attribute, kept file-local so it never collides.
        sb.AppendLine("namespace System.Runtime.CompilerServices");
        sb.AppendLine("{");
        sb.AppendLine("    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]");
        sb.AppendLine("    file sealed class InterceptsLocationAttribute : global::System.Attribute");
        sb.AppendLine("    {");
        sb.AppendLine("        public InterceptsLocationAttribute(int version, string data) { }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("namespace MapperPillow.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class MapToInterceptors");
        sb.AppendLine("    {");

        var index = 0;
        foreach (var group in groups)
        {
            var (sourceType, destType, csv) = group.Key;
            var props = csv.Length == 0 ? Array.Empty<string>() : csv.Split(',');

            foreach (var callSite in group)
            {
                sb.AppendLine("        " + callSite.AttributeText);
            }

            sb.AppendLine($"        public static {destType} Map_{index}(this object source)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (source is null) throw new global::System.ArgumentNullException(nameof(source));");
            sb.AppendLine($"            var typed = ({sourceType})source;");
            sb.AppendLine($"            var result = new {destType}");
            sb.AppendLine("            {");
            foreach (var prop in props)
            {
                sb.AppendLine($"                {prop} = typed.{prop},");
            }
            sb.AppendLine("            };");
            sb.AppendLine("            global::MapperPillow.MapperPillowTelemetry.MarkIntercepted();");
            sb.AppendLine("            return result;");
            sb.AppendLine("        }");
            sb.AppendLine();
            index++;
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource("MapperPillow.Interceptors.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private sealed record CallSite(string SourceType, string DestType, string AssignmentsCsv, string AttributeText);
}
