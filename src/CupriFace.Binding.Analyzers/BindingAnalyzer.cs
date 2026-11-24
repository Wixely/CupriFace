using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace CupriFace.Binding.Analyzers;

/// <summary>
/// CF0060 at build time: a <c>{{Path}}</c> in an app's markup that names nothing on its model.
///
/// <para>The quietest bug the engine has. An unknown property resolves to null, null formats as the
/// empty string, and the element renders perfectly with nothing in it — on screen it is a model
/// with no data yet, so it survives a code review and a screenshot alike. <c>CupriDoctor</c> finds
/// it, but only for someone who runs the doctor with the model in hand. This finds it on every
/// build, and puts the squiggle on the characters in the .html file.</para>
///
/// <para>The same code as the doctor's on purpose: it is the same defect with the same fix, one
/// <c>dotnet_diagnostic.CF0060.severity</c> line governs it, and a search for the code finds both
/// halves. A Warning rather than the doctor's Error because a build cannot see everything a running
/// document can — what is reported is certain, but the build is the wrong place to stop someone
/// over a check that has deliberately declined to look at half the model (see
/// <see cref="ModelShapes"/>).</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BindingAnalyzer : DiagnosticAnalyzer
{
    public const string Id = "CF0060";

    private const string LogicalNameKey = "build_metadata.AdditionalFiles.CupriLogicalName";

    internal static readonly DiagnosticDescriptor UnresolvedBinding = new(
        Id,
        title: "Binding names nothing on the model",
        messageFormat: "{0} does not name anything on {1}, {2}",
        category: "CupriFace.Binding",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An unknown property resolves to null and renders as empty text, so a misspelt binding "
                     + "looks exactly like a model with no data yet. Checked against the model's static type; "
                     + "models typed object, interfaces, dictionaries, JSON and IBindableShape are not checked.",
        helpLinkUri: "https://github.com/Wixely/CupriFace/blob/main/TOOLBOX.md#build-time-binding-checks-cf0060");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [UnresolvedBinding];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var compilation = start.Compilation;
            // Nothing to do in a compilation that cannot see the engine.
            if (compilation.GetTypeByMetadataName("CupriFace.CupriApp") is null
                && compilation.GetTypeByMetadataName("CupriFace.Binding.CupriMarkupAttribute") is null)
                return;

            // Built once per compilation, on first need, then shared by every file asked about.
            var index = new Lazy<Index>(() => Index.Build(compilation, start.CancellationToken),
                                        LazyThreadSafetyMode.ExecutionAndPublication);

            start.RegisterAdditionalFileAction(ctx => AnalyzeAsset(ctx, index.Value));
            start.RegisterSemanticModelAction(ctx => AnalyzeInline(ctx, index.Value));
        });
    }

    private sealed class Index
    {
        public List<MarkupUse> Uses { get; private set; } = new();
        public ModelShapes Shapes { get; private set; } = null!;

        public static Index Build(Compilation compilation, CancellationToken ct)
        {
            var (uses, types) = MarkupUses.Find(compilation, ct);
            return new Index { Uses = uses, Shapes = new ModelShapes(compilation, types) };
        }
    }

    // ---- markup in an Assets/ file ---------------------------------------------------------------

    private static void AnalyzeAsset(AdditionalFileAnalysisContext ctx, Index index)
    {
        var file = ctx.AdditionalFile;
        var uses = index.Uses.Where(u => u.Asset is not null && Matches(file, u.Asset, ctx.Options)).ToList();
        if (uses.Count == 0) return;
        if (file.GetText(ctx.CancellationToken) is not { } text) return;

        var scan = MarkupScan.Scan(text.ToString());
        foreach (var use in uses)
            foreach (var f in BindingCheck.Run(scan, use.Model, index.Shapes))
            {
                var span = new TextSpan(f.Start, f.Length);
                var location = Location.Create(file.Path, span, text.Lines.GetLinePositionSpan(span));
                ctx.ReportDiagnostic(Diagnostic.Create(UnresolvedBinding, location, f.Subject, f.Where, f.Advice));
            }
    }

    /// <summary>Is this the file a logical name means? By the metadata the resources targets attach
    /// (the same name the Assets class is generated from), or — for a project that adds its markup
    /// to AdditionalFiles by hand — by the end of its path.</summary>
    private static bool Matches(AdditionalText file, string asset, AnalyzerOptions options)
    {
        if (options.AnalyzerConfigOptionsProvider.GetOptions(file).TryGetValue(LogicalNameKey, out var logical)
            && logical.Length > 0)
            return string.Equals(logical, asset, StringComparison.OrdinalIgnoreCase);

        var path = file.Path.Replace('\\', '/');
        var want = asset.Replace('\\', '/').TrimStart('/');
        return path.Equals(want, StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/" + want, StringComparison.OrdinalIgnoreCase);
    }

    // ---- markup written in the C# ----------------------------------------------------------------

    private static void AnalyzeInline(SemanticModelAnalysisContext ctx, Index index)
    {
        var tree = ctx.SemanticModel.SyntaxTree;
        foreach (var use in index.Uses)
        {
            if (use.Inline is null || use.InlineLocation is null) continue;
            var home = use.Literal.SyntaxTree ?? use.InlineLocation.SourceTree;
            if (home != tree) continue;

            var scan = MarkupScan.Scan(use.Inline);
            foreach (var f in BindingCheck.Run(scan, use.Model, index.Shapes))
                ctx.ReportDiagnostic(Diagnostic.Create(UnresolvedBinding, Place(use, f), f.Subject, f.Where, f.Advice));
        }
    }

    /// <summary>
    /// The finding's characters inside the C# literal. The markup's VALUE is what was checked, and a
    /// raw string's value has its indentation removed and a verbatim one's quotes undoubled, so value
    /// offsets are not source offsets. Instead the Nth occurrence of the finding's text in the value
    /// is matched to the Nth occurrence in the literal's source — bindings never contain the
    /// characters an escape changes, so the two line up — and anything that cannot be matched
    /// falls back to the whole expression rather than to a wrong line.
    /// </summary>
    private static Location Place(MarkupUse use, BindingFinding f)
    {
        var fallback = use.Literal.SyntaxTree is not null ? use.Literal.GetLocation() : use.InlineLocation!;
        if (use.Literal.SyntaxTree is null) return fallback;

        var value = use.Inline!;
        var source = use.Literal.Text;
        var needle = value.Substring(f.Start, f.Length);

        var nth = 0;
        for (var at = value.IndexOf(needle, StringComparison.Ordinal); at >= 0 && at < f.Start;
             at = value.IndexOf(needle, at + 1, StringComparison.Ordinal))
            nth++;

        var pos = -1;
        for (var k = 0; k <= nth; k++)
        {
            pos = source.IndexOf(needle, pos + 1, StringComparison.Ordinal);
            if (pos < 0) return fallback;
        }
        var start = use.Literal.SpanStart + pos;
        return Location.Create(use.Literal.SyntaxTree, new TextSpan(start, f.Length));
    }
}
