using Microsoft.CodeAnalysis;

namespace CupriFace.Binding.Analyzers;

/// <summary>
/// The question the runtime's reflection asks — "does this type have a public instance property
/// of that name?" — asked of Roslyn symbols instead, with one difference that decides everything:
/// the runtime asks the CONCRETE type of the value it is holding, and all a compiler knows is the
/// declared one.
///
/// <para>So every type is either one whose names can be known, or an opaque one, and an opaque type
/// makes every path through it pass. <c>object</c>, an interface, a type parameter or <c>dynamic</c>
/// say nothing about the value behind them; a dictionary, a JSON node or an
/// <c>IBindableShape</c> has its names in its data; a hand-written <c>IBindableAccessor</c> answers
/// whatever its switch says. A declared class is taken at its word, widened by every class in this
/// compilation that derives from it, because a property typed <c>Shape</c> that holds a
/// <c>Circle</c> resolves <c>Radius</c> at runtime and must not be reported for it.</para>
/// </summary>
internal sealed class ModelShapes
{
    private readonly INamedTypeSymbol? _enumerableT;
    private readonly INamedTypeSymbol? _enumerable;
    private readonly INamedTypeSymbol? _accessor;
    private readonly INamedTypeSymbol? _shape;
    private readonly INamedTypeSymbol? _bindableAttr;
    private readonly INamedTypeSymbol? _dynamicProvider;
    private readonly INamedTypeSymbol?[] _dictionaries;
    private readonly Dictionary<ITypeSymbol, List<INamedTypeSymbol>> _descendants;

