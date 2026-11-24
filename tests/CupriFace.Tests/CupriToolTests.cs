using CupriFace.Tool;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The <c>cupri</c> command (src/CupriFace.Tool), run in-process: it renders through the engine,
/// binds a JSON model the way an app's model binds, reports the doctor's findings in the compiler's
/// format, and installs the agent skill.
/// </summary>
public class CupriToolTests(ITestOutputHelper output)
{
    private sealed class Dir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("cupri-tool-").FullName;
        public string File(string name, string text) { var p = System.IO.Path.Combine(Path, name); System.IO.File.WriteAllText(p, text); return p; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        using var o = new StringWriter();
        using var e = new StringWriter();
        var code = Cli.Run(args, o, e);
        output.WriteLine(o.ToString());
        output.WriteLine(e.ToString());
        return (code, o.ToString(), e.ToString());
    }

    private const string Page = """
<body>
  <div class="row">
    <div class="name">{{Name}}</div>
    <div class="count">{{Count}} items</div>
  </div>
  <div class="item" data-repeat="Items">{{Title}}</div>
</body>
""";
    private const string Css = "body{margin:0;font-family:sans-serif} .row{display:flex;align-items:center;gap:8px;flex-wrap:wrap}";
    private const string Model = """{ "Name": "Ada", "Count": 3, "Items": [ { "Title": "One" }, { "Title": "Two" } ] }""";

    [Fact]
    public void Preview_WritesTheImage_PicksUpTheSiblingStylesheet_AndIsCleanForGoodMarkup()
    {
        using var d = new Dir();
        var html = d.File("App.html", Page);
        d.File("App.css", Css);
        var model = d.File("model.json", Model);

        var (code, outText, _) = Run("preview", html, "--model", model);

        Assert.Equal(0, code);
        Assert.Contains("App.html + App.css bound to model.json", outText);
        Assert.Contains("no problems found", outText);
        var png = Path.Combine(d.Path, "App.preview.png");
        Assert.True(File.Exists(png));
        using var img = SkiaSharp.SKBitmap.Decode(png);
        Assert.True(img.Width > 1024 + 412, "desktop and phone side by side");
    }

    [Fact]
    public void Preview_OfAPageUsingAComponentLibrary_NamesTheTagItCannotDraw_WithoutFailing()
    {
        // The tool cannot add a library (that would take reflection), so the honest outcome is a
        // note naming the tag and the reason — not an error, and not an unexplained blank box.
        using var d = new Dir();
        var html = d.File("App.html", "<body>\n  <leaf-card heading=\"Booking\">x</leaf-card>\n</body>");

        var (code, outText, _) = Run("preview", html);

        Assert.Equal(0, code);
        Assert.Contains("App.html(2): info CF0020: <leaf-card> looks like a custom element", outText);
        Assert.Contains("cupri preview` knows only the built-ins", outText);
    }

    [Fact]
    public void Preview_ReportsABindingTypo_AtItsLine_InCompilerFormat_AndFails()
    {
        using var d = new Dir();
        var html = d.File("App.html", Page.Replace("{{Count}}", "{{Cuont}}").Replace("{{Title}}", "{{Titel}}"));
        d.File("App.css", Css);
        var model = d.File("model.json", Model);

        var (code, outText, _) = Run("check", html, "--model", model);

        Assert.Equal(1, code);
        Assert.Contains($"{html}(4): error CF0060: {{{{Cuont}}}}", outText);
        Assert.Contains("Did you mean {{Count}}?", outText);
        // Inside the repeat, checked against the ITEMS of the JSON array.
        Assert.Contains($"{html}(6): error CF0060: {{{{Titel}}}}", outText);
        Assert.Contains("Did you mean {{Title}}?", outText);
        // And the bindings that are right are not reported — a JSON model has no .NET type, and
        // the check walks its keys instead (IBindableShape).
        Assert.DoesNotContain("{{Name}}", outText);
    }

    [Fact]
    public void Preview_WithoutAModel_SaysTheBindingsWereNotChecked()
    {
        using var d = new Dir();
        var html = d.File("App.html", Page);

        var (_, outText, _) = Run("check", html);

        Assert.Contains("(no stylesheet)", outText);
        Assert.Contains("no --model", outText);
    }

    [Fact]
    public void Check_ReportsAPhoneOnlyProblem_AsPhoneOnly()
    {
        using var d = new Dir();
        var html = d.File("App.html", "<body><div class='wide'>wide</div></body>");
        d.File("App.css", "body{margin:0} .wide{width:700px;height:40px;background:#eef}");

        var (code, outText, _) = Run("check", html, "--fail-on", "warning");

        Assert.Equal(1, code);
        Assert.Contains("CF0072", outText);
        Assert.Contains("[at 412x915 only]", outText);
    }

    [Fact]
    public void FailOn_Never_AlwaysExitsZero()
    {
        using var d = new Dir();
        var html = d.File("App.html", "<body><div>{{Missing}}</div></body>");
        var model = d.File("m.json", "{ \"Name\": \"x\" }");
        Assert.Equal(0, Run("check", html, "--model", model, "--fail-on", "never").Code);
    }

