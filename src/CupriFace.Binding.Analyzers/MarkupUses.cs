using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CupriFace.Binding.Analyzers;

/// <summary>
/// Markup the compilation can see, and the model type it is bound to: an asset named by its
/// logical name ("Assets/App.html"), or markup written in the C# itself.
/// </summary>
internal sealed class MarkupUse(string? asset, string? inline, Location? inlineLocation, SyntaxToken literal,
                                ITypeSymbol model)
{
    /// <summary>The embedded asset's logical name, when the markup is a file.</summary>
    public string? Asset { get; } = asset;

    /// <summary>The markup itself, when it is a constant in the C#.</summary>
    public string? Inline { get; } = inline;

    /// <summary>Where an inline finding goes when it cannot be placed inside a single literal.</summary>
    public Location? InlineLocation { get; } = inlineLocation;

    /// <summary>The one string literal the inline markup came from, when there was exactly one.</summary>
    public SyntaxToken Literal { get; } = literal;

    public ITypeSymbol Model { get; } = model;
}

/// <summary>
/// Finds every <see cref="MarkupUse"/> in a compilation — once, on first need, shared by every
/// file the analyzer is then asked about.
///
/// <para>Three ways in, the most explicit last:</para>
/// <list type="bullet">
/// <item>A <c>CupriApp</c> subclass: what its <c>Html</c> (or failing that <c>MarkupSource</c>)
/// returns, bound to the static type of what its <c>Model</c> returns.</item>
/// <item><c>CupriDocument.Load(markup, …)</c> whose document is then <c>.Bind(model)</c>,
/// chained or through one local that nothing reassigns.</item>
/// <item><c>[CupriMarkup("Assets/App.html")]</c> on the model class, or
/// <c>[assembly: CupriMarkup("Assets/App.html", typeof(Model))]</c>, for anything else.</item>
/// </list>
/// <para>Everything inference cannot settle — a markup string computed at runtime, a model typed
/// <c>object</c> all the way down — yields nothing, never a guess.</para>
/// </summary>
internal static class MarkupUses
{
    private const int MaxDepth = 6;

