using System.Collections.Concurrent;
using CupriFace.Components;
using CupriFace.Hosting;
using CupriFace.Interaction;
using CupriFace.Platform;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// File dialogs and second windows (<see cref="CupriDocument.FileDialogs"/>,
/// <see cref="CupriDocument.Windows"/>): everything about them that holds without an OS behind it.
///
/// <para>The OS half — a real IFileOpenDialog, a real SDL window — is verified by hand in a harness
/// (see RELEASE-NOTES), because it needs a desktop session. What is pinned here is the contract the
/// hosts implement: options are validated the same way everywhere, a result crosses back to the
/// document's thread and nowhere else, windows are book-kept and closed exactly once, a host without
/// either feature says so with null, and a second window's input reaches ITS document the way the
/// main window's reaches the main one.</para>
/// </summary>
public class DesktopIntegrationTests
{
    // ---- FileFilter --------------------------------------------------------------------------------

    [Theory]
    [InlineData("png")]
    [InlineData(".png")]
    [InlineData("*.png")]
    [InlineData(" png ")]
    public void Every_common_spelling_of_an_extension_is_the_bare_one(string spelling)
    {
        var f = new FileFilter("Images", spelling);
        Assert.Equal(["png"], f.Extensions);
        Assert.False(f.MatchesAll);
        Assert.Equal("*.png", f.ToWindowsPattern());
    }

    [Fact]
    public void Star_means_every_file_on_every_os()
    {
        Assert.True(new FileFilter("All", "*").MatchesAll);
        Assert.True(new FileFilter("All", "*.*").MatchesAll);
        Assert.Equal("*.*", FileFilter.AllFiles.ToWindowsPattern());
    }

    [Fact]
    public void Extensions_are_deduplicated_without_regard_to_case_and_keep_their_order()
    {
        var f = new FileFilter("Images", "png", "JPG", ".jpg", "*.PNG", "gif");
        Assert.Equal(["png", "JPG", "gif"], f.Extensions);
        Assert.Equal("*.png;*.JPG;*.gif", f.ToWindowsPattern());
    }

    [Theory]
    [InlineData("")]
    [InlineData("p*g")]
    [InlineData("png;jpg")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("*.")]
    [InlineData("..png")]
    [InlineData("tar gz")]
    public void Anything_that_is_not_an_extension_is_refused_when_the_filter_is_built(string bad) =>
        Assert.Throws<ArgumentException>(() => new FileFilter("Bad", bad));

    [Fact]
    public void A_filter_needs_a_name_and_at_least_one_extension()
    {
        Assert.Throws<ArgumentException>(() => new FileFilter(" ", "png"));
        Assert.Throws<ArgumentException>(() => new FileFilter("Images"));
    }

    // ---- FileDialogs: validation, one-at-a-time, and the thread a result arrives on -------------

    /// <summary>A host whose "OS dialog" is a completion source the test answers, from any thread.</summary>
    private sealed class FakeDialogs(Action<Action> post) : FileDialogs(post)
    {
        public TaskCompletionSource<string[]?> Open = new();
        public TaskCompletionSource<string?> Save = new();
        public TaskCompletionSource<string?> Folder = new();
        public OpenFileOptions? LastOpen;
        public SaveFileOptions? LastSave;
        public CancellationToken LastToken;

        protected override Task<string[]?> ShowOpenAsync(OpenFileOptions options, CancellationToken ct)
        {
            LastOpen = options;
            LastToken = ct;
            return Open.Task;
        }

        protected override Task<string?> ShowSaveAsync(SaveFileOptions options, CancellationToken ct)
        {
            LastSave = options;
            return Save.Task;
        }

        protected override Task<string?> ShowFolderAsync(PickFolderOptions options, CancellationToken ct) => Folder.Task;
    }

    /// <summary>The document's thread, reduced to a queue the test drains when it chooses.</summary>
    private sealed class Pump
    {
        private readonly ConcurrentQueue<Action> _work = new();
        public void Post(Action a) => _work.Enqueue(a);
        public int Drain()
        {
            var n = 0;
            while (_work.TryDequeue(out var a)) { a(); n++; }
            return n;
        }
    }

    private static string AbsoluteDir() => Path.GetTempPath();