    public ModelShapes(Compilation compilation, IEnumerable<INamedTypeSymbol> sourceTypes)
    {
        _enumerableT = compilation.GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T);
        _enumerable = compilation.GetSpecialType(SpecialType.System_Collections_IEnumerable);
        _accessor = compilation.GetTypeByMetadataName("CupriFace.Binding.IBindableAccessor");
        _shape = compilation.GetTypeByMetadataName("CupriFace.Binding.IBindableShape");
        _bindableAttr = compilation.GetTypeByMetadataName("CupriFace.Binding.CupriBindableAttribute");
        _dynamicProvider = compilation.GetTypeByMetadataName("System.Dynamic.IDynamicMetaObjectProvider");
        _dictionaries =
        [
            compilation.GetTypeByMetadataName("System.Collections.IDictionary"),
            compilation.GetTypeByMetadataName("System.Collections.Generic.IDictionary`2"),
            compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyDictionary`2"),
        ];

        _descendants = new Dictionary<ITypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
        foreach (var t in sourceTypes)
            for (var b = t.BaseType; b is not null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
            {
                var key = b.OriginalDefinition;
                if (!_descendants.TryGetValue(key, out var list)) _descendants[key] = list = new List<INamedTypeSymbol>();
                list.Add(t);
            }
    }

    /// <summary>True when the names a value of this type answers to cannot be known from the type.</summary>
    public bool IsOpaque(ITypeSymbol type)
    {
        switch (type.TypeKind)
        {
            case TypeKind.Error:
            case TypeKind.Dynamic:
            case TypeKind.TypeParameter:
            case TypeKind.Interface:
            case TypeKind.Pointer:
            case TypeKind.FunctionPointer:
            case TypeKind.Unknown:
                return true;
        }
        if (type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType) return true;

        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns.StartsWith("System.Text.Json", StringComparison.Ordinal)
            || ns.StartsWith("Newtonsoft.Json", StringComparison.Ordinal)
            || ns.StartsWith("System.Dynamic", StringComparison.Ordinal)
            || ns.StartsWith("System.Data", StringComparison.Ordinal))
            return true;

        foreach (var i in type.AllInterfaces)
        {
            var def = i.OriginalDefinition;
            if (Is(def, _shape) || Is(def, _dynamicProvider)) return true;
            foreach (var d in _dictionaries)
                if (Is(def, d)) return true;
            // A generated accessor answers exactly the type's declared properties; a hand-written
            // one answers whatever its switch says, which no symbol can tell us.
            if (Is(def, _accessor) && !HasBindableAttribute(type)) return true;
        }

        // An abstract class is always some other class at runtime. If none is in sight, there is
        // nothing to widen it with, and the names it lacks may well be on the one that is not.
        if (type.IsAbstract && type.TypeKind == TypeKind.Class && Descendants(type).Count == 0) return true;
        return false;
    }

    private bool HasBindableAttribute(ITypeSymbol type) =>
        _bindableAttr is not null && type.GetAttributes().Any(a => Is(a.AttributeClass, _bindableAttr));

    private static bool Is(ITypeSymbol? a, ITypeSymbol? b) =>
        a is not null && b is not null && SymbolEqualityComparer.Default.Equals(a, b);

    private IReadOnlyList<INamedTypeSymbol> Descendants(ITypeSymbol type) =>
        type is INamedTypeSymbol n && _descendants.TryGetValue(n.OriginalDefinition, out var list)
            ? list : Array.Empty<INamedTypeSymbol>();

    /// <summary>The types a value declared as <paramref name="type"/> may really be: itself, the
    /// classes here that derive from it, and for <c>T?</c> the <c>T</c> it boxes to (the runtime
    /// reflects on the boxed value, which is never a Nullable).</summary>
    private IEnumerable<ITypeSymbol> Candidates(ITypeSymbol type)
    {
        yield return type;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n
            && n.TypeArguments.Length == 1)
            yield return n.TypeArguments[0];
        foreach (var d in Descendants(type)) yield return d;
    }

    /// <summary>Public instance non-indexer properties, inherited ones included — exactly what
    /// <c>Type.GetProperty(name, Public | Instance)</c> can find.</summary>
    private static IEnumerable<IPropertySymbol> Properties(ITypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            foreach (var m in t.GetMembers())
                if (m is IPropertySymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public, IsIndexer: false } p)
                    yield return p;
    }

    public IEnumerable<string> PropertyNames(IEnumerable<ITypeSymbol> types) =>
        types.SelectMany(Candidates).SelectMany(Properties).Select(p => p.Name).Distinct(StringComparer.Ordinal);

    /// <summary>
    /// Walk a dotted path across TYPES, the way the doctor does, so a property that exists and holds
    /// null is never mistaken for one that does not exist.
    /// </summary>
    public PathResult Walk(ITypeSymbol root, IReadOnlyList<string> segments)
    {
        IReadOnlyList<ITypeSymbol> current = [root];
        for (var i = 0; i < segments.Count; i++)
        {
            if (current.Any(IsOpaque)) return PathResult.Unknown;
            var next = new List<ITypeSymbol>();
            foreach (var t in current.SelectMany(Candidates))
            {
                if (IsOpaque(t)) return PathResult.Unknown;
                foreach (var p in Properties(t))
                    if (p.Name == segments[i] && !next.Contains(p.Type, SymbolEqualityComparer.Default))
                        next.Add(p.Type);
            }
            if (next.Count == 0) return PathResult.Missing(i, current);
            current = next;
        }
        return PathResult.Found(current);
    }

    /// <summary>
    /// The item type a <c>data-repeat</c> over a value of this type produces, or null when it
    /// cannot be known. The engine repeats over anything that is <c>IEnumerable</c> and not a
    /// string, and hands each item over as an object, so the question is what IEnumerable&lt;T&gt;
    /// says T is — and only when it says so unambiguously.
    /// </summary>
    public ITypeSymbol? ItemType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) return array.ElementType;
        if (type.SpecialType == SpecialType.System_String) return null;
        if (type is INamedTypeSymbol self && Is(self.OriginalDefinition, _enumerableT)) return self.TypeArguments[0];

        ITypeSymbol? found = null;
        foreach (var i in type.AllInterfaces)
            if (Is(i.OriginalDefinition, _enumerableT))
            {
                if (found is not null && !Is(found, i.TypeArguments[0])) return null;   // two Ts: ambiguous
                found = i.TypeArguments[0];
            }
        return found;
    }
}

/// <summary>How a path fared against one starting type.</summary>
internal sealed class PathResult
{
    private PathResult(PathOutcome outcome, int failedAt, IReadOnlyList<ITypeSymbol> types)
    {
        Outcome = outcome;
        FailedAt = failedAt;
        Types = types;
    }

    public PathOutcome Outcome { get; }

    /// <summary>For <see cref="PathOutcome.Missing"/>: the index of the first segment nothing had.</summary>
    public int FailedAt { get; }

    /// <summary>For Found: the types the path ends at. For Missing: the types the failing segment
    /// was looked for on, which is where a suggestion comes from.</summary>
    public IReadOnlyList<ITypeSymbol> Types { get; }

    public static readonly PathResult Unknown = new(PathOutcome.Unknown, -1, Array.Empty<ITypeSymbol>());
    public static PathResult Found(IReadOnlyList<ITypeSymbol> types) => new(PathOutcome.Found, -1, types);
    public static PathResult Missing(int at, IReadOnlyList<ITypeSymbol> types) => new(PathOutcome.Missing, at, types);
}

internal enum PathOutcome { Found, Missing, Unknown }