    public static (List<MarkupUse> Uses, List<INamedTypeSymbol> SourceTypes) Find(Compilation compilation, CancellationToken ct)
    {
        var uses = new List<MarkupUse>();
        var types = new List<INamedTypeSymbol>();
        Collect(compilation.Assembly.GlobalNamespace, types);

        var app = compilation.GetTypeByMetadataName("CupriFace.CupriApp");
        var attr = compilation.GetTypeByMetadataName("CupriFace.Binding.CupriMarkupAttribute");
        var source = compilation.GetTypeByMetadataName("CupriFace.Resources.CupriSource");
        var document = compilation.GetTypeByMetadataName("CupriFace.CupriDocument");
        if (app is null && attr is null) return (uses, types);
        var ctx = new Context(compilation, app, source, document);

        foreach (var t in types)
        {
            ct.ThrowIfCancellationRequested();
            if (app is not null && t.TypeKind == TypeKind.Class && !t.IsAbstract && DerivesFrom(t, app))
                FromApp(t, ctx, uses);
            if (attr is not null)
                foreach (var a in t.GetAttributes())
                    if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, attr)
                        && a.ConstructorArguments.Length >= 1 && a.ConstructorArguments[0].Value is string asset)
                        Add(uses, new MarkupUse(asset, null, null, default, t));
        }

        if (attr is not null)
            foreach (var a in compilation.Assembly.GetAttributes())
                if (SymbolEqualityComparer.Default.Equals(a.AttributeClass, attr)
                    && a.ConstructorArguments.Length == 2
                    && a.ConstructorArguments[0].Value is string asset
                    && a.ConstructorArguments[1].Value is ITypeSymbol model)
                    Add(uses, new MarkupUse(asset, null, null, default, model));

        if (document is not null) FromLoadAndBind(ctx, uses, ct);
        return (uses, types);
    }

    private sealed class Context(Compilation compilation, INamedTypeSymbol? app, INamedTypeSymbol? source,
                                 INamedTypeSymbol? document)
    {
        public Compilation Compilation { get; } = compilation;
        public INamedTypeSymbol? App { get; } = app;
        public INamedTypeSymbol? Source { get; } = source;
        public INamedTypeSymbol? Document { get; } = document;
        private readonly Dictionary<SyntaxTree, SemanticModel> _models = new();

        public SemanticModel Model(SyntaxTree tree)
        {
            if (!_models.TryGetValue(tree, out var m))
                // Markup and model are usually declared in different places — the Html property in
                // one file, the Assets accessor in a generated one — so the analysis has to reach
                // across trees, which only the compilation can do.
#pragma warning disable RS1030
                _models[tree] = m = Compilation.GetSemanticModel(tree);
#pragma warning restore RS1030
            return m;
        }
    }

    private static void Add(List<MarkupUse> uses, MarkupUse use)
    {
        foreach (var u in uses)
            if (SymbolEqualityComparer.Default.Equals(u.Model, use.Model)
                && u.Asset == use.Asset && u.Inline == use.Inline
                && Equals(u.InlineLocation, use.InlineLocation))
                return;
        uses.Add(use);
    }

    private static void Collect(INamespaceSymbol ns, List<INamedTypeSymbol> into)
    {
        foreach (var t in ns.GetTypeMembers()) CollectType(t, into);
        foreach (var child in ns.GetNamespaceMembers()) Collect(child, into);
    }

    private static void CollectType(INamedTypeSymbol t, List<INamedTypeSymbol> into)
    {
        into.Add(t);
        foreach (var nested in t.GetTypeMembers()) CollectType(nested, into);
    }

    private static bool DerivesFrom(INamedTypeSymbol t, INamedTypeSymbol baseType)
    {
        for (var b = t.BaseType; b is not null; b = b.BaseType)
            if (SymbolEqualityComparer.Default.Equals(b, baseType)) return true;
        return false;
    }

    // ---- CupriApp ------------------------------------------------------------------------------

    private static void FromApp(INamedTypeSymbol type, Context ctx, List<MarkupUse> uses)
    {
        // The override the runtime calls is the most-derived one. Html is what the host reads, and
        // only CupriApp's own Html falls back to MarkupSource — so an Html override anywhere in the
        // chain wins over a MarkupSource override, even one declared further down.
        var html = MostDerived(type, "Html", ctx.App!);
        var markupExpr = html is not null ? Body(html) : MostDerived(type, "MarkupSource", ctx.App!) is { } ms ? Body(ms) : null;
        var modelProp = MostDerived(type, "Model", ctx.App!);
        if (markupExpr is null || modelProp is null || Body(modelProp) is not { } modelExpr) return;

        var model = StaticType(modelExpr, ctx);
        if (model is null) return;
        if (Markup(markupExpr, ctx, 0) is { } m)
            Add(uses, new MarkupUse(m.Asset, m.Inline, m.Location, m.Literal, model));
    }

    private static IPropertySymbol? MostDerived(INamedTypeSymbol type, string name, INamedTypeSymbol stopAt)
    {
        for (var t = type; t is not null && !SymbolEqualityComparer.Default.Equals(t, stopAt); t = t.BaseType)
            foreach (var m in t.GetMembers(name))
                if (m is IPropertySymbol { IsOverride: true } p) return p;
        return null;
    }

    /// <summary>The expression a property returns: an expression body, a getter that is one, a
    /// getter that is a single <c>return</c>, or an auto-property's initializer.</summary>
    private static ExpressionSyntax? Body(IPropertySymbol property)
    {
        if (property.DeclaringSyntaxReferences.Length != 1) return null;
        if (property.DeclaringSyntaxReferences[0].GetSyntax() is not PropertyDeclarationSyntax decl) return null;
        if (decl.ExpressionBody is { } eb) return eb.Expression;
        if (decl.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) is { } get)
        {
            if (get.ExpressionBody is { } geb) return geb.Expression;
            if (get.Body is { Statements.Count: 1 } body && body.Statements[0] is ReturnStatementSyntax { Expression: { } r })
                return r;
            if (get.Body is null && decl.Initializer is { } init) return init.Value;
        }
        return null;
    }

    private static ITypeSymbol? StaticType(ExpressionSyntax expr, Context ctx)
    {
        var info = ctx.Model(expr.SyntaxTree).GetTypeInfo(expr);
        return info.Type is { TypeKind: not TypeKind.Error } t ? t : null;
    }

    // ---- what the markup expression is ---------------------------------------------------------

    private sealed class MarkupRef
    {
        public string? Asset;
        public string? Inline;
        public Location? Location;
        public SyntaxToken Literal;
    }

    private static MarkupRef? Markup(ExpressionSyntax expr, Context ctx, int depth)
    {
        if (depth > MaxDepth) return null;
        var sm = ctx.Model(expr.SyntaxTree);

        // A constant: the markup is right here (or in the const it names).
        var constant = sm.GetConstantValue(expr);
        if (constant is { HasValue: true, Value: string text })
            return new MarkupRef { Inline = text, Location = expr.GetLocation(), Literal = SingleLiteral(expr, sm) };

        expr = Unwrap(expr);
        var symbol = sm.GetSymbolInfo(expr).Symbol;

        if (expr is InvocationExpressionSyntax inv && symbol is IMethodSymbol method)
        {
            var args = inv.ArgumentList.Arguments;
            string? Const(int i) => i < args.Count && sm.GetConstantValue(args[i].Expression) is { HasValue: true, Value: string s } ? s : null;

            // x.ReadText(): the text of whatever source x is.
            if (method.Name == "ReadText" && IsSource(method.ContainingType, ctx)
                && inv.Expression is MemberAccessExpressionSyntax ma)
                return Markup(ma.Expression, ctx, depth + 1);

            if (IsSource(method.ContainingType, ctx))
            {
                // CupriSource.Embedded(asm, "Assets/X.html") / Embedded<T>("Assets/X.html") / Text("…")
                if (method.Name == "Embedded")
                    return (method.IsGenericMethod ? Const(0) : Const(1)) is { } name ? new MarkupRef { Asset = name } : null;
                if (method.Name == "Text" && Const(0) is { } inline)
                    return new MarkupRef { Inline = inline, Location = args[0].GetLocation(), Literal = SingleLiteral(args[0].Expression, sm) };
                return null;
            }

            // CupriApp.EmbeddedAsset("Assets/X.html")
            if (method.Name == "EmbeddedAsset" && ctx.App is not null
                && SymbolEqualityComparer.Default.Equals(method.ContainingType, ctx.App))
                return Const(0) is { } name ? new MarkupRef { Asset = name } : null;
            return null;
        }

        // A property or static field holding one of the above — Assets.X.Html is exactly this: a
        // generated property whose body is CupriSource.Embedded(…, "Assets/X.html").
        if (symbol is IPropertySymbol prop && Body(prop) is { } pb) return Markup(pb, ctx, depth + 1);
        if (symbol is IFieldSymbol { IsReadOnly: true } field
            && field.DeclaringSyntaxReferences.Length == 1
            && field.DeclaringSyntaxReferences[0].GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: { } fv })
            return Markup(fv, ctx, depth + 1);
        return null;
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax e)
    {
        while (true)
            switch (e)
            {
                case ParenthesizedExpressionSyntax p: e = p.Expression; break;
                case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } s: e = s.Operand; break;
                default: return e;
            }
    }

    private static bool IsSource(INamedTypeSymbol? t, Context ctx) =>
        ctx.Source is not null && SymbolEqualityComparer.Default.Equals(t, ctx.Source);

    /// <summary>The one literal a constant came from — directly, or through a <c>const</c> — so a
    /// finding can be placed on the exact characters rather than on the whole expression.</summary>
    private static SyntaxToken SingleLiteral(ExpressionSyntax expr, SemanticModel sm)
    {
        expr = Unwrap(expr);
        if (expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)) return lit.Token;
        if (sm.GetSymbolInfo(expr).Symbol is IFieldSymbol { IsConst: true } f
            && f.DeclaringSyntaxReferences.Length == 1
            && f.DeclaringSyntaxReferences[0].GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: LiteralExpressionSyntax l }
            && l.IsKind(SyntaxKind.StringLiteralExpression))
            return l.Token;
        if (sm.GetSymbolInfo(expr).Symbol is ILocalSymbol { IsConst: true } local
            && local.DeclaringSyntaxReferences.Length == 1
            && local.DeclaringSyntaxReferences[0].GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: LiteralExpressionSyntax ll }
            && ll.IsKind(SyntaxKind.StringLiteralExpression))
            return ll.Token;
        return default;
    }

    // ---- CupriDocument.Load(...).Bind(model) -----------------------------------------------------

    private static void FromLoadAndBind(Context ctx, List<MarkupUse> uses, CancellationToken ct)
    {
        foreach (var tree in ctx.Compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            // Cheap text filter first: most files never bind a document.
            if (!tree.TryGetText(out var text)) text = tree.GetText(ct);
            if (text.ToString().IndexOf(".Bind(", StringComparison.Ordinal) < 0) continue;

            var root = tree.GetRoot(ct);
            SemanticModel? sm = null;
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Bind" } bind) continue;
                if (inv.ArgumentList.Arguments.Count != 1) continue;
                sm ??= ctx.Model(tree);
                if (sm.GetSymbolInfo(inv).Symbol is not IMethodSymbol m
                    || !SymbolEqualityComparer.Default.Equals(m.ContainingType, ctx.Document)) continue;

                var load = LoadCall(Unwrap(bind.Expression), sm, ctx);
                if (load is null || load.ArgumentList.Arguments.Count == 0) continue;
                var model = StaticType(inv.ArgumentList.Arguments[0].Expression, ctx);
                if (model is null) continue;
                if (Markup(load.ArgumentList.Arguments[0].Expression, ctx, 0) is { } mk)
                    Add(uses, new MarkupUse(mk.Asset, mk.Inline, mk.Location, mk.Literal, model));
            }
        }
    }

    /// <summary>The <c>CupriDocument.Load(…)</c> a Bind is called on: directly, or through a local
    /// initialised by it and never assigned again — any reassignment and the markup is no longer
    /// knowable from here, so nothing is claimed.</summary>
    private static InvocationExpressionSyntax? LoadCall(ExpressionSyntax receiver, SemanticModel sm, Context ctx)
    {
        // Load(…).UseComponents(…).Bind(m): the document's fluent members hand back the same
        // document, so step through any instance call on it that returns one.
        while (receiver is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax step } fluent
               && sm.GetSymbolInfo(fluent).Symbol is IMethodSymbol { IsStatic: false } sm2
               && SymbolEqualityComparer.Default.Equals(sm2.ContainingType, ctx.Document)
               && SymbolEqualityComparer.Default.Equals(sm2.ReturnType, ctx.Document))
            receiver = Unwrap(step.Expression);

        if (receiver is InvocationExpressionSyntax direct) return IsLoad(direct, sm, ctx) ? direct : null;
        if (sm.GetSymbolInfo(receiver).Symbol is not ILocalSymbol local) return null;
        if (local.DeclaringSyntaxReferences.Length != 1
            || local.DeclaringSyntaxReferences[0].GetSyntax() is not VariableDeclaratorSyntax { Initializer.Value: { } init } decl)
            return null;
        if (Unwrap(init) is not InvocationExpressionSyntax call || !IsLoad(call, sm, ctx)) return null;

        var scope = decl.FirstAncestorOrSelf<BlockSyntax>() ?? (SyntaxNode?)decl.FirstAncestorOrSelf<CompilationUnitSyntax>();
        if (scope is null) return null;
        foreach (var assign in scope.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            if (assign.Left is IdentifierNameSyntax id && id.Identifier.ValueText == local.Name
                && SymbolEqualityComparer.Default.Equals(sm.GetSymbolInfo(id).Symbol, local))
                return null;
        return call;
    }

    private static bool IsLoad(InvocationExpressionSyntax call, SemanticModel sm, Context ctx) =>
        sm.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "Load", IsStatic: true } m
        && SymbolEqualityComparer.Default.Equals(m.ContainingType, ctx.Document);
}