    /// <summary>
    /// Start app code the way a desktop host runs it: on a thread with NO SynchronizationContext.
    /// xUnit installs one of its own, and an await that captures it is resumed through it rather than
    /// inline — which is not what happens on a host's render thread, and would hide the property
    /// these tests are about (the code after the await runs inside the frame that delivered it).
    /// </summary>
    private static Task AsHost(Func<Task> appCode)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try { return appCode(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public async Task A_result_waits_for_the_documents_thread_and_the_awaiting_code_runs_there()
    {
        var pump = new Pump();
        var dialogs = new FakeDialogs(pump.Post);
        var ranOn = -1;
        string? got = null;

        async Task App()
        {
            got = await dialogs.SaveFileAsync(new SaveFileOptions { DefaultName = "report.csv" });
            ranOn = Environment.CurrentManagedThreadId;
        }
        var app = AsHost(App);

        // The OS answers on some other thread…
        await Task.Run(() => dialogs.Save.SetResult(@"C:\out\report.csv"));
        await Task.Delay(20);
        // …and nothing has happened yet: the answer is waiting for the document's thread.
        Assert.False(app.IsCompleted);
        Assert.True(dialogs.IsShowing);

        // The "frame": the code after the await runs HERE, inline, on the draining thread.
        var me = Environment.CurrentManagedThreadId;
        Assert.Equal(1, pump.Drain());
        Assert.True(app.IsCompleted);
        Assert.Equal(me, ranOn);
        Assert.Equal(@"C:\out\report.csv", got);
        Assert.False(dialogs.IsShowing);
    }

    [Fact]
    public async Task Cancelled_by_the_user_is_null_not_an_exception()
    {
        var pump = new Pump();
        var dialogs = new FakeDialogs(pump.Post);
        var open = dialogs.OpenFilesAsync();
        dialogs.Open.SetResult(null);
        await Task.Delay(10);
        pump.Drain();
        Assert.Null(await open);

        var folder = dialogs.PickFolderAsync();
        dialogs.Folder.SetResult(null);
        await Task.Delay(10);
        pump.Drain();
        Assert.Null(await folder);
    }

    [Fact]
    public async Task A_token_that_fires_cancels_the_task_rather_than_answering_null()
    {
        var pump = new Pump();
        var dialogs = new FakeDialogs(pump.Post);
        using var cts = new CancellationTokenSource();
        var open = dialogs.OpenFilesAsync(null, cts.Token);
        Assert.True(dialogs.LastToken.CanBeCanceled);   // the host is handed the token, to close its dialog
        cts.Cancel();
        dialogs.Open.SetResult(null);                    // what a host's closed dialog reports
        await Task.Delay(10);
        pump.Drain();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
    }

    [Fact]
    public async Task A_dialog_the_os_could_not_show_faults_with_the_reason()
    {
        var pump = new Pump();
        var dialogs = new FakeDialogs(pump.Post);
        var open = dialogs.OpenFilesAsync();
        dialogs.Open.SetException(new InvalidOperationException("no shell"));
        await Task.Delay(10);
        pump.Drain();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => open);
        Assert.Equal("no shell", ex.Message);
        Assert.False(dialogs.IsShowing);
    }

    [Fact]
    public async Task One_dialog_at_a_time_and_the_next_may_follow_once_it_has_answered()
    {
        var pump = new Pump();
        var dialogs = new FakeDialogs(pump.Post);
        var first = dialogs.OpenFilesAsync();
        Assert.Throws<InvalidOperationException>(() => { _ = dialogs.SaveFileAsync(); });

        dialogs.Open.SetResult(["a.txt"]);
        await Task.Delay(10);
        pump.Drain();
        Assert.Equal(["a.txt"], (await first)!);

        var second = dialogs.SaveFileAsync();   // no longer refused
        dialogs.Save.SetResult("b.txt");
        await Task.Delay(10);
        pump.Drain();
        Assert.Equal("b.txt", await second);
    }

    [Fact]
    public void Options_are_validated_before_anything_is_shown()
    {
        var dialogs = new FakeDialogs(new Pump().Post);
        Assert.Throws<ArgumentException>(() => { _ = dialogs.OpenFilesAsync(new OpenFileOptions { InitialDirectory = "relative/dir" }); });
        Assert.Throws<ArgumentException>(() => { _ = dialogs.OpenFilesAsync(new OpenFileOptions { Filters = null! }); });
        Assert.Throws<ArgumentException>(() => { _ = dialogs.SaveFileAsync(new SaveFileOptions { DefaultName = "sub/report.csv" }); });
        Assert.Throws<ArgumentException>(() => { _ = dialogs.SaveFileAsync(new SaveFileOptions { DefaultName = "" }); });
        Assert.Throws<ArgumentException>(() => { _ = dialogs.PickFolderAsync(new PickFolderOptions { InitialDirectory = "" }); });
        Assert.False(dialogs.IsShowing);   // a refused request leaves nothing open
        Assert.Null(dialogs.LastOpen);
    }

    [Fact]
    public async Task OpenFileAsync_asks_for_one_file_and_answers_the_first()
    {
        var pump = new Pump();
        var dialogs = new FakeDialogs(pump.Post);
        var one = dialogs.OpenFileAsync(new OpenFileOptions
        {
            AllowMultiple = true,   // overridden: this is the single-file call
            Filters = [new FileFilter("Text", "txt")],
            InitialDirectory = AbsoluteDir(),
        });
        Assert.False(dialogs.LastOpen!.AllowMultiple);
        Assert.Equal("txt", dialogs.LastOpen.Filters[0].Extensions[0]);
        dialogs.Open.SetResult(["first.txt"]);
        await Task.Delay(10);
        pump.Drain();
        Assert.Equal("first.txt", await one);
    }

    /// <summary>The real seam end to end: a host posts through <see cref="CupriDocument.Post"/>, and
    /// a frame of the document is what delivers the answer — after which the model the code after
    /// the await changed is what that frame shows.</summary>
    [Fact]
    public async Task Through_a_real_document_the_next_frame_delivers_the_result()
    {
        var model = new PathModel();
        using var doc = CupriDocument.Load("<body><p class='p'>{{Path}}</p></body>", "").Bind(model);
        var dialogs = new FakeDialogs(doc.Post);
        doc.FileDialogs = dialogs;
        using (doc.RenderToImage(300, 80)) { }

        async Task OpenClicked()
        {
            if (await doc.FileDialogs!.OpenFileAsync() is { } path)
            {
                model.Path = path;
                doc.Refresh();
            }
        }
        var app = AsHost(OpenClicked);
        await Task.Run(() => dialogs.Open.SetResult(["/home/ada/notes.md"]));
        await Task.Delay(20);
        Assert.False(app.IsCompleted);
        Assert.True(doc.HasActiveAnimations);          // a pending post wakes a render-on-demand host

        using (doc.RenderToImage(300, 80)) { }          // the frame drains the post
        Assert.True(app.IsCompleted);
        Assert.Equal("/home/ada/notes.md", model.Path);
        Assert.Contains("notes.md", doc.DumpTree());
    }

    /// <summary>
    /// The SDL software window draws through <see cref="CupriDocument.RenderIncremental"/>, whose fast
    /// path used to return before the posted-work queue was drained — so on an otherwise idle page a
    /// posted result (a file dialog's answer) never ran, and the host was woken for it on every tick
    /// for ever. Found by the real-window harness: the first dialog result simply never arrived.
    /// </summary>
    [Fact]
    public void Posted_work_runs_on_an_incremental_frame_even_when_nothing_else_changed()
    {
        using var doc = CupriDocument.Load("<body><p>idle</p></body>", "");
        using var bmp = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bmp);
        doc.RenderIncremental(canvas, 200, 100, SKColors.White);
        doc.RenderIncremental(canvas, 200, 100, SKColors.White);   // clean now: the fast path

        var ran = false;
        doc.Post(() => ran = true);
        Assert.True(doc.HasActiveAnimations);                        // the host is woken…
        doc.RenderIncremental(canvas, 200, 100, SKColors.White);
        Assert.True(ran);                                            // …and the frame runs the work
        Assert.False(doc.HasActiveAnimations);                       // so it is not woken again
    }

