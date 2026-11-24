using CupriFace.DevTools;
using CupriFace.Diagnostics;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Resources;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The development tools: <see cref="CupriDocument.ReplaceSource"/> (the swap a hot reload makes),
/// <see cref="HotReloadSession"/> (which watches the files and runs the doctor), and the F12
/// <see cref="DevToolsPanel"/>. All headless — the panel is a document, so it renders and takes
/// input here exactly as it does docked in a window.
/// </summary>
public class DevToolsTests(ITestOutputHelper output)
{
    private const string Html = """
<body>
  <div class="page">
    <h1 class="title">Hello {{Name}}</h1>
    <div class="card" id="main-card">
      <cupri-textfield class="name" value="{{Name}}"></cupri-textfield>
      <div class="count">{{Count}} items</div>
    </div>
  </div>
</body>
""";
    private const string Css = """
body { margin:0; font-family:sans-serif; }
.page { padding:16px; }
.card { padding:12px; background:#eef; border-radius:8px; }
.count { color:#334; }
""";

    private sealed class Model
    {
        public string Name { get; set; } = "Ada";
        public int Count { get; set; } = 3;
    }

    /// <summary>An app whose markup and stylesheet are files on disk, as a hot-reloaded app's are.</summary>
    private sealed class FileApp(string html, string css, Model model) : CupriApp
    {
        protected override CupriSource MarkupSource => CupriSource.File(html);
        protected override CupriSource StyleSource => CupriSource.File(css);
        public override object Model => model;
    }

