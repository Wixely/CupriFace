using CupriFace.Interaction;
using CupriFace.Web;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The browser half of file drop (#182), driven headlessly through the same recording page the rest
/// of the web host is tested with.
///
/// <para>This is the half that decided the API. A desktop window can hand over a path and read it
/// whenever it likes; a page can do neither — a <c>File</c> is a blob handle with no path, and
/// reading one is a promise. So the browser path is the one that proves the design travels: the
/// engine must accept files it cannot read yet, hand the app metadata that arrived synchronously,
/// and survive an answer that comes back later, out of order, or never.</para>
///
/// <para>The page is a fake, but it is not a simplification: the sequence asserted here —
/// name, type, file, commit, then a read answered by token — is exactly what
/// <c>wwwroot/main.js</c> emits, because the C ABI has no string and no promise to emit anything
/// else with.</para>
/// </summary>
[Collection("webhostcore")]      // static host state: these must not interleave
public class WebFileDropTests(ITestOutputHelper output)
{
    private sealed class DropApp : CupriApp
    {
        public readonly List<CupriDocument.FileDropEvent> Drops = [];
        public override string Html =>
            "<body><div class='zone cupri-drop'>drop here</div><div class='rest'>elsewhere</div></body>";
        public override string Css =>
            "body{margin:0;background:#fff;display:flex}"
            + ".zone{width:150px;height:200px;background:#eee}"
            + ".rest{width:150px;height:200px}"
            + ".cupri-drop:drop-over{background:#d9642a}";
    }

    // Boot a document that accepts drops, and return the page it talks to.
    private static (WebHostCoreDropFixture Fixture, DropApp App) Boot()
    {
        var app = new DropApp();
        var bridge = new WebHostCoreDropFixture();
        WebHostCore.Init(app, doc => doc.OnFileDrop(e => app.Drops.Add(e)), bridge);
        WebHostCore.Tick(300, 200, 16);
        return (bridge, app);
    }

    // What main.js does for one file, in the order it does it.
    private static void PushFile(int id, string name, string type, long size, bool isDirectory = false)
    {
        WebHostCore.DropName(name);
        WebHostCore.DropType(type);
        WebHostCore.DropFile(id, size, isDirectory);
    }

    /// <summary>The page's sequence lands as one event with every file in it. Name and type cross in
    /// separate calls because the C ABI has no string type, so the assembly order is part of the
    /// contract rather than an implementation detail.</summary>
    [Fact]
    public void The_pages_call_sequence_becomes_one_drop()
    {
        var (_, app) = Boot();

        PushFile(1, "a.md", "text/markdown", 12);
        PushFile(2, "b.png", "image/png", 3400);
        WebHostCore.DropCommit(40, 50);

        var drop = Assert.Single(app.Drops);
        Assert.Equal(["a.md", "b.png"], drop.Files.Select(f => f.Name));
        Assert.Equal([12L, 3400L], drop.Files.Select(f => f.Size));
        Assert.Contains("zone", drop.Target!.ClassList);
    }

    /// <summary>The browser's own idea of the type wins; where it has none (it often reports an empty
    /// string) the extension fills in, so an app is not handed a blank.</summary>
    [Fact]
    public void The_browsers_type_is_used_and_an_empty_one_falls_back_to_the_extension()
    {
        var (_, app) = Boot();

        PushFile(1, "odd.bin", "text/plain", 1);   // the browser insists
        PushFile(2, "notes.md", "", 1);            // …and here it has no idea
        WebHostCore.DropCommit(40, 50);

        var files = Assert.Single(app.Drops).Files;
        Assert.Equal("text/plain", files[0].MediaType);
        Assert.Equal("text/markdown", files[1].MediaType);
    }

    /// <summary><b>Nothing is read on arrival.</b> The whole reason metadata and bytes are separated
    /// is that a page must be able to drop a 4GB video without transferring it, so the host must not
    /// ask the page for anything until the app does.</summary>
    [Fact]
    public void A_drop_reads_nothing_until_the_app_asks()
    {
        var (js, app) = Boot();

        PushFile(7, "huge.mp4", "video/mp4", 4_000_000_000);
        WebHostCore.DropCommit(40, 50);

        Assert.Empty(js.DropReads);
        var file = Assert.Single(Assert.Single(app.Drops).Files);
        Assert.Equal(4_000_000_000, file.Size);   // enough to refuse it, with nothing transferred

        _ = file.ReadBytesAsync();
        Assert.Equal(7, Assert.Single(js.DropReads).FileId);
    }

    /// <summary>The round trip: the host asks by token, the page answers later, the awaiting read
    /// completes. This is the shape a promise has to be flattened into, because neither the C ABI nor
    /// the bridge interface can carry one.</summary>
    [Fact]
    public async Task Bytes_arrive_when_the_page_answers_the_token()
    {
        var (js, app) = Boot();
        PushFile(3, "hello.txt", "text/plain", 5);
        WebHostCore.DropCommit(40, 50);

        var file = Assert.Single(Assert.Single(app.Drops).Files);
        var pending = file.ReadTextAsync();
        Assert.False(pending.IsCompleted, "a blob read cannot complete synchronously");

        var (fileId, token) = Assert.Single(js.DropReads);
        Assert.Equal(3, fileId);
        WebHostCore.DropBytes(token, "hello"u8.ToArray());

        Assert.Equal("hello", await pending);
    }

    /// <summary>Two reads in flight at once are told apart by their tokens, and may be answered in
    /// either order — a page awaiting two blobs has no obligation to finish them in sequence, and the
    /// engine must not assume it will.</summary>
    [Fact]
    public async Task Two_reads_are_answered_by_token_in_any_order()
    {
        var (js, app) = Boot();
        PushFile(1, "one.txt", "text/plain", 3);
        PushFile(2, "two.txt", "text/plain", 3);
        WebHostCore.DropCommit(40, 50);

        var files = Assert.Single(app.Drops).Files;
        var a = files[0].ReadTextAsync();
        var b = files[1].ReadTextAsync();

        Assert.Equal(2, js.DropReads.Count);
        var tokenFor1 = js.DropReads.Single(r => r.FileId == 1).Token;
        var tokenFor2 = js.DropReads.Single(r => r.FileId == 2).Token;

        WebHostCore.DropBytes(tokenFor2, "two"u8.ToArray());   // second first, deliberately
        WebHostCore.DropBytes(tokenFor1, "one"u8.ToArray());

        Assert.Equal("one", await a);
        Assert.Equal("two", await b);
    }

    /// <summary>A read that fails faults rather than returning nothing. An app must be able to tell
    /// "the file moved" from "the file was empty", and a silently empty array says the wrong one.</summary>
    [Fact]
    public async Task A_failed_read_faults_rather_than_returning_empty()
    {
        var (js, app) = Boot();
        PushFile(1, "gone.txt", "text/plain", 9);
        WebHostCore.DropCommit(40, 50);

        var pending = Assert.Single(Assert.Single(app.Drops).Files).ReadBytesAsync();
        WebHostCore.DropFailed(Assert.Single(js.DropReads).Token, "the file is no longer available");

        var ex = await Assert.ThrowsAsync<IOException>(() => pending);
        output.WriteLine(ex.Message);
        Assert.Contains("no longer available", ex.Message);
    }

    /// <summary>An answer to a token nobody is waiting for — a read abandoned, or a page answering
    /// twice — is ignored rather than throwing into the page's callback.</summary>
    [Fact]
    public void An_answer_to_an_unknown_token_is_ignored()
    {
        Boot();
        WebHostCore.DropBytes(9999, [1, 2, 3]);           // must not throw
        WebHostCore.DropFailed(9999, "nobody asked");     // nor this
    }

    // ---- the highlight only this host can show ---------------------------------------------------

    /// <summary>Drag-over marks the target. The browser is the ONLY host that can do this — a desktop
    /// window hears nothing until the drop — so if it regresses, nothing else covers it.</summary>
    [Fact]
    public void Dragging_over_the_canvas_marks_the_target()
    {
        Boot();
        WebHostCore.DropOver(40, 50);
        Assert.True(Zone().HasAttribute("data-drop-over"));

        WebHostCore.DropOver(220, 50);   // onto the half that is not a target
        Assert.False(Zone().HasAttribute("data-drop-over"));
    }

    /// <summary>Leaving the canvas clears it — a drag abandoned outside the window must not leave a
    /// panel lit up with nothing to turn it off.</summary>
    [Fact]
    public void Leaving_the_canvas_clears_the_highlight()
    {
        Boot();
        WebHostCore.DropOver(40, 50);
        WebHostCore.DropLeave();
        Assert.False(Zone().HasAttribute("data-drop-over"));
    }

    /// <summary>A drag that leaves mid-assembly takes the half-built file list with it, or the next
    /// drop would carry files the user already dragged away.</summary>
    [Fact]
    public void Leaving_discards_files_that_were_still_being_assembled()
    {
        var (_, app) = Boot();

        PushFile(1, "abandoned.txt", "text/plain", 1);
        WebHostCore.DropLeave();

        PushFile(2, "real.txt", "text/plain", 1);
        WebHostCore.DropCommit(40, 50);

        Assert.Equal(["real.txt"], Assert.Single(app.Drops).Files.Select(f => f.Name));
    }

    /// <summary>The page asks before it tells the browser it accepts a drag, so a document with no
    /// handler keeps the "no entry" cursor instead of silently swallowing files.</summary>
    [Fact]
    public void A_document_with_no_handler_reports_that_it_accepts_nothing()
    {
        WebHostCore.Init(new DropApp(), null, new WebHostCoreDropFixture());
        WebHostCore.Tick(300, 200, 16);
        Assert.False(WebHostCore.AcceptsFileDrop());

        Boot();
        Assert.True(WebHostCore.AcceptsFileDrop());
    }

    /// <summary>A commit with nothing gathered is not a drop. Browsers do fire a drop carrying only
    /// non-file data, and the page filters it, but the host must not depend on that.</summary>
    [Fact]
    public void An_empty_commit_raises_nothing()
    {
        var (_, app) = Boot();
        WebHostCore.DropCommit(40, 50);
        Assert.Empty(app.Drops);
    }

    /// <summary>Canvas coordinates are divided by the present scale like every other pointer input,
    /// so a drop on a scaled canvas lands where the user aimed.</summary>
    [Fact]
    public void The_point_is_scaled_like_every_other_pointer_input()
    {
        var app = new ScaledDropApp();
        WebHostCore.Init(app, doc => doc.OnFileDrop(e => app.Drops.Add(e)), new WebHostCoreDropFixture());
        WebHostCore.Tick(600, 400, 16);      // 2x: 300x200 logical

        PushFile(1, "x.txt", "text/plain", 1);
        WebHostCore.DropCommit(80, 100);

        var drop = Assert.Single(app.Drops);
        output.WriteLine($"canvas 80,100 -> document {drop.X:0},{drop.Y:0}");
        Assert.Equal(40f, drop.X, 0.5);
        Assert.Equal(50f, drop.Y, 0.5);
    }

    private sealed class ScaledDropApp : CupriApp
    {
        public readonly List<CupriDocument.FileDropEvent> Drops = [];
        public override string Html => "<body><div class='zone cupri-drop'>zone</div></body>";
        public override string Css => "body{margin:0;background:#fff}.zone{width:300px;height:200px}";
        public override PresentInfo Present(float w, float h) => new(w / 2f, h / 2f, 2f);
    }

    /// <summary>
    /// A folder dropped on the PAGE is reported as one, exactly as it is on the desktop.
    ///
    /// <para>A browser presents a dropped folder as a zero-byte File that fails to read, so without
    /// the page asking <c>webkitGetAsEntry</c> during the drop event, a folder and an empty file are
    /// indistinguishable until something tries to open one. That asymmetry is what made a dropped
    /// folder behave differently on each host.</para>
    /// </summary>
    [Fact]
    public async Task A_folder_dropped_on_the_page_is_reported_as_a_folder()
    {
        var (js, app) = Boot();

        PushFile(1, "project", "", 0, isDirectory: true);
        PushFile(2, "notes.md", "text/markdown", 4);
        WebHostCore.DropCommit(40, 50);

        var files = Assert.Single(app.Drops).Files;
        Assert.True(files[0].IsDirectory);
        Assert.False(files[1].IsDirectory);

        // …and reading it is refused here, without troubling the page at all.
        var ex = await Assert.ThrowsAsync<IOException>(() => files[0].ReadBytesAsync());
        output.WriteLine(ex.Message);
        Assert.Contains("is a folder, not a file", ex.Message);
        Assert.Empty(js.DropReads);
    }

    private static AngleSharp.Dom.IElement Zone()
    {
        var n = WebHostCore.Document.Root;
        AngleSharp.Dom.IElement? found = null;
        void Walk(Dom.RenderNode x)
        {
            if (found is null && x.Element?.ClassList.Contains("zone") == true) found = x.Element;
            foreach (var c in x.Children) Walk(c);
        }
        Walk(n);
        return found!;
    }
}

/// <summary>A page that records drop-read requests and does nothing else. Separate from
/// <c>WebHostCoreTests.RecordingBridge</c> only because that one is private to its class.</summary>
internal sealed class WebHostCoreDropFixture : IWebBridge
{
    public readonly List<(int FileId, int Token)> DropReads = [];
    public void DropRead(int fileId, int token) => DropReads.Add((fileId, token));

    public void Present(nint pixels, int byteCount, int w, int h, int dx, int dy, int dw, int dh) { }
    public void SetCursor(string cssCursor) { }
    public void Navigate(string href) { }
    public void SetFavicon(string dataUri) { }
    public void ClipboardWrite(string text) { }
    public void ClipboardPaste() { }
    public void PublishAria(string html) { }
    public void SetTextInput(bool focused, bool numeric, bool multiline, double x, double y) { }
    public void WindowCommand(int command) { }
    public void VideoOpen(int id, string src) { }
    public void VideoOpenBytes(int id, byte[] bytes) { }
    public void VideoClose(int id) { }
    public void VideoPlay(int id) { }
    public void VideoPause(int id) { }
    public void VideoMuted(int id, bool muted) { }
    public void VideoVolume(int id, double volume) { }
    public void VideoLoop(int id, bool loop) { }
    public void VideoSeek(int id, double seconds) { }
    public void UnderlayOpenCanvas(int id, string surfaceKey) { }
    public void UnderlayClose(int id) { }
    public void UnderlayRect(int id, double x, double y, double w, double h,
                             double ct, double cr, double cb, double cl, bool visible, string fit,
                             double a, double b, double c, double d, double e, double f) { }
}
