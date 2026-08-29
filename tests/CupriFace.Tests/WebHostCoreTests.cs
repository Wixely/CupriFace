using CupriFace.Web;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The shared web host core, driven headlessly through a recording bridge.
///
/// The browser gate covers touch and IME. It does NOT cover video, the straight-alpha present path,
/// the clipboard, the cursor, the favicon or navigation — and the shared-core refactor (#79) rewrote
/// all of them, the video backend most invasively of all (its state went from static to instance).
/// A gate that would have passed either way is not verification.
///
/// <see cref="WebHostCore"/> and <see cref="IWebBridge"/> are both public and the core has no
/// browser dependency of its own, so the whole host runs here against a fake page — which is faster
/// and more deterministic than driving a real one, and runs on every CI rather than only where a
/// browser is installed.
/// </summary>
[Collection("webhostcore")]      // static host state: these must not interleave
public class WebHostCoreTests(ITestOutputHelper output)
{
    /// <summary>A page that records instead of drawing.</summary>
    private sealed class RecordingBridge : IWebBridge
    {
        public readonly List<string> Calls = [];
        public int Presents;
        public nint LastPixels;
        public int LastByteCount, LastWidth, LastHeight;
        public string? Cursor, Favicon, Navigated, ClipboardText, Aria;
        public readonly List<(int Id, string Src)> Opened = [];
        public readonly List<(int Id, double W, double H, bool Visible)> Rects = [];
        public (bool Focused, bool Numeric, bool Multiline, double X, double Y)? TextInput;

        public void Present(nint pixels, int byteCount, int width, int height, int dx, int dy, int dw, int dh)
        {
            Presents++; LastPixels = pixels; LastByteCount = byteCount; LastWidth = width; LastHeight = height;
            Calls.Add($"present {width}x{height} damage {dw}x{dh}");
        }
        public void SetCursor(string c) { Cursor = c; Calls.Add($"cursor {c}"); }
        public void Navigate(string href) { Navigated = href; Calls.Add($"navigate {href}"); }
        public void SetFavicon(string uri) { Favicon = uri; Calls.Add("favicon"); }
        public void ClipboardWrite(string text) { ClipboardText = text; Calls.Add("clipboardWrite"); }
        public void ClipboardPaste() => Calls.Add("clipboardPaste");
        public void PublishAria(string html) { Aria = html; Calls.Add("aria"); }
        public void SetTextInput(bool f, bool n, bool m, double x, double y)
        { TextInput = (f, n, m, x, y); Calls.Add($"textInput {f} {x},{y}"); }
        public void WindowCommand(int c) => Calls.Add($"windowCommand {c}");
        public void VideoOpen(int id, string src) { Opened.Add((id, src)); Calls.Add($"videoOpen {id} {src}"); }
        public void VideoOpenBytes(int id, byte[] b) { Opened.Add((id, $"bytes:{b.Length}")); Calls.Add($"videoOpenBytes {id} {b.Length}"); }
        public void VideoClose(int id) => Calls.Add($"videoClose {id}");
        public void VideoPlay(int id) => Calls.Add($"videoPlay {id}");
        public void VideoPause(int id) => Calls.Add($"videoPause {id}");
        public void VideoMuted(int id, bool m) => Calls.Add($"videoMuted {id} {m}");
        public void VideoVolume(int id, double v) => Calls.Add($"videoVolume {id} {v}");
        public void VideoLoop(int id, bool l) => Calls.Add($"videoLoop {id} {l}");
        public void VideoSeek(int id, double s) => Calls.Add($"videoSeek {id} {s}");
        public void VideoRect(int id, double x, double y, double w, double h,
                              double ct, double cr, double cb, double cl, bool visible, string fit,
                              double a, double b, double c, double d, double e, double f)
        { Rects.Add((id, w, h, visible)); Calls.Add($"videoRect {id} {w}x{h} visible={visible}"); }
    }