    public sealed class PathModel
    {
        public string Path { get; set; } = "";
    }

    // ---- "not here": hosts without either feature ------------------------------------------------

    [Fact]
    public void A_document_with_no_host_offers_neither_dialogs_nor_windows()
    {
        using var doc = CupriDocument.Load("<body></body>", "");
        Assert.Null(doc.FileDialogs);
        Assert.Null(doc.Windows);

        using var fromApp = new Demo.ShowcaseApp().CreateDocument();
        Assert.Null(fromApp.FileDialogs);
        Assert.Null(fromApp.Windows);
    }

    // ---- AppWindows: book-keeping, close-once, and the thread rule ---------------------------------

    private sealed class FakeWindow(CupriDocument doc, string title) : AppWindow(doc, title)
    {
        public int CloseCalls;
        public string? NativeTitle = title;
        protected override void CloseCore() => CloseCalls++;
        protected override void SetTitleCore(string t) => NativeTitle = t;
        public void HostClosed(WindowCloseReason why) => NotifyClosed(why);
    }

    private sealed class FakeWindows(CupriDocument main) : AppWindows(main)
    {
        public bool Refuse;
        public CupriApp? LastApp;
        public WindowOptions? LastOptions;
        protected override AppWindow? OpenCore(CupriDocument document, WindowOptions options, CupriApp? app)
        {
            LastApp = app;
            LastOptions = options;
            return Refuse ? null : new FakeWindow(document, options.Title);
        }
    }

