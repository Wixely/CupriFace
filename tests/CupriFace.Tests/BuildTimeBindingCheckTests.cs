using System.Collections.Immutable;
using CupriFace.Binding.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// CF0060 at build time (src/CupriFace.Binding.Analyzers): the analyzer run over small compilations
/// built here, exactly as the compiler runs it — real C# against the real engine assembly, markup
/// handed over as AdditionalFiles with the metadata the resources targets attach.
///
/// <para>Most of these are the SILENT cases, on purpose. A typo being reported is the easy half; the
/// half that decides whether anyone leaves the warning on is that markup the engine renders correctly
/// is never reported — inside a repeat, through an item type's own list, on a model whose names
/// live in its data.</para>
/// </summary>
public class BuildTimeBindingCheckTests
{
    // What CupriFace.Resources.Gen generates for Assets/App.html, written out so the test does not
    // also depend on the generator.
    private const string AssetsClass = """
        public static class Assets
        {
            public static class App
            {
                public static global::CupriFace.Resources.CupriSource Html =>
                    global::CupriFace.Resources.CupriSource.Embedded(typeof(Assets).Assembly, "Assets/App.html");
            }
        }
        """;

    private const string Models = """
        using System.Collections.Generic;
        public class TaskItem { public string Name { get; set; } = ""; public string Status { get; set; } = ""; public List<Note> Notes { get; } = new(); }
        public class Note { public string Text { get; set; } = ""; }
        public class Group { public string Title { get; set; } = ""; public List<TaskItem> Items { get; } = new(); }
        public class User { public string Name { get; set; } = ""; public int Age { get; set; } }
        public class Board
        {
            public string Title { get; set; } = "";
            public int Count { get; set; }
            public string Theme { get; set; } = "";
            public User User { get; set; } = new();
            public User? Missing { get; set; }
            public List<TaskItem> Tasks { get; } = new();
            public TaskItem[] Pinned { get; } = new TaskItem[0];
            public IEnumerable<Group> Groups { get; } = new List<Group>();
        }
        """;

    private static string App(string model = "Board", string field = "new Board()") => $$"""
        using CupriFace;
        using CupriFace.Resources;
        public sealed class BoardApp : CupriApp
        {
            private readonly {{model}} _model = {{field}};
            public override object? Model => _model;
            protected override CupriSource MarkupSource => Assets.App.Html;
        }
        """;

    // ---- harness ---------------------------------------------------------------------------------