    private sealed class Workspace : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("cupri-devtools-").FullName;
        public string HtmlPath => Path.Combine(Dir, "app.html");
        public string CssPath => Path.Combine(Dir, "app.css");
        public Workspace(string html, string css)
        {
            File.WriteAllText(HtmlPath, html);
            File.WriteAllText(CssPath, css);
        }
        public void Dispose() { try { Directory.Delete(Dir, recursive: true); } catch { /* a watcher may still hold it */ } }
    }

    private static void Frame(CupriDocument doc, int w = 600, int h = 400) { using var _ = doc.RenderToImage(w, h); }

    private static RenderNode? FindClass(RenderNode root, string cls) =>
        TestDoc.Find(root, n => n.Element?.ClassList.Contains(cls) == true);

    private static string TextOf(RenderNode n)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(RenderNode x) { if (x.IsText) sb.Append(x.Text).Append(' '); foreach (var c in x.Children) Walk(c); }
        Walk(n);
        return sb.ToString();
    }

    /// <summary>Pump and render until <paramref name="done"/> holds — what a host's loop does: a
    /// saved file requests a reload, and the host runs it between frames.</summary>
    private static bool FramesUntil(CupriDocument doc, HotReloadSession session, Func<bool> done, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            session.Pump();
            Frame(doc);
            if (done()) return true;
            Thread.Sleep(25);
        }
        return false;
    }

    // ---- ReplaceSource -----------------------------------------------------------------------

    [Fact]
    public void ReplaceSource_SwapsTheMarkup_AndKeepsTheModel()
    {
        var model = new Model { Name = "Grace" };
        using var t = new TestDoc(Html, Css, model, 600, 400, components: true);
        Assert.Contains("Hello Grace", TextOf(t.Root));

        Assert.True(t.Doc.ReplaceSource(Html.Replace("Hello", "Welcome"), Css));
        t.Layout();

        Assert.Contains("Welcome Grace", TextOf(t.Root));
        model.Name = "Hopper";                 // still the same model, still bound
        t.Doc.Refresh();
        t.Layout();
        Assert.Contains("Welcome Hopper", TextOf(t.Root));
    }

    [Fact]
    public void ReplaceSource_NewStylesheet_IsApplied()
    {
        using var t = new TestDoc(Html, Css, new Model(), 600, 400, components: true);
        var before = FindClass(t.Root, "card")!.Height;

        t.Doc.ReplaceSource(Html, Css.Replace("padding:12px", "padding:40px"));
        t.Layout();

        Assert.Equal(before + 56, FindClass(t.Root, "card")!.Height, 1);
    }

    [Fact]
    public void ReplaceSource_SameText_ChangesNothing()
    {
        using var t = new TestDoc(Html, Css, new Model(), components: true);
        Assert.False(t.Doc.ReplaceSource(Html, Css));
        Assert.False(t.Doc.ReplaceSource(Html));   // null css keeps the stylesheet
    }

    [Fact]
    public void ReplaceSource_KeepsWhatTheUserTyped()
    {
        var model = new Model();
        using var t = new TestDoc(Html, Css, model, 600, 400, components: true);
        var field = TestDoc.Find(FindClass(t.Root, "name")!, n => n.Element?.GetAttribute("role") == "textbox")!;
        var box = HitTesting.AbsoluteBox(field);
        t.Doc.DispatchClick(box.X + box.W - 4, box.Y + box.H / 2);
        foreach (var ch in "!!") t.Doc.DispatchKey(ch.ToString(), EditKey.None);
        Assert.Equal("Ada!!", model.Name);

        t.Doc.ReplaceSource(Html.Replace("items", "things"), Css);
        t.Layout();

        Assert.Equal("Ada!!", model.Name);
        Assert.Contains("Hello Ada!!", TextOf(t.Root));
        Assert.Contains("3 things", TextOf(t.Root));
    }

    // ---- the function keys -------------------------------------------------------------------

    [Fact]
    public void FunctionKeys_ReachTheDocument_AndMeanNothingToIt()
    {
        using var t = new TestDoc(Html, Css, new Model(), components: true);
        Assert.False(t.Doc.DispatchKey(null, EditKey.F12));
        Assert.False(t.Doc.DispatchKey(null, EditKey.F5, KeyMods.Ctrl));
    }

    // ---- hot reload --------------------------------------------------------------------------

    [Fact]
    public void Session_ReloadsTheDocument_WhenTheMarkupFileIsSaved()
    {
        using var ws = new Workspace(Html, Css);
        var app = new FileApp(ws.HtmlPath, ws.CssPath, new Model());
        using var doc = app.CreateDocument();
        Frame(doc);
        using var log = new StringWriter();
        using var session = new HotReloadSession(app, doc, log);

        Assert.Equal(Path.GetFullPath(ws.HtmlPath), session.MarkupFile);
        File.WriteAllText(ws.HtmlPath, Html.Replace("Hello", "Reloaded"));

        Assert.True(FramesUntil(doc, session, () => TextOf(doc.Root).Contains("Reloaded Ada")), log.ToString());
        Assert.Equal(1, session.ReloadCount);
        output.WriteLine(log.ToString());
    }

    [Fact]
    public void Session_ReloadsTheDocument_WhenTheStylesheetIsSaved()
    {
        using var ws = new Workspace(Html, Css);
        var app = new FileApp(ws.HtmlPath, ws.CssPath, new Model());
        using var doc = app.CreateDocument();
        Frame(doc);
        var before = FindClass(doc.Root, "card")!.Height;
        using var session = new HotReloadSession(app, doc, TextWriter.Null);

        File.WriteAllText(ws.CssPath, Css.Replace("padding:12px", "padding:30px"));

        Assert.True(FramesUntil(doc, session, () => FindClass(doc.Root, "card")!.Height > before + 30));
    }

    [Fact]
    public void Session_PrintsTheDoctorsFindings_InTheCompilersFormat()
    {
        // A binding typo: the classic silent failure, now a line an editor's problem matcher reads.
        using var ws = new Workspace(Html.Replace("{{Count}}", "{{Cuont}}"), Css);
        var app = new FileApp(ws.HtmlPath, ws.CssPath, new Model());
        using var doc = app.CreateDocument();
        Frame(doc);
        using var log = new StringWriter();
        using var session = new HotReloadSession(app, doc, log);

        session.CheckNow();

        var text = log.ToString();
        output.WriteLine(text);
        var line = Html.Split('\n').ToList().FindIndex(l => l.Contains("{{Count}}")) + 1;
        Assert.Contains($"{Path.GetFullPath(ws.HtmlPath)}({line}): error CF0060:", text);
        Assert.Contains("[CupriFace] Doctor: checking.", text);
        Assert.Matches(@"\[CupriFace\] Doctor: 1 error", text);
        // The shape VS Code's $msCompile problem matcher expects.
        Assert.Matches(@"(?m)^(\S.*)\((\d+)\)\s*:\s+(error|warning|info)\s+(\w+\d+)\s*:\s*(.*)$", text);
    }

    [Fact]
    public void Session_KeepsThePreviousSource_WhenTheNewOneCannotBeRead()
    {
        using var ws = new Workspace(Html, Css);
        var app = new FileApp(ws.HtmlPath, ws.CssPath, new Model());
        using var doc = app.CreateDocument();
        Frame(doc);
        using var session = new HotReloadSession(app, doc, TextWriter.Null);

        File.Delete(ws.HtmlPath);             // an editor mid-rename, or the file simply gone
        session.ReloadNow();

        Assert.NotNull(session.LastError);
        Assert.Contains("Hello Ada", TextOf(doc.Root));
    }

    [Fact]
    public void Session_TracesAnEmbeddedAsset_BackToItsProjectFile()
    {
        // The Showcase embeds Assets/ShowcaseApp.html through CupriFace.Resources.targets, which
        // stamps the project directory into a Debug build — the file hot reload must watch.
        var app = new CupriFace.Demo.ShowcaseApp();
        using var doc = app.CreateDocument();
        using var session = new HotReloadSession(app, doc, TextWriter.Null);

        var expected = Path.GetFullPath(Path.Combine(RepoRoot(), "samples", "DemoApp", "Assets", "ShowcaseApp.html"));
        Assert.Equal(expected, session.MarkupFile, ignoreCase: OperatingSystem.IsWindows());
        Assert.EndsWith("ShowcaseApp.css", session.StyleFile);
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "CupriFace.slnx"))) return d.FullName;
        throw new InvalidOperationException("repo root not found");
    }

    // ---- the panel ---------------------------------------------------------------------------

    private static (CupriDocument App, DevToolsPanel Panel, HotReloadSession Session, Workspace Ws) Inspect(
        string? html = null, KeyChord? shortcut = null)
    {
        var ws = new Workspace(html ?? Html, Css);
        var app = new FileApp(ws.HtmlPath, ws.CssPath, new Model());
        var doc = app.CreateDocument();
        Frame(doc);
        var session = new HotReloadSession(app, doc, TextWriter.Null);
        session.CheckNow();
        var panel = new DevToolsPanel(doc, session, shortcut ?? new KeyChord(EditKey.F12)) { IsOpen = true };
        Frame(doc);   // the panel tags the template with where each element was written: a rebuild
        return (doc, panel, session, ws);
    }

    private static SKBitmap Draw(CupriDocument app, DevToolsPanel panel, int appW = 600, int h = 400)
    {
        var bmp = new SKBitmap(appW + (int)panel.Width, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        app.Render(canvas, appW, h);
        panel.PaintHighlight(canvas);
        canvas.Save();
        canvas.Translate(appW, 0);
        panel.Render(canvas, panel.Width, h);
        canvas.Restore();
        canvas.Flush();
        return bmp;
    }

    private static void Settle(CupriDocument app, DevToolsPanel panel, int h = 400)
    {
        using var _ = Draw(app, panel, 600, h);   // the panel rebuilds its model on render, then lays out
        using var __ = Draw(app, panel, 600, h);
    }

    [Fact]
    public void Panel_Shortcut_TogglesIt_AndNothingElseDoes()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.IsOpen = false;

        Assert.False(panel.HandleKey(EditKey.F11, KeyMods.None));
        Assert.False(panel.HandleKey(EditKey.F12, KeyMods.Ctrl));   // modifiers must match exactly
        Assert.True(panel.HandleKey(EditKey.F12, KeyMods.None));
        Assert.True(panel.IsOpen);
        Assert.True(panel.HandleKey(EditKey.F12, KeyMods.None));
        Assert.False(panel.IsOpen);
    }

    [Fact]
    public void Panel_RemappedShortcut_LeavesF12ToTheApp()
    {
        var (app, panel, session, ws) = Inspect(shortcut: new KeyChord(EditKey.F12, KeyMods.Ctrl | KeyMods.Shift));
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.IsOpen = false;

        Assert.False(panel.HandleKey(EditKey.F12, KeyMods.None));
        Assert.True(panel.HandleKey(EditKey.F12, KeyMods.Ctrl | KeyMods.Shift));
        Assert.True(panel.IsOpen);

        panel.Shortcut = null;                 // an app that opts out entirely
        Assert.False(panel.HandleKey(EditKey.F12, KeyMods.Ctrl | KeyMods.Shift));
        Assert.True(panel.IsOpen);
    }

    [Fact]
    public void Panel_Picker_SelectsTheElementClicked_AndSwallowsTheClick()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        var count = FindClass(app.Root, "count")!;
        var box = HitTesting.AbsoluteBox(count);

        panel.Picking = true;
        Assert.True(panel.AppPointerMove(box.X + 4, box.Y + box.H / 2));
        Assert.Same(count, panel.HoveredNode);
        Assert.True(panel.AppPointerDown(box.X + 4, box.Y + box.H / 2));
        Assert.True(panel.AppPointerUp(box.X + 4, box.Y + box.H / 2));

        Assert.Same(count, panel.SelectedNode);
        Assert.False(panel.Picking);
        // Not picking any more: the app's pointer is its own again.
        Assert.False(panel.AppPointerDown(box.X + 4, box.Y + box.H / 2));
    }

    [Fact]
    public void Panel_ShowsTheSelectedElement_ItsRules_AndWhereItWasWritten()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.Select(FindClass(app.Root, "card"));
        Settle(app, panel);

        var ui = panel.Document.Root;
        Assert.Contains("div#main-card", TextOf(FindClass(ui, "dt-sel-name")!));
        var line = Html.Split('\n').ToList().FindIndex(l => l.Contains("main-card")) + 1;
        Assert.Contains($"app.html:{line}", TextOf(FindClass(ui, "dt-source-text")!));
        var details = TextOf(FindClass(ui, "dt-details")!);
        Assert.Contains(".card", details);                  // the matched rule…
        Assert.Contains("border-radius", details);           // …and its declarations
        Assert.Contains("padding", details);
        // The selected row is in the tree, and marked.
        Assert.NotNull(TestDoc.Find(ui, n => n.Element?.ClassList.Contains("sel") == true
                                            && TextOf(n).Contains("div#main-card")));
    }

    [Fact]
    public void Panel_ShowsTheBindings_OfABoundControl()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.Select(FindClass(app.Root, "name"));
        Settle(app, panel);

        Assert.Contains("{{Name}}", TextOf(FindClass(panel.Document.Root, "dt-details")!));
    }

    [Fact]
    public void Panel_ListsTheDoctorsFindings()
    {
        var (app, panel, session, ws) = Inspect(Html.Replace("{{Count}}", "{{Cuont}}"));
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        Settle(app, panel);

        var problems = TextOf(FindClass(panel.Document.Root, "dt-problems")!);
        Assert.Contains("CF0060", problems);
        Assert.Contains("app.html:", problems);
    }

    [Fact]
    public void Panel_TreeArrows_MoveTheSelection_OnlyWhenThePanelHasFocus()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        Settle(app, panel);

        Assert.False(panel.HandleKey(EditKey.Down, KeyMods.None));   // the app's arrows, still
        panel.PanelPointerDown(5, 5);
        Assert.True(panel.HandleKey(EditKey.Down, KeyMods.None));
        Settle(app, panel);
        Assert.NotNull(panel.SelectedNode);

        // Closed and reopened, the keyboard is the app's again.
        panel.IsOpen = false;
        panel.IsOpen = true;
        Assert.False(panel.HandleKey(EditKey.Down, KeyMods.None));
    }

    [Fact]
    public void Panel_PopOutButton_ShowsOnlyWhereTheHostCanPopOut_AndTogglesDetached()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        Settle(app, panel);
        Assert.Equal(0, FindClass(panel.Document.Root, "dt-dock")!.Width);   // headless: no window to pop into

        panel.CanDetach = true;
        Settle(app, panel);
        var dock = FindClass(panel.Document.Root, "dt-dock")!;
        Assert.Contains("Pop out", TextOf(dock));

        var box = HitTesting.AbsoluteBox(dock);
        panel.PanelPointerDown(box.X + box.W / 2, box.Y + box.H / 2);
        panel.PanelPointerUp(box.X + box.W / 2, box.Y + box.H / 2);
        Assert.True(panel.Detached);
        Settle(app, panel);
        Assert.Contains("Dock", TextOf(FindClass(panel.Document.Root, "dt-dock")!));

        // Closing keeps the choice, so the next F12 opens it the way it was left.
        panel.IsOpen = false;
        Assert.True(panel.Detached);
    }

    // ---- editing ------------------------------------------------------------------------------

    private static void ClickPanel(DevToolsPanel panel, string cls)
    {
        var n = FindClass(panel.Document.Root, cls) ?? throw new InvalidOperationException($"no .{cls} in the panel");
        var b = HitTesting.AbsoluteBox(n);
        Assert.True(b.W > 0, $".{cls} is not showing");
        // The click must land ON it — not on whatever is drawn there because it is scrolled away.
        var hit = HitTesting.HitTest(panel.Document.Root, b.X + b.W / 2, b.Y + b.H / 2);
        var on = false;
        for (var h = hit; h is not null && !on; h = h.Parent) on = ReferenceEquals(h, n);
        Assert.True(on, $".{cls} is not under the pointer at its own centre (scrolled out of view?)");
        panel.PanelPointerDown(b.X + b.W / 2, b.Y + b.H / 2);
        panel.PanelPointerUp(b.X + b.W / 2, b.Y + b.H / 2);
    }

    private static (string? Clip, Func<string?> Read, Action<string> Write) Clipboard(string? start = null)
    {
        var box = new[] { start };
        return (start, () => box[0], v => box[0] = v);
    }

    [Fact]
    public void Edit_Delete_RemovesTheElement_LiveAndOnUndoBringsItBack()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.Select(FindClass(app.Root, "count"));
        Settle(app, panel);

        ClickPanel(panel, "dt-delete");
        Frame(app);
        Assert.Null(FindClass(app.Root, "count"));
        Assert.DoesNotContain("class=\"count\"", app.TemplateHtml);
        Assert.Equal(1, panel.UnsavedEdits);
        Assert.Equal(Html, File.ReadAllText(ws.HtmlPath));          // live only: the file is untouched
        // The line it stood on went with it — no blank line left behind.
        Assert.DoesNotContain("\n\n", app.TemplateHtml);

        Settle(app, panel);
        ClickPanel(panel, "dt-undo");
        Frame(app);
        Assert.NotNull(FindClass(app.Root, "count"));
        Assert.Equal(Html, app.TemplateHtml);
        Assert.Equal(0, panel.UnsavedEdits);
    }

    [Fact]
    public void Edit_AsHtml_TypedAndApplied_ChangesTheApp_AndKeepsTheModel()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.Select(FindClass(app.Root, "count"));
        Settle(app, panel);

        ClickPanel(panel, "dt-edit");
        Settle(app, panel);                                          // the editor opens, and takes the caret
        var (_, read, write) = Clipboard();
        Assert.True(panel.PanelShortcut('a', KeyMods.Ctrl, read, write));
        Assert.True(panel.PanelText("<div class=\"count total\"><b>{{Count}}</b> in all</div>"));
        Assert.True(panel.PanelShortcut('s', KeyMods.Ctrl, read, write));   // Ctrl+S applies
        Frame(app);

        Assert.Contains("3 in all", TextOf(app.Root));
        Assert.NotNull(FindClass(app.Root, "total"));
        Assert.Contains("<div class=\"count total\"><b>{{Count}}</b> in all</div>", app.TemplateHtml);
    }

    [Fact]
    public void Edit_CopyThenPaste_DuplicatesTheElement_AfterItself()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.Select(FindClass(app.Root, "count"));
        Settle(app, panel);
        panel.PanelPointerDown(5, 5);                                // the panel has the keyboard
        var (_, read, write) = Clipboard();

        Assert.True(panel.PanelShortcut('c', KeyMods.Ctrl, read, write));
        Assert.Equal("<div class=\"count\">{{Count}} items</div>", read());
        panel.Select(FindClass(app.Root, "count"));
        Assert.True(panel.PanelShortcut('v', KeyMods.Ctrl, read, write));
        Frame(app);

        var counts = new List<RenderNode>();
        void Walk(RenderNode n) { if (n.Element?.ClassList.Contains("count") == true) counts.Add(n); foreach (var c in n.Children) Walk(c); }
        Walk(app.Root);
        Assert.Equal(2, counts.Count);
        // Pasted on its own line, at the element's indentation.
        Assert.Contains("      <div class=\"count\">{{Count}} items</div>\n      <div class=\"count\">{{Count}} items</div>",
            app.TemplateHtml);
    }

    [Fact]
    public void Edit_CopyButton_PutsTheElementsHtmlOnTheClipboard()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        string? copied = null;
        panel.ClipboardWriteRequested += v => copied = v;
        panel.Select(FindClass(app.Root, "card"));
        Settle(app, panel);

        ClickPanel(panel, "dt-copy");

        // The whole element, reindented to column 0, children and all.
        Assert.StartsWith("<div class=\"card\" id=\"main-card\">\n  <cupri-textfield", copied);
        Assert.EndsWith("</div>\n</div>", copied);
    }

    [Fact]
    public void Edit_InsideAComponent_EditsTheComponentsOwnTag()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        // The text field's insides are generated by the component; the markup has one tag.
        var inner = TestDoc.Find(FindClass(app.Root, "name")!, n => n.Element?.GetAttribute("role") == "textbox")!;
        panel.Select(inner);
        Settle(app, panel);

        Assert.Contains("<cupri-textfield>", TextOf(FindClass(panel.Document.Root, "dt-edit-target")!));
        ClickPanel(panel, "dt-delete");
        Frame(app);
        Assert.DoesNotContain("cupri-textfield", app.TemplateHtml);
    }

    [Fact]
    public void Edit_InsertInside_AddsALastChild()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.Select(FindClass(app.Root, "card"));
        Settle(app, panel);

        ClickPanel(panel, "dt-ins-inside");
        Settle(app, panel);
        var (_, read, write) = Clipboard();
        panel.PanelText("<p class=\"note\">Added</p>");
        // Ctrl+S, not the Apply button: at this panel's height the button is below the fold.
        panel.PanelShortcut('s', KeyMods.Ctrl, read, write);
        Frame(app);

        var card = FindClass(app.Root, "card")!;
        Assert.Contains("note", card.Children[^1].Element!.ClassName);
        // One level deeper than the card (4 spaces), above its own end tag.
        Assert.Contains("      <p class=\"note\">Added</p>\n    </div>", app.TemplateHtml);
    }

    [Fact]
    public void Edit_SaveToFile_WritesTheEditedMarkup_AndHotReloadFindsNothingToDo()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.Select(FindClass(app.Root, "count"));
        Settle(app, panel);
        ClickPanel(panel, "dt-delete");
        Settle(app, panel);

        ClickPanel(panel, "dt-save");

        Assert.Equal(app.TemplateHtml, File.ReadAllText(ws.HtmlPath));
        Assert.Equal(0, panel.UnsavedEdits);
        session.ReloadNow();                                          // what the save's file event causes
        Assert.Null(session.LastError);
        Assert.Null(FindClass(app.Root, "count"));
    }

    [Fact]
    public void Edit_SaveToFile_Refuses_WhenTheFileChangedUnderTheEdits()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.Select(FindClass(app.Root, "count"));
        Settle(app, panel);
        ClickPanel(panel, "dt-delete");
        Settle(app, panel);
        var theirs = Html.Replace("Hello", "Changed elsewhere");
        File.WriteAllText(ws.HtmlPath, theirs);

        ClickPanel(panel, "dt-save");
        Settle(app, panel);

        Assert.Equal(theirs, File.ReadAllText(ws.HtmlPath));          // their work is intact
        Assert.Contains("Not saved", TextOf(FindClass(panel.Document.Root, "dt-edits-note")!));
    }

    [Fact]
    public void Edit_ARepeatedRow_EditsEveryCopy()
    {
        const string list = """
<body>
  <div class="list">
    <div class="row" data-repeat="Items">{{Name}}</div>
  </div>
</body>
""";
        var (app, panel, session, ws) = Inspect(list);
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        app.Bind(new { Items = new[] { new { Name = "a" }, new { Name = "b" }, new { Name = "c" } } });
        Frame(app);
        panel.Select(FindClass(app.Root, "row"));
        Settle(app, panel);
        ClickPanel(panel, "dt-edit");
        Settle(app, panel);
        var (_, read, write) = Clipboard();
        panel.PanelShortcut('a', KeyMods.Ctrl, read, write);
        panel.PanelText("<div class=\"row\" data-repeat=\"Items\">• {{Name}}</div>");
        panel.PanelShortcut('s', KeyMods.Ctrl, read, write);
        Frame(app);

        Assert.Contains("• a", TextOf(app.Root));
        Assert.Contains("• c", TextOf(app.Root));
    }

    [Fact]
    public void Edit_KeepsTheFilesLineEndings()
    {
        var crlf = Html.Replace("\n", "\r\n");
        var (app, panel, session, ws) = Inspect(crlf);
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.Select(FindClass(app.Root, "count"));
        Settle(app, panel);
        panel.PanelPointerDown(5, 5);
        var (_, read, write) = Clipboard("<p>pasted</p>");
        panel.PanelShortcut('v', KeyMods.Ctrl, read, write);

        var html = app.TemplateHtml;
        Assert.Contains("<p>pasted</p>", html);
        Assert.DoesNotMatch("[^\r]\n", html);
    }

    // ---- two engine bugs the HTML editor found ----------------------------------------------------

    private sealed class FieldModel { public string Text { get; set; } = "hello"; }

    [Fact]
    public void Focus_FromAButtonsClick_MovesKeyboardFocusOffTheButton()
    {
        // "Edit" button → handler focuses a field. The button used to stay marked focused beside
        // the field, and was drawn as an empty field: its label vanished.
        var model = new FieldModel();
        using var t = new TestDoc("<body><div class='go'>Edit</div><cupri-textfield class='f' value='{{Text}}'></cupri-textfield></body>",
            ".go{padding:6px}", model, 400, 200, components: true);
        t.Doc.OnClick(".go", _ => t.Doc.Focus("[data-bind-value='Text']"));
        var go = HitTesting.AbsoluteBox(t.FindClass("go"));
        t.Doc.DispatchClick(go.X + 4, go.Y + 4);
        t.Layout();

        Assert.False(t.FindClass("go").Element!.HasAttribute("data-focus"));
        t.Doc.DispatchKey("!", EditKey.None);
        t.Doc.Blur();
        Assert.Equal("hello!", model.Text);
    }

    private sealed class AreaModel { public string Text { get; set; } = "first line\nsecond line\nthird"; }

    private static void ClickOnLine(TestDoc t, int line, float dx)
    {
        var lines = new List<RenderNode>();
        void Walk(RenderNode n) { if (n.Element?.ClassList.Contains("cupri-ta-line") == true) lines.Add(n); foreach (var c in n.Children) Walk(c); }
        Walk(t.Root);
        var b = HitTesting.AbsoluteBox(lines[line]);
        t.Doc.DispatchClick(b.X + dx, b.Y + b.H / 2);
        t.Doc.DispatchPointerUp(b.X + dx, b.Y + b.H / 2);
        t.Layout();
    }

    [Fact]
    public void Click_InAFieldFocusedFromCode_PlacesTheCaretWhereItLands()
    {
        // Focus(selector) leaves the edit buffer empty, meaning "read the model". The click path read
        // the buffer alone, so every click put the caret at 0 until a key had been typed.
        var model = new AreaModel();
        using var t = new TestDoc("<body><cupri-textarea class='ta' value='{{Text}}'></cupri-textarea></body>",
            "body{margin:0} .ta{width:300px;height:200px}", model, 400, 300, components: true);
        t.Doc.Focus("[data-bind-value='Text']");
        t.Layout();

        ClickOnLine(t, 1, 20);
        t.Doc.DispatchKey("X", EditKey.None);
        t.Doc.Blur();

        Assert.Equal("first line\nsecXond line\nthird", model.Text);
    }

    [Fact]
    public void Focus_FromCode_DrawsTheFieldFocused_AndItsCaret()
    {
        // Focus(selector) set the caret but never rebuilt, and only a rebuild marks a field as the
        // focused one — which is what the caret, the selection and :focus are drawn from. A click on
        // it changed nothing either, so the field showed no caret until a key was typed.
        var model = new AreaModel();
        using var t = new TestDoc("<body><cupri-textarea class='ta' value='{{Text}}'></cupri-textarea></body>",
            "body{margin:0} .ta{width:300px;height:200px}", model, 400, 300, components: true);
        int CaretPixels()
        {
            using var bmp = t.Render(SKColors.White);
            // The whole text body: the caret lands at the END of the text, on its last line.
            var body = HitTesting.AbsoluteBox(TestDoc.Find(t.Root, n => n.Element?.ClassList.Contains("cupri-ta-body") == true)!);
            var n = 0;
            for (var y = (int)body.Y; y < body.Y + body.H; y++)
                for (var x = 0; x < bmp.Width; x++) { var c = bmp.GetPixel(x, y); if (c.Red < 80 && c.Green < 80 && c.Blue < 80) n++; }
            return n;
        }
        var unfocused = CaretPixels();

        t.Doc.Focus("[data-bind-value='Text']");
        t.Layout();

        Assert.True(t.FindClass("ta").Element!.HasAttribute("data-focus"));
        Assert.True(CaretPixels() > unfocused, "a caret should be drawn without anything being typed");
    }

    [Fact]
    public void CopySelection_InAFieldFocusedFromCode_CopiesTheSelectedText()
    {
        var model = new AreaModel();
        using var t = new TestDoc("<body><cupri-textarea class='ta' value='{{Text}}'></cupri-textarea></body>",
            "body{margin:0} .ta{width:300px;height:200px}", model, 400, 300, components: true);
        t.Doc.Focus("[data-bind-value='Text']");
        t.Doc.DispatchKey(null, EditKey.SelectAll);

        Assert.Equal(model.Text, t.Doc.CopySelection());
    }

    [Fact]
    public void Edit_ClickInTheHtmlEditor_PlacesTheCaret_BeforeAnythingIsTyped()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.Select(FindClass(app.Root, "count"));
        Settle(app, panel, 900);                  // tall enough that the editor is on screen
        ClickPanel(panel, "dt-edit");
        Settle(app, panel, 900);

        // Click part-way along the editor's one line, then type. Mid-line on purpose: the bug put the
        // caret at 0, so a click near the start would pass with or without the fix.
        var line = TestDoc.Find(panel.Document.Root, n => n.Element?.ClassList.Contains("cupri-ta-line") == true)!;
        var b = HitTesting.AbsoluteBox(line);
        panel.PanelPointerDown(b.X + 60, b.Y + b.H / 2);
        panel.PanelPointerUp(b.X + 60, b.Y + b.H / 2);
        panel.PanelText("X");
        Settle(app, panel, 900);

        var text = TextOf(TestDoc.Find(panel.Document.Root, n => n.Element?.ClassList.Contains("cupri-ta-line") == true)!).Trim();
        var at = text.IndexOf('X');
        Assert.True(at > 3 && at < text.Length - 4, $"X should land where the click was, mid-line: \"{text}\"");
    }

    [Fact]
    public void Textarea_ShowsTheSpacesThatWereTyped()
    {
        float LineWidth(string value)
        {
            using var t = new TestDoc($"<body><cupri-textarea value='{value}'></cupri-textarea></body>", "", null, 400, 200, components: true);
            var lines = new List<RenderNode>();
            void Walk(RenderNode n) { if (n.Element?.ClassList.Contains("cupri-ta-line") == true) lines.Add(n); foreach (var c in n.Children) Walk(c); }
            Walk(t.Root);
            var text = TestDoc.Find(lines[^1], n => n.IsText)!;
            return text.Lines?.Sum(l => l.Width) ?? text.Width;
        }
        // Leading spaces are indentation, and a textarea shows them (pre-wrap, as a browser's does).
        Assert.True(LineWidth("a\n    b") > LineWidth("a\nb") + 10);
    }

    // ---- the Performance tab ----------------------------------------------------------------------

    [Fact]
    public void Performance_RecordsOnlyWhileItsTabIsShowing()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        Assert.False(panel.RecordingPerformance);

        panel.ShowingPerformance = true;
        Assert.True(panel.RecordingPerformance);

        panel.IsOpen = false;                 // closed: nobody is looking, so nothing is recorded
        Assert.False(panel.RecordingPerformance);
        panel.IsOpen = true;
        Assert.True(panel.RecordingPerformance);
        panel.ShowingPerformance = false;
        Assert.False(panel.RecordingPerformance);
    }

    [Fact]
    public void Performance_ShowsTheAppsFrames_AndWhereTheLastRebuildWent()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        Settle(app, panel, 700);
        ClickPanel(panel, "dt-tab-perf");
        Assert.True(panel.ShowingPerformance);

        // The app does some work: rebuilds and frames, as interaction would cause.
        for (var i = 0; i < 6; i++) { app.Refresh(); Frame(app); }
        Thread.Sleep(300);                    // past the panel's quarter-second refresh limit
        panel.ConsumeDirty();
        Settle(app, panel, 700);

        var ui = panel.Document.Root;
        var bars = 0;
        void Count(RenderNode n) { if (n.Element?.ClassList.Contains("dt-col") == true) bars++; foreach (var c in n.Children) Count(c); }
        Count(ui);
        Assert.True(bars >= 6, $"a bar per frame, got {bars}");
        var perf = TextOf(FindClass(ui, "dt-perf")!);
        Assert.Contains("frame time", perf);
        Assert.Contains("Last rebuild", perf);
        Assert.Contains("parse-html", perf);           // a rebuild stage, by name
        // The Elements view is hidden — display:none builds no boxes at all, so the tree is not there.
        Assert.True(FindClass(ui, "dt-tree") is null or { Width: 0 });
    }

    [Fact]
    public void Render_ReportsItsFrameTimings()
    {
        // The full-frame path — the GL window's — used to leave LastFrame untouched.
        using var t = new TestDoc(Html, Css, new Model(), 600, 400, components: true);
        var before = t.Doc.LastFrame;
        using var bmp = new SKBitmap(600, 400);
        using var canvas = new SKCanvas(bmp);
        t.Doc.Refresh();
        t.Doc.Render(canvas, 600, 400);
        Assert.NotEqual(before, t.Doc.LastFrame);
        Assert.True(t.Doc.LastFrame.TotalMs > 0);
    }

    [Fact]
    public void Panel_PerformanceTab_PassesTheDoctor_AndRenders()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.ShowingPerformance = true;
        for (var i = 0; i < 20; i++) { app.Refresh(); Frame(app); }
        Thread.Sleep(300);
        panel.ConsumeDirty();
        Settle(app, panel, 700);

        var report = CupriDoctor.Check(panel.Document, (int)panel.Width, 700);
        output.WriteLine(report.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Severity >= Severity.Warning);

        using var bmp = Draw(app, panel, 600, 700);
        var path = Path.Combine(Path.GetTempPath(), "cupri-devtools-perf.png");
        using (var f = File.Create(path)) bmp.Encode(f, SKEncodedImageFormat.Png, 95);
        output.WriteLine(path);
    }

    [Fact]
    public void Panel_ItsOwnMarkup_PassesTheDoctor()
    {
        var (app, panel, session, ws) = Inspect();
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.Select(FindClass(app.Root, "card"));
        Settle(app, panel);

        var report = CupriDoctor.Check(panel.Document, (int)panel.Width, 700);
        output.WriteLine(report.ToString());
        Assert.DoesNotContain(report.Findings, f => f.Severity >= Severity.Warning);
    }

    [Fact]
    public void Panel_Renders_BesideTheApp()
    {
        var (app, panel, session, ws) = Inspect(Html.Replace("{{Count}}", "{{Cuont}}"));
        using var _ = ws; using var __ = app; using var ___ = session; using var ____ = panel;
        panel.Select(FindClass(app.Root, "card"));
        Settle(app, panel);

        using var bmp = Draw(app, panel, 600, 700);
        var path = Path.Combine(Path.GetTempPath(), "cupri-devtools-panel.png");
        using (var f = File.Create(path)) bmp.Encode(f, SKEncodedImageFormat.Png, 95);
        output.WriteLine(path);
        // The highlight is drawn over the card (the content tint is blue), and the panel's
        // background fills its side.
        var card = HitTesting.AbsoluteBox(FindClass(app.Root, "card")!);
        var tint = bmp.GetPixel((int)(card.X + card.W / 2), (int)(card.Y + card.H - 14));
        Assert.True(tint.Blue > tint.Red, $"expected the blue content tint over the card, got {tint}");
        Assert.Equal(new SKColor(0x1e, 0x1f, 0x24), bmp.GetPixel(600 + (int)panel.Width - 3, 690));
    }
}
