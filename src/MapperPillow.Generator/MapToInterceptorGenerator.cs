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
/// mapping, nested complex objects (recursive, null-guarded), one-level flattening
/// (<c>CustomerName</c> → <c>Customer.Name</c>) and collection mapping. Destination
/// members left unmapped are reported as MP0001 warnings. Call sites the generator
/// cannot handle at all are left to the runtime fallback.
/// </summary>
[Generator]
public sealed class MapToInterceptorGenerator : IIncrementalGenerator
{
    private const int MaxNestingDepth = 6;
    private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

    private static readonly DiagnosticDescriptor UnmappedMember = new(
        id: "MP0001",
        title: "Unmapped destination member",
        messageFormat: "MapTo<{0}> leaves destination member(s) unmapped: {1}",
        category: "MapperPillow",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A destination property has no matching source member. Map it, or ignore it once member configuration is available.");

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
            destType.TypeKind == TypeKind.Error || srcType.TypeKind == TypeKind.Error ||
            IsFileLocal(destType) || IsFileLocal(srcType))
        {
            // File-local types cannot be referenced from the generated file; fall back.
            return null;
        }

        var location = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);
        if (location is null)
        {
            return null;
        }

        var plan = BuildBody(ctx.SemanticModel.Compilation, srcType, destType);
        if (plan is null)
        {
            return null; // not mappable here — leave it to the runtime fallback
        }

        return new CallSite(
            destType.ToDisplayString(FullyQualified),
            destType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            plan.Value.Body,
            plan.Value.UnmappedCsv,
            location.GetInterceptsLocationAttributeSyntax(),
            invocation.GetLocation());
    }

    private static (string Body, string UnmappedCsv)? BuildBody(Compilation compilation, ITypeSymbol source, ITypeSymbol destination)
    {
        var destElement = CollectionElement(destination);
        if (destElement is not null)
        {
            var srcElement = EnumerableElement(source);
            if (srcElement is not INamedTypeSymbol srcElem ||
                destElement is not INamedTypeSymbol destElem ||
                !HasParameterlessCtor(destElem) ||
                IsFileLocal(destElem) || IsFileLocal(srcElem))
            {
                return null;
            }

            var elemPairs = BuildAssignmentPairs(compilation, srcElem, destElem, "item", ImmutableHashSet.Create(StringComparer.Ordinal, destElem.ToDisplayString(FullyQualified)));
            return (BuildCollectionBody(destination, destElem.ToDisplayString(FullyQualified), srcElem.ToDisplayString(FullyQualified), elemPairs), Unmapped(destElem, elemPairs));
        }

        if (source is not INamedTypeSymbol ||
            destination is not INamedTypeSymbol destNamed ||
            !HasParameterlessCtor(destNamed))
        {
            return null;
        }

        var pairs = BuildAssignmentPairs(compilation, source, destination, "typed", ImmutableHashSet.Create(StringComparer.Ordinal, destination.ToDisplayString(FullyQualified)));
        return (BuildScalarBody(source.ToDisplayString(FullyQualified), destination.ToDisplayString(FullyQualified), pairs), Unmapped(destNamed, pairs));
    }

    private static string Unmapped(ITypeSymbol destination, List<(string Name, string Expr)> pairs)
    {
        var mapped = new HashSet<string>(pairs.Select(p => p.Name), StringComparer.Ordinal);
        var unmapped = SettableProperties(destination)
            .Select(p => p.Name)
            .Where(n => !mapped.Contains(n))
            .Distinct(StringComparer.Ordinal)
            .Select(n => $"'{n}'");
        return string.Join(", ", unmapped);
    }

    private static string BuildScalarBody(string src, string dest, List<(string Name, string Expr)> pairs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("            if (source is null) throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine($"            var typed = ({src})source;");
        sb.AppendLine($"            var result = new {dest}");
        sb.AppendLine("            {");
        foreach (var (name, expr) in pairs)
        {
            sb.AppendLine($"                {name} = {expr},");
        }
        sb.AppendLine("            };");
        sb.AppendLine("            global::MapperPillow.MapperPillowTelemetry.MarkIntercepted();");
        sb.Append("            return result;");
        return sb.ToString();
    }

    private static string BuildCollectionBody(ITypeSymbol destination, string destElem, string srcElem, List<(string Name, string Expr)> pairs)
    {
        var toArray = destination is IArrayTypeSymbol;

        var sb = new StringBuilder();
        sb.AppendLine("            if (source is null) throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine($"            var typed = (global::System.Collections.Generic.IEnumerable<{srcElem}>)source;");
        sb.AppendLine($"            var result = new global::System.Collections.Generic.List<{destElem}>();");
        sb.AppendLine("            foreach (var item in typed)");
        sb.AppendLine("            {");
        sb.AppendLine($"                result.Add(new {destElem}");
        sb.AppendLine("                {");
        foreach (var (name, expr) in pairs)
        {
            sb.AppendLine($"                    {name} = {expr},");
        }
        sb.AppendLine("                });");
        sb.AppendLine("            }");
        sb.AppendLine("            global::MapperPillow.MapperPillowTelemetry.MarkIntercepted();");
        sb.Append(toArray
            ? "            return global::System.Linq.Enumerable.ToArray(result);"
            : "            return result;");
        return sb.ToString();
    }

    private static List<(string Name, string Expr)> BuildAssignmentPairs(
        Compilation compilation, ITypeSymbol source, ITypeSymbol destination, string accessor, ImmutableHashSet<string> visited)
    {
        var sourceProps = ReadableProperties(source);
        var pairs = new List<(string Name, string Expr)>();

        foreach (var dest in SettableProperties(destination))
        {
            string? value = null;

            if (sourceProps.TryGetValue(dest.Name, out var src))
            {
                value = BuildValue(compilation, src.Type, dest.Type, $"{accessor}.{dest.Name}", visited);
            }

            value ??= BuildFlattenedValue(compilation, sourceProps.Values, dest, accessor);

            if (value is not null)
            {
                pairs.Add((dest.Name, value));
            }
        }

        return pairs;
    }

    private static string? BuildValue(
        Compilation compilation, ITypeSymbol source, ITypeSymbol destination, string accessor, ImmutableHashSet<string> visited)
    {
        var conversion = compilation.ClassifyConversion(source, destination);
        if (conversion.IsIdentity || conversion.IsImplicit)
        {
            return accessor;
        }

        // Collection-valued property: map each element with LINQ (null -> null).
        var destCollElem = CollectionElement(destination);
        if (destCollElem is not null)
        {
            if (EnumerableElement(source) is not INamedTypeSymbol srcElem ||
                destCollElem is not INamedTypeSymbol destElem ||
                !HasParameterlessCtor(destElem) ||
                destElem.IsFileLocal || srcElem.IsFileLocal)
            {
                return null;
            }

            var elemDisplay = destElem.ToDisplayString(FullyQualified);
            if (visited.Contains(elemDisplay) || visited.Count > MaxNestingDepth)
            {
                return null;
            }

            var childVisited = visited.Add(elemDisplay);
            var lambda = $"e{childVisited.Count}";
            var elemPairs = BuildAssignmentPairs(compilation, srcElem, destElem, lambda, childVisited);
            if (elemPairs.Count == 0)
            {
                return null;
            }

            var newElem = $"new {elemDisplay} {{ {string.Join(", ", elemPairs.Select(p => $"{p.Name} = {p.Expr}"))} }}";
            var projected = $"global::System.Linq.Enumerable.Select({accessor}, {lambda} => {newElem})";
            var materialized = destination is IArrayTypeSymbol
                ? $"global::System.Linq.Enumerable.ToArray({projected})"
                : $"global::System.Linq.Enumerable.ToList({projected})";
            return $"{accessor} == null ? null : {materialized}";
        }

        if (source is INamedTypeSymbol &&
            destination is INamedTypeSymbol destNamed &&
            destination.IsReferenceType &&
            HasParameterlessCtor(destNamed) &&
            !destNamed.IsFileLocal &&
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

            var body = string.Join(", ", pairs.Select(p => $"{p.Name} = {p.Expr}"));
            return $"{accessor} is null ? null : new {destDisplay} {{ {body} }}";
        }

        return null;
    }

    private static string? BuildFlattenedValue(
        Compilation compilation, IEnumerable<IPropertySymbol> sourceProps, IPropertySymbol dest, string accessor)
    {
        var candidates = sourceProps
            .Where(s => s.Type is INamedTypeSymbol &&
                        CollectionElement(s.Type) is null &&
                        dest.Name.Length > s.Name.Length &&
                        dest.Name.StartsWith(s.Name, StringComparison.Ordinal))
            .OrderByDescending(s => s.Name.Length);

        foreach (var outer in candidates)
        {
            var remainder = dest.Name.Substring(outer.Name.Length);
            if (!ReadableProperties(outer.Type).TryGetValue(remainder, out var inner))
            {
                continue;
            }

            var conversion = compilation.ClassifyConversion(inner.Type, dest.Type);
            if (!conversion.IsIdentity && !conversion.IsImplicit)
            {
                continue;
            }

            var outerAccess = $"{accessor}.{outer.Name}";
            return outer.Type.IsReferenceType
                ? $"{outerAccess} == null ? default : {outerAccess}.{remainder}"
                : $"{outerAccess}.{remainder}";
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

    private static bool IsFileLocal(ITypeSymbol? type) =>
        type is INamedTypeSymbol { IsFileLocal: true } ||
        (type is IArrayTypeSymbol array && IsFileLocal(array.ElementType));

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

        foreach (var callSite in callSites)
        {
            if (callSite.UnmappedCsv.Length > 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    UnmappedMember, callSite.Location, callSite.DestShort, callSite.UnmappedCsv));
            }
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

    private sealed record CallSite(
        string ReturnType, string DestShort, string Body, string UnmappedCsv, string AttributeText, Location Location);
}
