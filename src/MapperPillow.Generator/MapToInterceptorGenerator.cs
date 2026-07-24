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
/// Replaces each concrete <c>source.MapTo&lt;TDestination&gt;()</c> call with a
/// compile-time interceptor — no runtime reflection. Supports scalar object
/// mapping (same-name, implicitly-convertible properties), nested complex objects
/// (mapped recursively with a null guard), and collection mapping (<c>List&lt;T&gt;</c>,
/// arrays, and the common read-only/list interfaces). Call sites the generator
/// cannot handle are left to the runtime fallback.
/// </summary>
[Generator]
public sealed class MapToInterceptorGenerator : IIncrementalGenerator
{
    private const int MaxNestingDepth = 6;
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

    private static bool IsCandidateMapToCall(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "MapTo" } }
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

        if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method ||
            method.Name != "MapTo" ||
            method.ContainingType?.ToDisplayString() != "MapperPillow.MapperPillowExtensions")
        {
            return null;
        }

        var destType = ctx.SemanticModel.GetTypeInfo(generic.TypeArgumentList.Arguments[0], ct).Type;
        var srcType = ctx.SemanticModel.GetTypeInfo(memberAccess.Expression, ct).Type;
        if (destType is null || srcType is null ||
            destType is ITypeParameterSymbol || srcType is ITypeParameterSymbol ||
            destType.TypeKind == TypeKind.Error || srcType.TypeKind == TypeKind.Error)
        {
            return null;
        }

        var location = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);
        if (location is null)
        {
            return null;
        }

        var body = BuildBody(ctx.SemanticModel.Compilation, srcType, destType);
        if (body is null)
        {
            return null; // not mappable here — leave it to the runtime fallback
        }

        return new CallSite(
            destType.ToDisplayString(FullyQualified),
            body,
            location.GetInterceptsLocationAttributeSyntax());
    }

    private static string? BuildBody(Compilation compilation, ITypeSymbol source, ITypeSymbol destination)
    {
        var destElement = CollectionElement(destination);
        if (destElement is not null)
        {
            var srcElement = EnumerableElement(source);
            if (srcElement is not INamedTypeSymbol srcElem ||
                destElement is not INamedTypeSymbol destElem ||
                !HasParameterlessCtor(destElem))
            {
                return null;
            }

            return BuildCollectionBody(compilation, srcElem, destination, destElem);
        }

        if (source is not INamedTypeSymbol ||
            destination is not INamedTypeSymbol destNamed ||
            !HasParameterlessCtor(destNamed))
        {
            return null;
        }

        return BuildScalarBody(compilation, source, destination);
    }

    private static string BuildScalarBody(Compilation compilation, ITypeSymbol source, ITypeSymbol destination)
    {
        var src = source.ToDisplayString(FullyQualified);
        var dest = destination.ToDisplayString(FullyQualified);
        var pairs = BuildAssignmentPairs(compilation, source, destination, "typed", ImmutableHashSet.Create(StringComparer.Ordinal, dest));

        var sb = new StringBuilder();
        sb.AppendLine("            if (source is null) throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine($"            var typed = ({src})source;");
        sb.AppendLine($"            var result = new {dest}");
        sb.AppendLine("            {");
        foreach (var pair in pairs)
        {
            sb.AppendLine($"                {pair},");
        }
        sb.AppendLine("            };");
        sb.AppendLine("            global::MapperPillow.MapperPillowTelemetry.MarkIntercepted();");
        sb.Append("            return result;");
        return sb.ToString();
    }

    private static string BuildCollectionBody(
        Compilation compilation, ITypeSymbol sourceElement, ITypeSymbol destination, ITypeSymbol destinationElement)
    {
        var srcElem = sourceElement.ToDisplayString(FullyQualified);
        var destElem = destinationElement.ToDisplayString(FullyQualified);
        var toArray = destination is IArrayTypeSymbol;
        var pairs = BuildAssignmentPairs(compilation, sourceElement, destinationElement, "item", ImmutableHashSet.Create(StringComparer.Ordinal, destElem));

        var sb = new StringBuilder();
        sb.AppendLine("            if (source is null) throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine($"            var typed = (global::System.Collections.Generic.IEnumerable<{srcElem}>)source;");
        sb.AppendLine($"            var result = new global::System.Collections.Generic.List<{destElem}>();");
        sb.AppendLine("            foreach (var item in typed)");
        sb.AppendLine("            {");
        sb.AppendLine($"                result.Add(new {destElem}");
        sb.AppendLine("                {");
        foreach (var pair in pairs)
        {
            sb.AppendLine($"                    {pair},");
        }
        sb.AppendLine("                });");
        sb.AppendLine("            }");
        sb.AppendLine("            global::MapperPillow.MapperPillowTelemetry.MarkIntercepted();");
        sb.Append(toArray
            ? "            return global::System.Linq.Enumerable.ToArray(result);"
            : "            return result;");
        return sb.ToString();
    }

    /// <summary>Builds "<c>Name = &lt;value expression&gt;</c>" pairs for a destination.</summary>
    private static List<string> BuildAssignmentPairs(
        Compilation compilation, ITypeSymbol source, ITypeSymbol destination, string accessor, ImmutableHashSet<string> visited)
    {
        var sourceProps = ReadableProperties(source);
        var pairs = new List<string>();

        foreach (var dest in SettableProperties(destination))
        {
            if (!sourceProps.TryGetValue(dest.Name, out var src))
            {
                continue;
            }

            var value = BuildValue(compilation, src.Type, dest.Type, $"{accessor}.{dest.Name}", visited);
            if (value is not null)
            {
                pairs.Add($"{dest.Name} = {value}");
            }
        }

        return pairs;
    }

    /// <summary>The right-hand side that produces a destination value, or null if unmappable.</summary>
    private static string? BuildValue(
        Compilation compilation, ITypeSymbol source, ITypeSymbol destination, string accessor, ImmutableHashSet<string> visited)
    {
        var conversion = compilation.ClassifyConversion(source, destination);
        if (conversion.IsIdentity || conversion.IsImplicit)
        {
            return accessor;
        }

        // Nested complex object: map it recursively, guarding null and cycles.
        if (source is INamedTypeSymbol &&
            destination is INamedTypeSymbol destNamed &&
            destination.IsReferenceType &&
            HasParameterlessCtor(destNamed) &&
            CollectionElement(destination) is null &&
            EnumerableElement(source) is null)
        {
            var destDisplay = destination.ToDisplayString(FullyQualified);
            if (visited.Contains(destDisplay) || visited.Count > MaxNestingDepth)
            {
                return null;
            }

            var pairs = BuildAssignmentPairs(compilation, source, destination, accessor, visited.Add(destDisplay));
            if (pairs.Count == 0)
            {
                return null;
            }

            return $"{accessor} is null ? null : new {destDisplay} {{ {string.Join(", ", pairs)} }}";
        }

        return null;
    }

    // --- collection shape detection -----------------------------------------

    private static ITypeSymbol? CollectionElement(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return array.ElementType;
        }

        if (type is INamedTypeSymbol named && named.TypeArguments.Length == 1)
        {
            switch (named.OriginalDefinition.SpecialType)
            {
                case SpecialType.System_Collections_Generic_IEnumerable_T:
                case SpecialType.System_Collections_Generic_IList_T:
                case SpecialType.System_Collections_Generic_ICollection_T:
                case SpecialType.System_Collections_Generic_IReadOnlyList_T:
                case SpecialType.System_Collections_Generic_IReadOnlyCollection_T:
                    return named.TypeArguments[0];
            }

            if (named.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.List<T>")
            {
                return named.TypeArguments[0];
            }
        }

        return null;
    }

    private static ITypeSymbol? EnumerableElement(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return array.ElementType;
        }

        IEnumerable<INamedTypeSymbol> candidates = type.AllInterfaces;
        if (type is INamedTypeSymbol named)
        {
            candidates = new[] { named }.Concat(candidates);
        }

        foreach (var candidate in candidates)
        {
            if (candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            {
                return candidate.TypeArguments[0];
            }
        }

        return null;
    }

    // --- property helpers ----------------------------------------------------

    private static bool HasParameterlessCtor(INamedTypeSymbol type) =>
        type.IsValueType ||
        type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);

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

    // --- emission ------------------------------------------------------------

    private static void Emit(SourceProductionContext spc, ImmutableArray<CallSite> callSites)
    {
        if (callSites.IsDefaultOrEmpty)
        {
            return;
        }

        var groups = callSites
            .GroupBy(c => (c.ReturnType, c.Body))
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable disable");
        sb.AppendLine();
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
            foreach (var callSite in group)
            {
                sb.AppendLine("        " + callSite.AttributeText);
            }

            sb.AppendLine($"        public static {group.Key.ReturnType} Map_{index}(this object source)");
            sb.AppendLine("        {");
            sb.AppendLine(group.Key.Body);
            sb.AppendLine("        }");
            sb.AppendLine();
            index++;
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        spc.AddSource("MapperPillow.Interceptors.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private sealed record CallSite(string ReturnType, string Body, string AttributeText);
}