    private sealed class Text(string path, string text) : AdditionalText
    {
        public override string Path { get; } = path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text);
    }

    private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }

    private sealed class OptionsProvider(Dictionary<string, string?> logicalByPath) : AnalyzerConfigOptionsProvider
    {
        private static readonly Options Empty = new(new());
        public override AnalyzerConfigOptions GlobalOptions => Empty;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
            logicalByPath.TryGetValue(textFile.Path, out var logical) && logical is not null
                ? new Options(new() { ["build_metadata.AdditionalFiles.CupriLogicalName"] = logical })
                : Empty;
    }

    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray());

    /// <summary>Compile the sources (which must compile cleanly, or a silent result proves nothing)
    /// and run the analyzer over them with the given markup files.</summary>
    private static Diagnostic[] Analyze(string[] sources, params (string Path, string Text, string? Logical)[] files)
    {
        var trees = sources.Select((s, i) => CSharpSyntaxTree.ParseText(s, path: $"Source{i}.cs")).ToArray();
        var compilation = CSharpCompilation.Create("BindingCheckSubject", trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(e => e.ToString())));

        var options = new AnalyzerOptions(
            files.Select(f => (AdditionalText)new Text(f.Path, f.Text)).ToImmutableArray(),
            new OptionsProvider(files.ToDictionary(f => f.Path, f => f.Logical)));
        return compilation.WithAnalyzers([new BindingAnalyzer()], options)
            .GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult()
            .OrderBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .ToArray();
    }

    private const string HtmlPath = "C:/app/Assets/App.html";

    private static Diagnostic[] AnalyzeApp(string html, string? app = null) =>
        Analyze([app ?? App(), AssetsClass, Models], (HtmlPath, html, "Assets/App.html"));

    private static string Messages(Diagnostic[] d) => string.Join("\n", d.Select(x => x.GetMessage()));

    // ---- reported --------------------------------------------------------------------------------

    [Fact]
    public void A_typo_is_reported_in_the_html_file_at_its_line_and_column_with_a_suggestion()
    {
        var d = AnalyzeApp("""
            <body>
              <h1>{{Title}}</h1>
              <span class="count">{{Cuont}} tasks</span>
            </body>
            """);

        var f = Assert.Single(d);
        Assert.Equal("CF0060", f.Id);
        Assert.Equal(DiagnosticSeverity.Warning, f.Severity);
        Assert.Equal("{{Cuont}} does not name anything on Board, so it renders as empty text. Did you mean {{Count}}?",
                     f.GetMessage());

        var span = f.Location.GetLineSpan();
        Assert.Equal(HtmlPath, span.Path);
        Assert.Equal(2, span.StartLinePosition.Line);              // zero-based: the third line
        Assert.Equal(24, span.StartLinePosition.Character);        // the C of Cuont
        Assert.Equal(29, span.EndLinePosition.Character);
    }

    [Fact]
    public void Every_occurrence_is_reported_where_it_is()
    {
        var d = AnalyzeApp("<body>{{Titel}}\n<p>{{Titel}}</p></body>");
        Assert.Equal(2, d.Length);
        Assert.Equal(0, d[0].Location.GetLineSpan().StartLinePosition.Line);
        Assert.Equal(1, d[1].Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void A_dotted_path_is_walked_and_the_suggestion_fixes_the_segment_that_failed()
    {
        var d = AnalyzeApp("<body>{{User.Name}} {{User.Nmae}} {{Tasks.Count}} {{Usr.Age}}</body>");

        Assert.Equal(2, d.Length);
        Assert.Contains("{{User.Nmae}} does not name anything on Board, so it renders as empty text. Did you mean {{User.Name}}?",
                        Messages(d));
        Assert.Contains("{{Usr.Age}} does not name anything on Board, so it renders as empty text. Did you mean {{User}}?",
                        Messages(d));
    }

    [Fact]
    public void Nothing_close_says_so_rather_than_suggesting_something_unrelated()
    {
        var f = Assert.Single(AnalyzeApp("<body>{{Zebra}}</body>"));
        Assert.Contains("Nothing in scope is close to 'Zebra'", f.GetMessage());
    }

    [Fact]
    public void A_typo_inside_a_repeat_names_the_item_type_it_was_looked_for_on()
    {
        var f = Assert.Single(AnalyzeApp("""<body><div data-repeat="Tasks">{{Stauts}}</div></body>"""));
        Assert.Equal("{{Stauts}} does not name anything on TaskItem (an item of Tasks) or on Board, so it renders as empty text. "
                     + "Did you mean {{Status}}?", f.GetMessage());
    }

    [Fact]
    public void A_repeat_that_names_nothing_is_reported_once_and_its_contents_are_not()
    {
        var d = AnalyzeApp("""<body><div data-repeat="Taks"><b>{{Name}}</b> {{Anything}}</div></body>""");

        var f = Assert.Single(d);
        Assert.Equal("data-repeat=\"Taks\" does not name anything on Board, so the list renders no rows. Did you mean {{Tasks}}?",
                     f.GetMessage());
        Assert.Equal(24, f.Location.GetLineSpan().StartLinePosition.Character);
    }

    [Fact]
    public void Markup_written_in_the_html_property_is_checked_and_placed_inside_the_literal()
    {
        const string app = """"
            using CupriFace;
            public sealed class InlineApp : CupriApp
            {
                private readonly Board _model = new();
                public override object? Model => _model;
                public override string Html => """
                    <body>
                      <h1>{{Title}}</h1>
                      <p>{{Tilte}}</p>
                    </body>
                    """;
            }
            """";
        var f = Assert.Single(Analyze([app, Models]));
        Assert.Contains("{{Tilte}}", f.GetMessage());
        Assert.Contains("Did you mean {{Title}}?", f.GetMessage());

        var span = f.Location.GetLineSpan();
        Assert.Equal("Source0.cs", span.Path);
        Assert.Equal(8, span.StartLinePosition.Line);
        Assert.Equal(15, span.StartLinePosition.Character);   // the T of Tilte, in the C# file
        Assert.Equal("Tilte", f.Location.SourceTree!.GetText().ToString(f.Location.SourceSpan));
    }

    [Fact]
    public void Markup_in_a_const_and_in_a_verbatim_string_is_placed_exactly_too()
    {
        const string app = """
            using CupriFace;
            public sealed class ConstApp : CupriApp
            {
                private const string Markup = @"<body><div class=""x"" data-repeat=""Tasks"">{{Nmae}}</div></body>";
                private readonly Board _model = new();
                public override object? Model => _model;
                public override string Html => Markup;
            }
            """;
        var f = Assert.Single(Analyze([app, Models]));
        Assert.Equal("Nmae", f.Location.SourceTree!.GetText().ToString(f.Location.SourceSpan));
    }

    [Fact]
    public void A_model_class_can_name_its_markup_when_nothing_can_infer_it()
    {
        const string model = """
            using CupriFace.Binding;
            [CupriMarkup("Assets/Panel.html")]
            public class PanelModel { public string Heading { get; set; } = ""; }
            """;
        var f = Assert.Single(Analyze([model], ("C:/app/Assets/Panel.html", "<body>{{Heading}} {{Headnig}}</body>", "Assets/Panel.html")));
        Assert.Contains("{{Headnig}} does not name anything on PanelModel", f.GetMessage());
        Assert.Equal("C:/app/Assets/Panel.html", f.Location.GetLineSpan().Path);
    }

    [Fact]
    public void An_assembly_attribute_maps_markup_to_a_model_it_cannot_annotate()
    {
        const string source = """
            [assembly: CupriFace.Binding.CupriMarkup("Assets/Panel.html", typeof(User))]
            """;
        var f = Assert.Single(Analyze([source, Models], ("C:/app/Assets/Panel.html", "<body>{{Name}} {{Aeg}}</body>", "Assets/Panel.html")));
        Assert.Contains("Did you mean {{Age}}?", f.GetMessage());
    }

    [Fact]
    public void A_file_added_to_AdditionalFiles_by_hand_is_found_by_its_path()
    {
        const string model = """
            [CupriFace.Binding.CupriMarkup("Assets/Panel.html")]
            public class PanelModel { public string Heading { get; set; } = ""; }
            """;
        Assert.Single(Analyze([model], ("C:/app/Assets/Panel.html", "<body>{{Headnig}}</body>", null)));
        Assert.Empty(Analyze([model], ("C:/app/Other/Panel.html", "<body>{{Headnig}}</body>", null)));
    }

    [Fact]
    public void Load_then_Bind_is_inferred_chained_fluent_or_through_a_local()
    {
        const string program = """
            using CupriFace;
            public static class Program
            {
                public static void Chained()
                {
                    var board = new Board();
                    using var doc = CupriDocument.Load("<body>{{Titel}}</body>", "").Bind(board);
                }
                public static void Local()
                {
                    const string html = "<body>{{Cuont}}</body>";
                    using var doc = CupriDocument.Load(html);
                    doc.Bind(new Board());
                }
            }
            """;
        var d = Analyze([program, Models]);
        Assert.Equal(2, d.Length);
        Assert.Contains("Did you mean {{Title}}?", Messages(d));
        Assert.Contains("Did you mean {{Count}}?", Messages(d));
    }

    [Fact]
    public void EmbeddedAsset_and_ReadText_forms_are_followed_to_the_file()
    {
        const string viaHelper = """
            using CupriFace;
            using CupriFace.Resources;
            public sealed class HelperApp : CupriApp
            {
                private readonly Board _model = new();
                public override object? Model => _model;
                protected override CupriSource MarkupSource => EmbeddedAsset("Assets/App.html");
            }
            """;
        const string viaReadText = """
            using CupriFace;
            public sealed class ReadApp : CupriApp
            {
                private readonly Board _model = new();
                public override object? Model => _model;
                public override string Html => Assets.App.Html.ReadText();
            }
            """;
        Assert.Single(AnalyzeApp("<body>{{Titel}}</body>", viaHelper));
        Assert.Single(AnalyzeApp("<body>{{Titel}}</body>", viaReadText));
    }

    // ---- silent ----------------------------------------------------------------------------------

    [Fact]
    public void Correct_markup_is_silent()
    {
        Assert.Empty(AnalyzeApp("""
            <body class="{{Theme}}">
              <!-- a comment is never bound, so {{NotAProperty}} here is not a binding -->
              <h1 title="{{Title}}">{{ Title }} — {{Count}} / {{Tasks.Count}} / {{Pinned.Length}}</h1>
              <p>{{User.Name}} is {{User.Age}}, {{Missing.Name}}</p>
              <style>.x { color: red; }</style>
              <input value="{{Title}}">
              <cupri-icon name="x" />
              <div data-repeat="Tasks"><span>{{.}} {{this}} {{this.Name}}</span></div>
            </body>
            """));
    }

    [Fact]
    public void Inside_a_repeat_the_item_comes_first_and_the_model_is_still_in_reach()
    {
        Assert.Empty(AnalyzeApp("""
            <body>
              <div class="row {{Theme}}" data-repeat="Tasks">{{Name}} — {{Status}} — {{Theme}} — {{User.Name}}</div>
              <div data-repeat="Pinned">{{Name}}</div>
            </body>
            """));
    }

    [Fact]
    public void Nested_repeats_and_a_list_that_lives_on_an_item_type_resolve_to_a_fixed_point()
    {
        // Groups → Items → Notes: Notes is only reachable from TaskItem, which is only reachable
        // from Group (#160 in the doctor).
        Assert.Empty(AnalyzeApp("""
            <body>
              <section data-repeat="Groups">
                <h2>{{Title}}</h2>
                <div data-repeat="Items">
                  {{Name}}
                  <i data-repeat="Notes">{{Text}} in {{Title}}</i>
                </div>
              </section>
            </body>
            """));
    }

    [Fact]
    public void A_model_typed_object_or_an_interface_is_not_checked()
    {
        Assert.Empty(AnalyzeApp("<body>{{Anything}}</body>", App("object", "new Board()")));
        Assert.Empty(AnalyzeApp("<body>{{Anything}}</body>",
            App("System.Collections.Generic.IReadOnlyList<int>", "new System.Collections.Generic.List<int>()")));
    }

    [Fact]
    public void Models_whose_names_live_in_their_data_are_not_checked()
    {
        const string shaped = """
            using System.Collections.Generic;
            using CupriFace.Binding;
            public sealed class Shaped : IBindableAccessor, IBindableShape
            {
                public IEnumerable<string> BindableNames => new[] { "A" };
                public object? GetBindable(string name) => null;
                public bool SetBindable(string name, object? value) => false;
            }
            public sealed class HandWritten : IBindableAccessor
            {
                public object? GetBindable(string name) => name;
                public bool SetBindable(string name, object? value) => false;
            }
            """;
        foreach (var (type, init) in new[]
                 {
                     ("Shaped", "new Shaped()"),
                     ("HandWritten", "new HandWritten()"),
                     ("System.Collections.Generic.Dictionary<string, object>", "new()"),
                     ("System.Text.Json.Nodes.JsonObject", "new()"),
                 })
            Assert.Empty(Analyze([App(type, init), AssetsClass, Models, shaped], (HtmlPath, "<body>{{Anything}}</body>", "Assets/App.html")));
    }

    [Fact]
    public void A_generated_accessor_is_still_checked_against_its_properties()
    {
        // [CupriBindable] puts IBindableAccessor on the type too; unlike a hand-written one it answers
        // exactly the declared properties, so it is checked like any class. The interface is written
        // out here because the generator is not running in this compilation.
        const string bindable = """
            using CupriFace.Binding;
            [CupriBindable]
            public partial class Settings : IBindableAccessor
            {
                public int Volume { get; set; }
                public object? GetBindable(string name) => null;
                public bool SetBindable(string name, object? value) => false;
            }
            """;
        var f = Assert.Single(Analyze([App("Settings", "new Settings()"), AssetsClass, bindable],
                                      (HtmlPath, "<body>{{Volume}} {{Volumn}}</body>", "Assets/App.html")));
        Assert.Contains("Did you mean {{Volume}}?", f.GetMessage());
    }

    [Fact]
    public void A_property_typed_as_a_base_class_answers_for_what_derives_from_it()
    {
        const string shapes = """
            public abstract class Shape { public string Label { get; set; } = ""; }
            public sealed class Circle : Shape { public double Radius { get; set; } }
            public class Scene { public Shape Current { get; set; } = new Circle(); public System.Collections.Generic.List<Shape> All { get; } = new(); }
            """;
        Assert.Empty(Analyze([App("Scene", "new Scene()"), AssetsClass, shapes],
            (HtmlPath, """<body>{{Current.Radius}} {{Current.Label}} <i data-repeat="All">{{Radius}}</i></body>""", "Assets/App.html")));
        Assert.Single(Analyze([App("Scene", "new Scene()"), AssetsClass, shapes],
            (HtmlPath, "<body>{{Current.Raduis}}</body>", "Assets/App.html")));
    }

    [Fact]
    public void A_repeat_over_items_that_cannot_be_known_silences_what_is_inside_it()
    {
        const string loose = """
            public class Loose
            {
                public System.Collections.ArrayList Untyped { get; } = new();
                public System.Collections.Generic.List<object> Objects { get; } = new();
                public string Title { get; set; } = "";
            }
            """;
        var d = Analyze([App("Loose", "new Loose()"), AssetsClass, loose], (HtmlPath, """
            <body>
              <div data-repeat="Untyped">{{Whatever}}</div>
              <div data-repeat="Objects">{{Anything}}</div>
              {{Titel}}
            </body>
            """, "Assets/App.html"));
        var f = Assert.Single(d);
        Assert.Contains("{{Titel}}", f.GetMessage());
    }

    [Fact]
    public void Unbalanced_tags_around_an_unknowable_list_silence_the_document_rather_than_guess()
    {
        const string loose = """
            public class Loose { public System.Collections.ArrayList Untyped { get; } = new(); public string Title { get; set; } = ""; }
            """;
        // The stray </span> means the scanner cannot be sure where the list ends, so {{Whatever}}
        // might be inside it.
        Assert.Empty(Analyze([App("Loose", "new Loose()"), AssetsClass, loose],
            (HtmlPath, """<body><div data-repeat="Untyped"><p>x</span></div>{{Whatever}}</body>""", "Assets/App.html")));
    }

    [Fact]
    public void Markup_no_model_is_bound_to_is_left_alone()
    {
        Assert.Empty(Analyze([Models], (HtmlPath, "<body>{{Titel}}</body>", "Assets/App.html")));
    }

    [Fact]
    public async Task The_severity_follows_the_compilation_options_as_a_globalconfig_sets_them()
    {
        // A .globalconfig's dotnet_diagnostic.CF0060.severity (or WarningsAsErrors / NoWarn) arrives
        // as exactly this option. An .editorconfig SECTION does not: it is keyed by syntax tree, and
        // a finding in an .html file has none (TOOLBOX §3).
        var trees = new[] { App(), AssetsClass, Models }.Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
        var compilation = CSharpCompilation.Create("Severity", trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)
                .WithSpecificDiagnosticOptions([new("CF0060", ReportDiagnostic.Error)]));
        var options = new AnalyzerOptions([new Text(HtmlPath, "<body>{{Titel}}</body>")],
                                          new OptionsProvider(new() { [HtmlPath] = "Assets/App.html" }));
        var f = Assert.Single(await compilation.WithAnalyzers([new BindingAnalyzer()], options)
                                               .GetAnalyzerDiagnosticsAsync());
        Assert.Equal(DiagnosticSeverity.Error, f.Severity);
    }
}