    [Fact]
    public void A_window_closes_once_whatever_asks_and_says_why()
    {
        using var main = CupriDocument.Load("<body></body>", "");
        using var second = CupriDocument.Load("<body></body>", "");
        var windows = new FakeWindows(main);
        var w = (FakeWindow)windows.Open(second, new WindowOptions { Title = "Second" })!;
        Assert.True(w.IsOpen);
        Assert.Same(second, w.Document);
        Assert.Single(windows.OpenWindows);

        var reasons = new List<WindowCloseReason>();
        w.Closed += reasons.Add;
        w.Close();
        w.Close();                                  // a second Close is nothing
        Assert.False(w.IsOpen);
        Assert.Equal(1, w.CloseCalls);
        Assert.Empty(reasons);                      // …until the host has actually destroyed it

        w.HostClosed(WindowCloseReason.App);
        w.HostClosed(WindowCloseReason.Host);       // and a late shutdown does not raise it again
        Assert.Equal([WindowCloseReason.App], reasons);
        Assert.Empty(windows.OpenWindows);
    }

    [Fact]
    public void The_user_closing_it_is_reported_as_the_user()
    {
        using var main = CupriDocument.Load("<body></body>", "");
        using var second = CupriDocument.Load("<body></body>", "");
        var windows = new FakeWindows(main);
        var w = (FakeWindow)windows.Open(second)!;
        WindowCloseReason? why = null;
        w.Closed += r => why = r;
        w.HostClosed(WindowCloseReason.User);
        Assert.Equal(WindowCloseReason.User, why);
        Assert.False(w.IsOpen);
        Assert.Equal(0, w.CloseCalls);              // the host closed it; nobody asked the host to
    }

    [Fact]
    public void A_document_is_in_one_window_at_a_time_and_may_open_again_once_that_closes()
    {
        using var main = CupriDocument.Load("<body></body>", "");
        using var second = CupriDocument.Load("<body></body>", "");
        var windows = new FakeWindows(main);
        Assert.Throws<InvalidOperationException>(() => windows.Open(main));   // the main window has it

        var w = (FakeWindow)windows.Open(second)!;
        Assert.Throws<InvalidOperationException>(() => windows.Open(second));

        w.HostClosed(WindowCloseReason.User);
        Assert.NotNull(windows.Open(second));
    }

    [Fact]
    public void Retitling_reaches_the_native_window_only_while_it_is_open()
    {
        using var main = CupriDocument.Load("<body></body>", "");
        using var second = CupriDocument.Load("<body></body>", "");
        var w = (FakeWindow)new FakeWindows(main).Open(second, new WindowOptions { Title = "One" })!;
        w.Title = "Two";
        Assert.Equal("Two", w.NativeTitle);
        w.Close();
        w.Title = "Three";
        Assert.Equal("Two", w.NativeTitle);
        Assert.Equal("Three", w.Title);
    }

    [Fact]
    public void A_refused_window_is_null_and_not_tracked()
    {
        using var main = CupriDocument.Load("<body></body>", "");
        using var second = CupriDocument.Load("<body></body>", "");
        var windows = new FakeWindows(main) { Refuse = true };
        Assert.Null(windows.Open(second));
        Assert.Empty(windows.OpenWindows);
        windows.Refuse = false;
        Assert.NotNull(windows.Open(second));      // the refused attempt did not claim the document
    }