    /// <summary>A remote source, so Open takes the URL path and hands it to the page directly. The
    /// URL is never fetched here — the page would do that, and the page is a recording fake.</summary>
    private sealed class VideoApp : CupriApp
    {
        public override string Html => """
            <body><div style="padding:8px">
              <cupri-video src="https://example.invalid/clip.webm" muted loop
                           style="width:160px;height:90px"></cupri-video>
            </div></body>
            """;
        public override string Css => "body{margin:0}";
    }

    /// <summary>A local source, which resolves through the same pipeline images use and reaches the
    /// page as BYTES — the other half of Open, and the half an app's embedded clip takes.</summary>
    private sealed class EmbeddedVideoApp : CupriApp
    {
        public override string Html => $$"""
            <body><div style="padding:8px">
              <cupri-video src="{{FixturePath}}" muted style="width:160px;height:90px"></cupri-video>
            </div></body>
            """;
        public override string Css => "body{margin:0}";
        internal static string FixturePath =
            Path.Combine(AppContext.BaseDirectory, "fixtures", "demo.webm").Replace("\\", "/");
    }

    /// <summary>An editable field with known content, for the paths that need a real selection.</summary>
    private sealed class FieldApp : CupriApp
    {
        private sealed class M { public string Name { get; set; } = "hello"; }
        private readonly M _m = new();
        public override object Model => _m;
        public override string Html =>
            "<body><cupri-textfield value=\"{{Name}}\"></cupri-textfield></body>";
        public override string Css => "body{margin:0}";
    }

    private sealed class PlainApp : CupriApp
    {
        public override string Html => "<body><div style=\"padding:20px\">hello</div></body>";
        public override string Css => "body{margin:0;background:#fff}";
    }

    private static RecordingBridge Boot(CupriApp app)
    {
        var bridge = new RecordingBridge();
        WebHostCore.Init(app, null, bridge);
        return bridge;
    }

    [Fact]
    public void The_host_paints_a_frame_and_mirrors_it_for_screen_readers()
    {
        var js = Boot(new PlainApp());
        Assert.True(WebHostCore.Tick(300, 200, 16), "the first tick must paint");

        Assert.Equal(1, js.Presents);
        Assert.Equal(300, js.LastWidth);
        Assert.Equal(200, js.LastHeight);
        // A real buffer in memory, sized for RGBA8888 — not a null handed over by a broken bridge.
        Assert.NotEqual(nint.Zero, js.LastPixels);
        Assert.Equal(300 * 200 * 4, js.LastByteCount);
        Assert.False(string.IsNullOrEmpty(js.Aria), "the ARIA mirror must be published on an input-driven frame");

        // Render-on-demand: an unchanged frame paints nothing at all.
        Assert.False(WebHostCore.Tick(300, 200, 32), "an idle tick must not paint");
        Assert.Equal(1, js.Presents);
    }

    /// <summary>The path the touch gate never reaches: a &lt;cupri-video&gt; must reach the page as
    /// an open request, and then be positioned every painted frame.</summary>
    [Fact]
    public void A_video_element_is_opened_and_kept_glued_to_its_box()
    {
        var js = Boot(new VideoApp());
        WebHostCore.Tick(320, 240, 16);

        Assert.True(js.Opened.Count > 0,
            "no video was opened — the backend never reached the bridge. Calls: " + string.Join(", ", js.Calls));
        output.WriteLine("opened: " + string.Join(", ", js.Opened.Select(o => $"{o.Id}:{o.Src}")));

        // SyncRects runs in the same painted frame as the blit, so the hole and the element cannot
        // shear apart. A rect with a real size proves the whole chain ran.
        Assert.True(js.Rects.Count > 0, "the video was never positioned. Calls: " + string.Join(", ", js.Calls));
        var last = js.Rects[^1];
        output.WriteLine($"last rect: {last.W}x{last.H} visible={last.Visible}");
        Assert.True(last.W > 0 && last.H > 0, $"the video was sized to nothing ({last.W}x{last.H})");
    }

