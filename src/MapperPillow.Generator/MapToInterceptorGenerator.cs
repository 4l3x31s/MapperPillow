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
/// Replaces each concrete <c>source.MapTo&lt;TDestination&gt;()</c> call (and its
/// <c>Map&lt;TDestination&gt;</c> alias) with a compile-time interceptor — no runtime
/// reflection. Supports scalar objects (parameterless-ctor and constructor-based,
/// e.g. positional records), nested objects, collections/arrays, collection-valued
/// properties, multi-level flattening, enums, and per-member attributes
/// (<c>[MapIgnore]</c>, <c>[MapFrom]</c>).
/// Unmapped destination members are reported as MP0001 warnings. Call sites the
/// generator cannot handle fall back to runtime reflection and are reported as
/// MP0002 warnings, because that fallback is neither trim/Native-AOT safe nor
/// feature-equivalent to the generated code.
/// </summary>
[Generator]
public sealed class MapToInterceptorGenerator : IIncrementalGenerator
{
    private const int MaxNestingDepth = 6;
    private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;
    private static readonly SymbolDisplayFormat Short = SymbolDisplayFormat.MinimallyQualifiedFormat;

    private static readonly DiagnosticDescriptor UnmappedMember = new(
        id: "MP0001",
        title: "Unmapped destination member",
        messageFormat: "{0} leaves destination member(s) unmapped: {1}",
        category: "MapperPillow",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A destination property has no matching source member. Map it, remap it with [MapFrom], or exclude it with [MapIgnore].");

    private static readonly DiagnosticDescriptor NotIntercepted = new(
        id: "MP0002",
        title: "Mapping falls back to runtime reflection",
        messageFormat: "{0} cannot be generated at compile time ({1}); it falls back to runtime reflection, which is not trimming/Native AOT safe and supports neither flattening, [MapFrom], [MapConvert], nor constructor-based destinations",
        category: "MapperPillow",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The reflection fallback only copies public properties matching by name and assignable type, so it can produce a different result than the generated mapper, and a trimmed or Native AOT build may drop the members it needs. Change the call site so it can be generated, or map explicitly.");

    private static readonly DiagnosticDescriptor UntranslatableProjection = new(
        id: "MP0003",
        title: "Projected member is not translated by the query provider",
        messageFormat: "{0} projects member(s) the query provider cannot translate: {1}",
        category: "MapperPillow",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ProjectTo emits a LINQ projection for the database to run, so every member has to be expressible in the provider's query language. These members are valid in an expression tree but not translatable. Measured against EF Core: some throw outright, and some are silently evaluated on the client — which still costs a per-row callback and, either way, means nothing composed after the ProjectTo can filter or order by that member. Map them after materialising with MapTo, or exclude them with [MapIgnore].");

    /// <summary>
    /// Carries planning state that the recursive value planner needs but that differs
    /// between mapping and projection.
    /// </summary>
    /// <remarks>
    /// The distinction exists because a projection is executed by a query provider,
    /// not by .NET. Some expressions are perfectly valid trees that no provider can
    /// translate — running user code (a <c>[MapConvert]</c> converter) or calling
    /// <c>Enum.Parse</c>. Those are emitted anyway, deliberately: dropping them would
    /// silently produce a DTO with missing members, which is worse than a build
    /// warning plus a loud provider error.
    /// </remarks>
    private sealed class PlanContext
    {
        private readonly List<(string Member, string Reason)> _untranslatable = new();

        public PlanContext(bool forProjection) => ForProjection = forProjection;

        public bool ForProjection { get; }

        public IReadOnlyList<(string Member, string Reason)> Untranslatable => _untranslatable;

        /// <summary>Position to attribute later refusals from. See <see cref="AttributeFrom"/>.</summary>
        public int Mark() => _untranslatable.Count;

        /// <summary>Records a refusal whose destination member the caller will supply.</summary>
        public void Record(string reason)
        {
            if (ForProjection)
            {
                _untranslatable.Add((string.Empty, reason));
            }
        }

        /// <summary>
        /// Names every refusal recorded since <paramref name="from"/>. The value planner
        /// is recursive and does not know which destination member it is serving, so the
        /// assignment loop labels them once the value comes back.
        /// </summary>
        public void AttributeFrom(int from, string member)
        {
            for (var i = from; i < _untranslatable.Count; i++)
            {
                if (_untranslatable[i].Member.Length == 0)
                {
                    _untranslatable[i] = (member, _untranslatable[i].Reason);
                }
            }
        }

        public string Describe() => string.Join(", ", _untranslatable
            .Select(u => u.Member.Length == 0 ? u.Reason : $"'{u.Member}' ({u.Reason})")
            .Distinct(StringComparer.Ordinal));
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidateMapToCall(node),
                transform: static (ctx, ct) => GetCallSite(ctx, ct))
            .Where(static c => c is not null)
            .Select(static (c, _) => c!)
            .Collect();

        context.RegisterSourceOutput(candidates, static (spc, items) => Emit(spc, items));
    }

    private static bool IsCandidateMapToCall(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { Identifier.ValueText: "MapTo" or "Map" or "ProjectTo" }
            }
        };

    private static Candidate? GetCallSite(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
        var generic = (GenericNameSyntax)memberAccess.Name;
        if (generic.TypeArgumentList.Arguments.Count != 1)
        {
            return null;
        }

        // Anything that is not MapperPillow's own surface is none of our business:
        // no interceptor, and no diagnostic either.
        if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method ||
            (method.Name != "MapTo" && method.Name != "Map" && method.Name != "ProjectTo") ||
            method.ContainingType?.ToDisplayString() != "MapperPillow.MapperPillowExtensions")
        {
            return null;
        }

        var isProjection = method.Name == "ProjectTo";

        var destType = ctx.SemanticModel.GetTypeInfo(generic.TypeArgumentList.Arguments[0], ct).Type;
        var srcType = ctx.SemanticModel.GetTypeInfo(memberAccess.Expression, ct).Type;

        // Broken code: the compiler is already reporting it. Stay quiet.
        if (destType is null || srcType is null ||
            destType.TypeKind == TypeKind.Error || srcType.TypeKind == TypeKind.Error)
        {
            return null;
        }

        var location = invocation.GetLocation();
        var call = $"{method.Name}<{destType.ToDisplayString(Short)}>";

        if (destType is ITypeParameterSymbol || srcType is ITypeParameterSymbol)
        {
            return Candidate.Skip(call, "the source or destination is an open generic type parameter, so there is no concrete type to generate for", location);
        }

        if (IsFileLocal(destType) || IsFileLocal(srcType))
        {
            return Candidate.Skip(call, "'file'-local types cannot be referenced from the generated file", location);
        }

        var interceptable = ctx.SemanticModel.GetInterceptableLocation(invocation, ct);
        if (interceptable is null)
        {
            return Candidate.Skip(call, "this call site has no interceptable location", location);
        }

        var plan = new PlanContext(isProjection);
        var built = isProjection
            ? BuildProjectionBody(ctx.SemanticModel.Compilation, plan, srcType, destType)
            : BuildBody(ctx.SemanticModel.Compilation, plan, srcType, destType);
        if (built is null)
        {
            return Candidate.Skip(
                call,
                isProjection
                    ? $"no compile-time projection could be built from '{srcType.ToDisplayString(Short)}' to '{destType.ToDisplayString(Short)}'"
                    : $"no compile-time mapping could be built from '{srcType.ToDisplayString(Short)}' to '{destType.ToDisplayString(Short)}'",
                location);
        }

        var returnType = isProjection
            ? $"global::System.Linq.IQueryable<{destType.ToDisplayString(FullyQualified)}>"
            : destType.ToDisplayString(FullyQualified);

        return Candidate.Intercepted(new CallSite(
            returnType,
            isProjection ? "global::System.Linq.IQueryable" : "object",
            call,
            built.Value.Body,
            built.Value.UnmappedCsv,
            plan.Describe(),
            interceptable.GetInterceptsLocationAttributeSyntax(),
            location));
    }