    [Fact]
    public void Opening_an_app_uses_its_own_title_size_background_and_present()
    {
        using var main = CupriDocument.Load("<body></body>", "");
        var windows = new FakeWindows(main);
        var app = new Demo.SettingsApp();
        var w = windows.Open(app)!;
        Assert.Same(app, windows.LastApp);          // so the host lays it out with app.Present
        Assert.Equal(app.Title, windows.LastOptions!.Title);
        Assert.Equal(app.Width, windows.LastOptions.Width);
        Assert.Equal(app.Height, windows.LastOptions.Height);
        Assert.Equal(app.Background, windows.LastOptions.Background);
        Assert.NotSame(main, w.Document);
        w.Document.Dispose();
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, -1)]
    [InlineData(WindowOptions.MaxSize + 1, 100)]
    public void A_window_has_a_sane_size(int w, int h)
    {
        using var main = CupriDocument.Load("<body></body>", "");
        using var second = CupriDocument.Load("<body></body>", "");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FakeWindows(main).Open(second, new WindowOptions { Width = w, Height = h }));
    }

    [Fact]
    public async Task Windows_open_on_the_hosts_thread_only()
    {
        using var main = CupriDocument.Load("<body></body>", "");
        using var second = CupriDocument.Load("<body></body>", "");
        var windows = new FakeWindows(main);
        var ex = await Task.Run(() => Record.Exception(() => windows.Open(second)));
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("doc.Post", ex!.Message);
    }

    // ---- a second window's input reaches ITS document ----------------------------------------------

    private const string WindowHtml = """
        <body>
          <cupri-button class="go">Go</cupri-button>
          <cupri-textfield class="field" value="{{Text}}"></cupri-textfield>
          <p>{{Clicks}}</p>
        </body>
        """;

    private const string WindowCss = "body { margin:0; padding:10px; font-family:sans-serif; display:flex; flex-direction:column; gap:10px; }";

    public sealed class WindowModel
    {
        public string Text { get; set; } = "hello world";
        public int Clicks { get; set; }
    }

    private sealed class Clipboard
    {
        public string? Text;
    }

    private static (CupriDocument Doc, WindowModel Model, DocumentWindowContent Content, Clipboard Clip) Window(
        float deviceScale = 1f, CupriApp? app = null)
    {
        var model = new WindowModel();
        var doc = CupriDocument.Load(WindowHtml, WindowCss).UseComponents(ComponentRegistry.Default()).Bind(model);
        doc.OnClick(".go", _ => model.Clicks++);
        var clip = new Clipboard();
        var content = new DocumentWindowContent(doc, app, SKColors.White, () => clip.Text, v => clip.Text = v);
        Frame(content, deviceScale);
        return (doc, model, content, clip);
    }

    // A frame the way the native window draws one: device pixels = logical size x the monitor's scale.
    private static void Frame(DocumentWindowContent content, float deviceScale = 1f)
    {
        var (w, h) = ((int)(320 * deviceScale), (int)(200 * deviceScale));
        using var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        content.Render(canvas, w, h, deviceScale, now: 0);
    }

    private static (float X, float Y) CenterOf(CupriDocument doc, string cls)
    {
        var node = TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains(cls) == true)!;
        return TestDoc.Center(node);
    }

    [Fact]
    public void A_click_in_the_window_runs_the_handler_of_its_own_document()
    {
        var (doc, model, content, _) = Window();
        using var _d = doc;
        using var _c = content;
        var (x, y) = CenterOf(doc, "go");
        Assert.True(content.PointerDown(x, y, 1));
        content.PointerUp(x, y);
        Frame(content);
        Assert.Equal(1, model.Clicks);
        Assert.True(content.NeedsRender(0));        // the click marked a frame due…
        Assert.False(content.NeedsRender(0));       // …once
    }

    [Fact]
    public void Typing_and_the_clipboard_chords_reach_the_focused_field()
    {
        var (doc, model, content, clip) = Window();
        using var _d = doc;
        using var _c = content;
        var (x, y) = CenterOf(doc, "field");
        content.PointerDown(x, y, 1);
        content.PointerUp(x, y);
        Frame(content);

        content.Shortcut('a', KeyMods.Ctrl);
        content.Shortcut('c', KeyMods.Ctrl);
        Assert.Equal("hello world", clip.Text);      // Ctrl+A, Ctrl+C → this window's clipboard seam

        content.Key(EditKey.End, KeyMods.None);
        content.Text("!");
        Frame(content);
        Assert.Equal("hello world!", model.Text);

        clip.Text = " pasted";
        content.Shortcut('v', KeyMods.Ctrl);
        Frame(content);
        Assert.Equal("hello world! pasted", model.Text);
    }

    [Fact]
    public void A_copy_button_and_the_context_menu_write_through_the_windows_clipboard()
    {
        var (doc, _, content, clip) = Window();
        using var _d = doc;
        using var _c = content;
        var (x, y) = CenterOf(doc, "field");
        content.PointerDown(x, y, 1);
        content.PointerUp(x, y);
        content.Shortcut('a', KeyMods.Ctrl);
        doc.RequestContextCommand(ContextCommand.Copy);
        Assert.Equal("hello world", clip.Text);

        content.Dispose();                           // a closed window lets go of the document
        clip.Text = null;
        doc.RequestContextCommand(ContextCommand.Copy);
        Assert.Null(clip.Text);
    }

    [Fact]
    public void Ctrl_wheel_zooms_this_document_and_a_plain_wheel_does_not()
    {
        var (doc, _, content, _) = Window();
        using var _d = doc;
        using var _c = content;
        var before = doc.Zoom;
        content.Wheel(50, 50, 1, KeyMods.None);
        Assert.Equal(before, doc.Zoom);
        Assert.True(content.Wheel(50, 50, 1, KeyMods.Ctrl));
        Assert.True(doc.Zoom > before);
    }

    /// <summary>On a 200% monitor the window hands over LOGICAL units (it divided its pixels by the
    /// monitor's scale), so the same logical point must hit the same control — the scale must not be
    /// divided out twice.</summary>
    [Fact]
    public void On_a_scaled_monitor_logical_coordinates_still_hit_the_control()
    {
        var (doc, model, content, _) = Window(deviceScale: 2f);
        using var _d = doc;
        using var _c = content;
        var (x, y) = CenterOf(doc, "go");
        content.PointerDown(x, y, 1);
        content.PointerUp(x, y);
        Assert.Equal(1, model.Clicks);
    }

    /// <summary>A window opened for a CupriApp is laid out by that app's Present — here a fixed
    /// design scaled 2x into the window — and pointer input divides the present scale back out.</summary>
    [Fact]
    public void An_apps_present_scale_is_applied_and_divided_back_out_of_the_pointer()
    {
        var (doc, model, content, _) = Window(app: new ZoomedApp());
        using var _d = doc;
        using var _c = content;
        Assert.Equal(2f, content.PresentScale);
        var (x, y) = CenterOf(doc, "go");                       // document units
        content.PointerDown(x * 2, y * 2, 1);                    // the window's units are 2x those
        content.PointerUp(x * 2, y * 2);
        Assert.Equal(1, model.Clicks);
    }

    private sealed class ZoomedApp : CupriApp
    {
        public override string Html => "<body></body>";
        public override PresentInfo Present(float w, float h) => PresentInfo.Zoom(w, h, 2f);
    }

    [Fact]
    public void Work_posted_to_the_windows_document_makes_a_frame_due()
    {
        var (doc, model, content, _) = Window();
        using var _d = doc;
        using var _c = content;
        content.NeedsRender(0);
        Assert.False(content.NeedsRender(0));
        doc.Post(() => model.Clicks = 42);
        Assert.True(content.NeedsRender(0));        // a dialog result for THIS window arrives this way
        Frame(content);                              // …and a frame runs it
        Assert.Equal(42, model.Clicks);
    }

    [Fact]
    public void A_drop_on_the_window_is_its_documents()
    {
        var (doc, _, content, _) = Window();
        using var _d = doc;
        using var _c = content;
        IReadOnlyList<DroppedFile>? got = null;
        doc.OnFileDrop(e => got = e.Files);
        var file = Path.GetTempFileName();
        try
        {
            content.FilesDropped(20, 20, [file]);
            Assert.NotNull(got);
            Assert.Equal(Path.GetFileName(file), got![0].Name);
        }
        finally { File.Delete(file); }
    }
}