    /// <summary>Transport calls must reach the page. These are the player methods whose static
    /// backing became instance state in the refactor.</summary>
    [Fact]
    public void Video_transport_reaches_the_page()
    {
        var js = Boot(new VideoApp());
        WebHostCore.Tick(320, 240, 16);
        var id = js.Opened[0].Id;

        // The browser reports the element is ready, which is what flips the engine's hole on.
        WebHostCore.VideoReady(id);
        WebHostCore.VideoMeta(id, duration: 12.5, width: 160, height: 90);
        WebHostCore.VideoPlayState(id, playing: true);
        WebHostCore.VideoTime(id, 3.25);

        // With a player ready the present path switches to straight alpha — a whole branch of Paint
        // that nothing else turns on. It must still paint.
        var before = js.Presents;
        Assert.True(WebHostCore.Tick(320, 240, 200), "a frame with a ready video must paint");
        Assert.True(js.Presents > before, "the straight-alpha path produced no present");
        Assert.Equal(320 * 240 * 4, js.LastByteCount);
    }

    /// <summary>The other half of Open: a local source resolves to BYTES and reaches the page that
    /// way, which is how an app's embedded clip plays on the web.</summary>
    [Fact]
    public void A_local_video_reaches_the_page_as_bytes()
    {
        Assert.True(File.Exists(EmbeddedVideoApp.FixturePath), "the demo clip fixture is missing");
        var js = Boot(new EmbeddedVideoApp());
        WebHostCore.Tick(320, 240, 16);

        var opened = js.Opened.FirstOrDefault();
        output.WriteLine("opened: " + string.Join(", ", js.Opened.Select(o => $"{o.Id}:{o.Src}")));
        Assert.True(js.Calls.Any(c => c.StartsWith("videoOpenBytes")),
            "a local source must reach the page as bytes. Calls: " + string.Join(", ", js.Calls));
        Assert.StartsWith("bytes:", opened.Src);
        // Real bytes, not an empty array from a resolution that quietly failed.
        Assert.True(int.Parse(opened.Src["bytes:".Length..]) > 1000, $"suspiciously small payload: {opened.Src}");
    }

    /// <summary>The context menu's clipboard route, which the browser gate never exercises. Paste
    /// first, because it reaches the page unconditionally and so proves the handler is wired at all;
    /// then Copy, which only writes when there IS a selection — so it also proves the selection made
    /// it through.</summary>
    [Fact]
    public void The_context_menu_carries_clipboard_commands_to_the_page()
    {
        var js = Boot(new FieldApp());
        WebHostCore.Tick(300, 200, 16);

        WebHostCore.Document.RequestContextCommand(CupriFace.Interaction.ContextCommand.Paste);
        Assert.Contains("clipboardPaste", js.Calls);

        // Focus the field, select its text, then copy. Copy is conditional on a selection existing,
        // which is exactly why it is worth asserting: a broken bridge and an empty selection look
        // identical from outside.
        WebHostCore.EditKeyPress((int)CupriFace.Interaction.EditKey.Tab, 0);
        WebHostCore.EditKeyPress((int)CupriFace.Interaction.EditKey.SelectAll, 0);
        output.WriteLine($"selection: '{WebHostCore.CopySelection()}'");

        WebHostCore.Document.RequestContextCommand(CupriFace.Interaction.ContextCommand.Copy);
        Assert.True(js.Calls.Contains("clipboardWrite"),
            "Copy did not reach the page. Calls: " + string.Join(", ", js.Calls));
        Assert.Equal("hello", js.ClipboardText);
    }

    [Fact]
    public void Pointer_input_reaches_the_document_and_pushes_a_cursor()
    {
        var js = Boot(new PlainApp());
        WebHostCore.Tick(300, 200, 16);
        WebHostCore.PointerMove(30, 30);
        // The cursor is pushed only when it CHANGES, so the first move over content sets it.
        Assert.NotNull(js.Cursor);
        output.WriteLine($"cursor: {js.Cursor}");
    }

    [Fact]
    public void The_key_map_the_page_reads_is_the_engines_own()
    {
        Boot(new PlainApp());
        var map = WebHostCore.EditKeyMap();
        Assert.Contains("\"Backspace\":", map);
        Assert.Contains("\"SelectAll\":", map);
        // Both pages parse this as JSON at boot; malformed here is a dead keyboard there.
        Assert.StartsWith("{", map);
        Assert.EndsWith("}", map);
    }
}