    /// <summary>
    /// Builds the body of a <c>ProjectTo</c> interceptor: a <c>Queryable.Select</c>
    /// whose lambda the C# compiler converts into an expression tree, so the LINQ
    /// provider translates the mapping rather than the mapping running per row.
    /// </summary>
    /// <remarks>
    /// This is where being a compile-time mapper pays off most. A runtime mapper has
    /// to compose the projection as expression-tree objects; here the projection is
    /// ordinary C# that the compiler turns into the tree for free — nothing to build,
    /// nothing to cache, and nothing that needs dynamic code under Native AOT.
    /// </remarks>
    private static (string Body, string UnmappedCsv)? BuildProjectionBody(
        Compilation compilation, PlanContext ctx, ITypeSymbol source, ITypeSymbol destination)
    {
        if (EnumerableElement(source) is not INamedTypeSymbol srcElem ||
            destination is not INamedTypeSymbol destNamed ||
            IsFileLocal(srcElem) || IsFileLocal(destNamed))
        {
            return null;
        }

        var visited = ImmutableHashSet.Create(StringComparer.Ordinal, destNamed.ToDisplayString(FullyQualified));
        var construction = BuildConstruction(compilation, ctx, srcElem, destNamed, "src", visited);
        if (construction is null)
        {
            return null;
        }

        var srcDisplay = srcElem.ToDisplayString(FullyQualified);

        var sb = new StringBuilder();
        sb.AppendLine("            if (source is null) throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine($"            var typed = (global::System.Linq.IQueryable<{srcDisplay}>)source;");
        sb.AppendLine($"            var result = global::System.Linq.Queryable.Select(typed, src => {construction.Value.Expr});");
        sb.AppendLine("            global::MapperPillow.MapperPillowTelemetry.MarkIntercepted();");
        sb.Append("            return result;");

        return (sb.ToString(), UnmappedFrom(destNamed, construction.Value.Mapped));
    }

    private static (string Body, string UnmappedCsv)? BuildBody(Compilation compilation, PlanContext ctx, ITypeSymbol source, ITypeSymbol destination)
    {
        var destElement = CollectionElement(destination);
        if (destElement is not null)
        {
            if (EnumerableElement(source) is not INamedTypeSymbol srcElem ||
                destElement is not INamedTypeSymbol destElem ||
                IsFileLocal(destElem) || IsFileLocal(srcElem))
            {
                return null;
            }

            var elemVisited = ImmutableHashSet.Create(StringComparer.Ordinal, destElem.ToDisplayString(FullyQualified));
            var built = BuildConstruction(compilation, ctx, srcElem, destElem, "item", elemVisited);
            if (built is null)
            {
                return null;
            }

            return (
                BuildCollectionBody(destination, destElem.ToDisplayString(FullyQualified), srcElem.ToDisplayString(FullyQualified), built.Value.Expr),
                UnmappedFrom(destElem, built.Value.Mapped));
        }

        if (source is not INamedTypeSymbol || destination is not INamedTypeSymbol destNamed)
        {
            return null;
        }

        var visited = ImmutableHashSet.Create(StringComparer.Ordinal, destNamed.ToDisplayString(FullyQualified));
        var construction = BuildConstruction(compilation, ctx, source, destNamed, "typed", visited);
        if (construction is null)
        {
            return null;
        }

        return (
            BuildScalarBody(source.ToDisplayString(FullyQualified), construction.Value.Expr),
            UnmappedFrom(destNamed, construction.Value.Mapped));
    }

    private static string BuildScalarBody(string src, string constructionExpr)
    {
        var sb = new StringBuilder();
        sb.AppendLine("            if (source is null) throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine($"            var typed = ({src})source;");
        sb.AppendLine($"            var result = {constructionExpr};");
        sb.AppendLine("            global::MapperPillow.MapperPillowTelemetry.MarkIntercepted();");
        sb.Append("            return result;");
        return sb.ToString();
    }

    private static string BuildCollectionBody(ITypeSymbol destination, string destElem, string srcElem, string elementConstructionExpr)
    {
        var toArray = destination is IArrayTypeSymbol;

        var sb = new StringBuilder();
        sb.AppendLine("            if (source is null) throw new global::System.ArgumentNullException(nameof(source));");
        sb.AppendLine($"            var typed = (global::System.Collections.Generic.IEnumerable<{srcElem}>)source;");
        sb.AppendLine($"            var result = new global::System.Collections.Generic.List<{destElem}>();");
        sb.AppendLine("            foreach (var item in typed)");
        sb.AppendLine("            {");
        sb.AppendLine($"                result.Add({elementConstructionExpr});");
        sb.AppendLine("            }");
        sb.AppendLine("            global::MapperPillow.MapperPillowTelemetry.MarkIntercepted();");
        sb.Append(toArray
            ? "            return global::System.Linq.Enumerable.ToArray(result);"
            : "            return result;");
        return sb.ToString();
    }

    /// <summary>
    /// Builds the <c>new Destination(...) { ... }</c> expression that maps
    /// <paramref name="destination"/> from <paramref name="accessor"/>, choosing an
    /// object initializer when there is a parameterless constructor or a constructor
    /// call (e.g. positional records) otherwise. Returns the expression plus the set
    /// of destination member names it covers, or null if it cannot be constructed.
    /// </summary>
    private static (string Expr, ImmutableHashSet<string> Mapped)? BuildConstruction(
        Compilation compilation, PlanContext ctx, ITypeSymbol source, INamedTypeSymbol destination, string accessor, ImmutableHashSet<string> visited)
    {
        var display = destination.ToDisplayString(FullyQualified);
        var pairs = BuildAssignmentPairs(compilation, ctx, source, destination, accessor, visited);

        if (HasParameterlessCtor(destination))
        {
            var expr = pairs.Count == 0
                ? $"new {display}()"
                : $"new {display} {{ {string.Join(", ", pairs.Select(p => $"{p.Name} = {p.Expr}"))} }}";
            return (expr, pairs.Select(p => p.Name).ToImmutableHashSet(StringComparer.Ordinal));
        }

        var ctor = ChooseConstructor(compilation, ctx, source, destination, accessor, visited);
        if (ctor is null)
        {
            return null;
        }

        var remaining = pairs.Where(p => !ctor.Value.Consumed.Contains(p.Name)).ToList();
        var init = remaining.Count == 0
            ? string.Empty
            : $" {{ {string.Join(", ", remaining.Select(p => $"{p.Name} = {p.Expr}"))} }}";
        var mapped = pairs.Select(p => p.Name).Concat(ctor.Value.Consumed).ToImmutableHashSet(StringComparer.Ordinal);
        return ($"new {display}({string.Join(", ", ctor.Value.Args)}){init}", mapped);
    }

    /// <summary>
    /// Picks the public constructor with the most parameters that can all be filled
    /// from the source (by name, case-insensitive), and returns the argument
    /// expressions plus the destination members those parameters cover.
    /// </summary>
    private static (List<string> Args, ImmutableHashSet<string> Consumed)? ChooseConstructor(
        Compilation compilation, PlanContext ctx, ITypeSymbol source, INamedTypeSymbol destination, string accessor, ImmutableHashSet<string> visited)
    {
        var sourceByName = new Dictionary<string, IPropertySymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in ReadableProperties(source).Values)
        {
            sourceByName[p.Name] = p;
        }

        var destByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in SettableProperties(destination))
        {
            destByName[p.Name] = p.Name;
        }

        (List<string> Args, ImmutableHashSet<string> Consumed)? best = null;
        var bestCount = 0;

        foreach (var ctor in destination.InstanceConstructors)
        {
            if (ctor.DeclaredAccessibility != Accessibility.Public || ctor.Parameters.Length == 0)
            {
                continue;
            }

            var args = new List<string>();
            var consumed = new List<string>();
            var usable = true;

            foreach (var param in ctor.Parameters)
            {
                if (!sourceByName.TryGetValue(param.Name, out var srcProp))
                {
                    usable = false;
                    break;
                }

                var value = BuildValue(compilation, ctx, srcProp.Type, param.Type, $"{accessor}.{srcProp.Name}", visited);
                if (value is null)
                {
                    usable = false;
                    break;
                }

                args.Add(value);
                consumed.Add(destByName.TryGetValue(param.Name, out var destName) ? destName : param.Name);
            }

            if (usable && ctor.Parameters.Length > bestCount)
            {
                best = (args, consumed.ToImmutableHashSet(StringComparer.Ordinal));
                bestCount = ctor.Parameters.Length;
            }
        }

        return best;
    }