    [Fact]
    public void Sizes_AcceptNamesAndWxH_AndRejectNonsense()
    {
        using var d = new Dir();
        var html = d.File("App.html", "<body><div>hi</div></body>");
        var (code, outText, _) = Run("check", html, "--sizes", "small-phone,1280x800");
        Assert.Equal(0, code);
        Assert.Contains("at 360x640, 1280x800", outText);

        var bad = Run("check", html, "--sizes", "huge");
        Assert.Equal(2, bad.Code);
        Assert.Contains("not a size", bad.Err);
    }

    [Fact]
    public void Tree_PrintsTheLaidOutBoxes()
    {
        using var d = new Dir();
        var html = d.File("App.html", Page);
        d.File("App.css", Css);
        var model = d.File("model.json", Model);

        var (_, outText, _) = Run("preview", html, "--model", model, "--tree", "2", "-o", Path.Combine(d.Path, "x.png"));

        Assert.Contains("tree at 1024x768", outText);
        Assert.Contains("div.row", outText);
    }

    [Fact]
    public void Sweep_FlipsTheBoolsInAJsonModel()
    {
        using var d = new Dir();
        // Open, the panel is a fixed 40px box holding 120px of content — a finding that exists only
        // in the state the flag produces.
        var html = d.File("App.html", """
<body>
  <div class="panel" style="display:{{PanelDisplay}}">x</div>
  <cupri-switch checked="{{Open}}">Open</cupri-switch>
  <div class="box {{BoxState}}"><div class="tall">tall</div></div>
</body>
""");
        d.File("App.css", "body{margin:0} .box{height:200px} .box.closed{} .tall{height:120px}");
        var model = d.File("m.json", """{ "Open": false, "PanelDisplay": "none", "BoxState": "closed" }""");

        var (_, outText, _) = Run("check", html, "--model", model, "--sweep");

        Assert.Matches(@"[2-9]\d* states checked", outText);
    }

    [Fact]
    public void Skill_InstallsTheEmbeddedSkill_WhichIsTheRepositorysOwnCopy()
    {
        using var d = new Dir();

        var (code, outText, _) = Run("skill", "--dir", d.Path);

        Assert.Equal(0, code);
        var installed = Path.Combine(d.Path, ".claude", "skills", "cupriface-ui", "SKILL.md");
        Assert.True(File.Exists(installed), outText);
        var repoCopy = Path.Combine(RepoRoot(), ".claude", "skills", "cupriface-ui", "SKILL.md");
        Assert.Equal(File.ReadAllText(repoCopy), File.ReadAllText(installed));
        Assert.StartsWith("---\nname: cupriface-ui", File.ReadAllText(installed).Replace("\r\n", "\n"));
    }

    // ---- --app: a built app, with its own model ---------------------------------------------------

    private static string DemoAppDll() => Path.Combine(AppContext.BaseDirectory, "DemoApp.dll");

    [Fact]
    public void App_PreviewsABuiltApp_WithItsOwnModel_AndNamesItsMarkupFile()
    {
        using var d = new Dir();
        var outPng = Path.Combine(d.Path, "settings.png");

        var (code, outText, _) = Run("preview", "--app", DemoAppDll(), "--type", "SettingsApp", "--sizes", "app,phone", "-o", outPng);

        Assert.Equal(0, code);
        Assert.Contains("SettingsApp from DemoApp.dll (SettingsApp.html + SettingsApp.css) bound to its Settings", outText);
        Assert.Contains("at 600x520, 412x915", outText);          // "app" is the app's own window size
        Assert.True(File.Exists(outPng));
        // Findings name the file the markup came from, found through the stamped project directory.
        var html = Path.Combine(RepoRoot(), "samples", "DemoApp", "Assets", "SettingsApp.html");
        Assert.Contains(html + "(", outText);
    }

    [Fact]
    public void App_WithSeveralApps_AsksWhichOne()
    {
        var (code, _, err) = Run("check", "--app", DemoAppDll());
        Assert.Equal(2, code);
        Assert.Contains("pick one with --type", err);
        Assert.Contains("SettingsApp", err);
    }

    [Fact]
    public void App_RefusesOptionsThatOnlyMeanSomethingForAPage()
    {
        using var d = new Dir();
        var model = d.File("m.json", "{}");
        Assert.Equal(2, Run("check", "--app", DemoAppDll(), "--type", "SettingsApp", "--model", model).Code);
        Assert.Equal(2, Run("check", d.File("p.html", "<body></body>"), "--type", "SettingsApp").Code);
        Assert.Equal(2, Run("check", d.File("q.html", "<body></body>"), "--sizes", "app").Code);
    }

    [Fact]
    public void Usage_ErrorsExitTwo()
    {
        Assert.Equal(2, Run().Code);
        Assert.Equal(2, Run("preview").Code);
        Assert.Equal(2, Run("preview", "no-such-file.html").Code);
        Assert.Equal(2, Run("frobnicate").Code);
        Assert.Equal(0, Run("--help").Code);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CupriFace.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }
}