    private static List<(string Name, string Expr)> BuildAssignmentPairs(
        Compilation compilation, PlanContext ctx, ITypeSymbol source, ITypeSymbol destination, string accessor, ImmutableHashSet<string> visited)
    {
        var sourceProps = ReadableProperties(source);
        var pairs = new List<(string Name, string Expr)>();

        foreach (var dest in SettableProperties(destination))
        {
            if (IsIgnored(dest))
            {
                continue;
            }

            var mark = ctx.Mark();

            string? value;
            var converter = MapConverter(dest);
            var mapFrom = MapFromPath(dest);
            if (converter is not null)
            {
                value = BuildConverterValue(compilation, ctx, source, dest, accessor, converter);
            }
            else if (mapFrom is not null)
            {
                value = BuildMapFromExpr(
                    compilation, ctx, source, accessor, mapFrom.Split('.'), 0, dest.Type,
                    dest.Type.ToDisplayString(FullyQualified), visited);
            }
            else
            {
                value = sourceProps.TryGetValue(dest.Name, out var src)
                    ? BuildValue(compilation, ctx, src.Type, dest.Type, $"{accessor}.{dest.Name}", visited)
                    : null;
                value ??= BuildFlattenedValue(compilation, source, dest, accessor);
            }

            if (value is not null)
            {
                pairs.Add((dest.Name, value));
            }

            // The value planner is recursive and does not know which destination
            // member it served, so name any refusals it recorded now that we do.
            ctx.AttributeFrom(mark, dest.Name);
        }

        return pairs;
    }

    private static bool IsIgnored(IPropertySymbol property) =>
        property.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == "MapperPillow.MapIgnoreAttribute");

    private static string? MapFromPath(IPropertySymbol property)
    {
        var attribute = property.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "MapperPillow.MapFromAttribute");

        if (attribute is null || attribute.ConstructorArguments.Length != 1)
        {
            return null;
        }

        var path = attribute.ConstructorArguments[0].Value as string;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static INamedTypeSymbol? MapConverter(IPropertySymbol property)
    {
        var attribute = property.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "MapperPillow.MapConvertAttribute");

        if (attribute is null || attribute.ConstructorArguments.Length != 1)
        {
            return null;
        }

        return attribute.ConstructorArguments[0].Value as INamedTypeSymbol;
    }

    /// <summary>Emits <c>new Converter().Convert(source.Member)</c> for <c>[MapConvert]</c>.</summary>
    private static string? BuildConverterValue(
        Compilation compilation, PlanContext ctx, ITypeSymbol source, IPropertySymbol dest, string accessor, INamedTypeSymbol converter)
    {
        if (converter.IsFileLocal || !HasParameterlessCtor(converter))
        {
            return null;
        }

        var contract = converter.AllInterfaces.FirstOrDefault(i =>
            i.Name == "IValueConverter" &&
            i.ContainingNamespace?.ToDisplayString() == "MapperPillow" &&
            i.TypeArguments.Length == 2);
        if (contract is null)
        {
            return null;
        }

        if (!ReadableProperties(source).TryGetValue(dest.Name, out var srcProp))
        {
            return null;
        }

        var toConverter = compilation.ClassifyConversion(srcProp.Type, contract.TypeArguments[0]);
        var toDestination = compilation.ClassifyConversion(contract.TypeArguments[1], dest.Type);
        if ((!toConverter.IsIdentity && !toConverter.IsImplicit) ||
            (!toDestination.IsIdentity && !toDestination.IsImplicit))
        {
            return null;
        }

        // Measured against EF Core: this does not throw — the provider silently
        // evaluates it on the client. The cost is that the database never computes the
        // member, so nothing composed after the ProjectTo can filter or order by it.
        ctx.Record("a [MapConvert] converter is evaluated on the client, so the database never computes the member");

        return $"new {converter.ToDisplayString(FullyQualified)}().Convert({accessor}.{srcProp.Name})";
    }

    /// <summary>Resolves an explicit dot-separated source path for <c>[MapFrom]</c>.</summary>
    private static string? BuildMapFromExpr(
        Compilation compilation, PlanContext ctx, ITypeSymbol type, string accessor, string[] segments, int index,
        ITypeSymbol targetType, string targetDisplay, ImmutableHashSet<string> visited)
    {
        if (index >= segments.Length ||
            !ReadableProperties(type).TryGetValue(segments[index].Trim(), out var property))
        {
            return null;
        }

        var access = $"{accessor}.{property.Name}";

        if (index == segments.Length - 1)
        {
            return BuildValue(compilation, ctx, property.Type, targetType, access, visited);
        }

        var sub = BuildMapFromExpr(compilation, ctx, property.Type, access, segments, index + 1, targetType, targetDisplay, visited);
        if (sub is null)
        {
            return null;
        }

        return property.Type.IsReferenceType
            ? $"{access} == null ? default({targetDisplay}) : ({sub})"
            : sub;
    }

    /// <summary>The right-hand side that produces a destination value, or null if unmappable.</summary>
    private static string? BuildValue(
        Compilation compilation, PlanContext ctx, ITypeSymbol source, ITypeSymbol destination, string accessor, ImmutableHashSet<string> visited)
    {
        var conversion = compilation.ClassifyConversion(source, destination);
        if (conversion.IsIdentity || conversion.IsImplicit)
        {
            return accessor;
        }

        // Nullable value source -> non-nullable value destination: unwrap with default.
        if (source is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableSource &&
            destination.IsValueType &&
            destination is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
        {
            var underlying = nullableSource.TypeArguments[0];
            var unwrapped = compilation.ClassifyConversion(underlying, destination);
            if (unwrapped.IsIdentity || unwrapped.IsImplicit)
            {
                return $"{accessor}.GetValueOrDefault()";
            }
        }

        // Enums: enum <-> enum (by value), enum <-> integral, enum -> string, string -> enum.
        if (source.TypeKind == TypeKind.Enum || destination.TypeKind == TypeKind.Enum)
        {
            var destDisplay = destination.ToDisplayString(FullyQualified);

            if (source.TypeKind == TypeKind.Enum && destination.TypeKind == TypeKind.Enum)
            {
                return $"({destDisplay}){accessor}";
            }

            if (source.TypeKind == TypeKind.Enum && destination.SpecialType == SpecialType.System_String)
            {
                return $"{accessor}.ToString()";
            }

            if (source.SpecialType == SpecialType.System_String && destination.TypeKind == TypeKind.Enum)
            {
                // Unlike a converter, this one throws outright — EF Core will not even
                // client-evaluate it. The reverse direction (enum -> string) emits
                // ToString(), which providers do translate, so it is not flagged. Both
                // measured in MapperPillow.EfCore.Tests.
                ctx.Record("string to enum uses Enum.Parse, which the provider rejects outright");

                return $"({destDisplay})global::System.Enum.Parse(typeof({destDisplay}), {accessor})";
            }

            if ((source.TypeKind == TypeKind.Enum && IsIntegral(destination)) ||
                (IsIntegral(source) && destination.TypeKind == TypeKind.Enum))
            {
                return $"({destDisplay}){accessor}";
            }

            return null;
        }

        // Collection-valued property: map each element with LINQ (null -> null).
        var destCollElem = CollectionElement(destination);
        if (destCollElem is not null)
        {
            if (EnumerableElement(source) is not INamedTypeSymbol srcElem ||
                destCollElem is not INamedTypeSymbol destElem ||
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
            var built = BuildConstruction(compilation, ctx, srcElem, destElem, lambda, childVisited);
            if (built is null)
            {
                return null;
            }

            var projected = $"global::System.Linq.Enumerable.Select({accessor}, {lambda} => {built.Value.Expr})";
            var materialized = destination is IArrayTypeSymbol
                ? $"global::System.Linq.Enumerable.ToArray({projected})"
                : $"global::System.Linq.Enumerable.ToList({projected})";
            return $"{accessor} == null ? null : {materialized}";
        }

        // Nested complex object: construct it recursively, guarding null and cycles.
        if (source is INamedTypeSymbol &&
            destination is INamedTypeSymbol destNamed &&
            destination.IsReferenceType &&
            !destNamed.IsFileLocal &&
            EnumerableElement(source) is null)
        {
            var destDisplay = destination.ToDisplayString(FullyQualified);
            if (visited.Contains(destDisplay) || visited.Count > MaxNestingDepth)
            {
                return null;
            }

            var built = BuildConstruction(compilation, ctx, source, destNamed, accessor, visited.Add(destDisplay));
            if (built is null)
            {
                return null;
            }

            // `is null` is the safer null test — it cannot be changed by an overloaded
            // operator== — but an expression tree may not contain a pattern (CS8122),
            // so a projection has to use `== null`. Only nested objects were affected;
            // every other null guard already emits `== null`.
            return ctx.ForProjection
                ? $"{accessor} == null ? null : {built.Value.Expr}"
                : $"{accessor} is null ? null : {built.Value.Expr}";
        }

        return null;
    }

    private static string? BuildFlattenedValue(
        Compilation compilation, ITypeSymbol source, IPropertySymbol dest, string accessor)
    {
        var targetDisplay = dest.Type.ToDisplayString(FullyQualified);
        return FlattenExpr(compilation, source, accessor, dest.Name, dest.Type, targetDisplay, depth: 0);
    }

    /// <summary>
    /// Resolves <paramref name="name"/> against <paramref name="type"/> by walking a
    /// nested member path (e.g. <c>CustomerAddressCity</c> → <c>Customer.Address.City</c>),
    /// null-guarding each object hop. Longest matching prefix wins at every level.
    /// </summary>
    private static string? FlattenExpr(
        Compilation compilation, ITypeSymbol type, string accessor, string name,
        ITypeSymbol targetType, string targetDisplay, int depth)
    {
        if (depth > MaxNestingDepth)
        {
            return null;
        }

        var props = ReadableProperties(type);

        if (props.TryGetValue(name, out var direct))
        {
            var conversion = compilation.ClassifyConversion(direct.Type, targetType);
            if (conversion.IsIdentity || conversion.IsImplicit)
            {
                return $"{accessor}.{name}";
            }
        }

        var candidates = props.Values
            .Where(s => s.Type is INamedTypeSymbol &&
                        CollectionElement(s.Type) is null &&
                        name.Length > s.Name.Length &&
                        name.StartsWith(s.Name, StringComparison.Ordinal))
            .OrderByDescending(s => s.Name.Length);

        foreach (var outer in candidates)
        {
            var remainder = name.Substring(outer.Name.Length);
            var sub = FlattenExpr(compilation, outer.Type, $"{accessor}.{outer.Name}", remainder, targetType, targetDisplay, depth + 1);
            if (sub is not null)
            {
                return outer.Type.IsReferenceType
                    ? $"{accessor}.{outer.Name} == null ? default({targetDisplay}) : ({sub})"
                    : sub;
            }
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

    private static bool IsIntegral(ITypeSymbol type) => type.SpecialType is
        SpecialType.System_SByte or SpecialType.System_Byte or
        SpecialType.System_Int16 or SpecialType.System_UInt16 or
        SpecialType.System_Int32 or SpecialType.System_UInt32 or
        SpecialType.System_Int64 or SpecialType.System_UInt64;

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

    private static string UnmappedFrom(ITypeSymbol destination, ImmutableHashSet<string> mapped)
    {
        var unmapped = SettableProperties(destination)
            .Where(p => !IsIgnored(p))
            .Select(p => p.Name)
            .Where(n => !mapped.Contains(n))
            .Distinct(StringComparer.Ordinal)
            .Select(n => $"'{n}'");
        return string.Join(", ", unmapped);
    }

    // --- emission ------------------------------------------------------------

    private static void Emit(SourceProductionContext spc, ImmutableArray<Candidate> candidates)
    {
        if (candidates.IsDefaultOrEmpty)
        {
            return;
        }

        // Every call site we could not generate silently degrades to reflection —
        // say so at compile time instead of letting it surface in production.
        foreach (var skipped in candidates.Select(c => c.Skipped).Where(s => s is not null))
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                NotIntercepted, skipped!.Location, skipped.Call, skipped.Reason));
        }

        var callSites = candidates.Select(c => c.Site).Where(s => s is not null).Select(s => s!).ToArray();
        if (callSites.Length == 0)
        {
            return;
        }

        foreach (var callSite in callSites)
        {
            if (callSite.UnmappedCsv.Length > 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    UnmappedMember, callSite.Location, callSite.Call, callSite.UnmappedCsv));
            }

            if (callSite.UntranslatableCsv.Length > 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    UntranslatableProjection, callSite.Location, callSite.Call, callSite.UntranslatableCsv));
            }
        }

        var groups = callSites
            .GroupBy(c => (c.ReturnType, c.SourceParameter, c.Body))
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

            sb.AppendLine($"        public static {group.Key.ReturnType} Map_{index}(this {group.Key.SourceParameter} source)");
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

    /// <param name="SourceParameter">
    /// The interceptor's parameter type. It must match the intercepted method's own
    /// signature: <c>object</c> for MapTo/Map, <c>IQueryable</c> for ProjectTo.
    /// </param>
    /// <param name="UntranslatableCsv">
    /// Projected members no query provider can translate, empty for a plain mapping.
    /// </param>
    private sealed record CallSite(
        string ReturnType, string SourceParameter, string Call, string Body, string UnmappedCsv,
        string UntranslatableCsv, string AttributeText, Location Location);

    /// <summary>A call site that could not be generated, and why.</summary>
    private sealed record SkippedCallSite(string Call, string Reason, Location Location);

    /// <summary>
    /// One MapperPillow call site: either generated (<see cref="Site"/>) or left to
    /// the reflection fallback (<see cref="Skipped"/>). Exactly one is non-null.
    /// </summary>
    private sealed record Candidate(CallSite? Site, SkippedCallSite? Skipped)
    {
        public static Candidate Intercepted(CallSite site) => new(site, null);

        public static Candidate Skip(string call, string reason, Location location) =>
            new(null, new SkippedCallSite(call, reason, location));
    }
}
