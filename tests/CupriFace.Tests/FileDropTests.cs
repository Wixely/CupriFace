using System.Text;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Accepting a file dragged in from outside the window (#182).
///
/// <para>The engine's half is deliberately host-shaped: a point, a list of <see cref="DroppedFile"/>,
/// and an element the point landed on. What is worth pinning is not that the handler fires — it is
/// the three things that differ between a desktop window and a browser tab, because an API that only
/// works on one of them is the failure mode this design exists to avoid:</para>
///
/// <list type="bullet">
/// <item>metadata is readable with no await, so an app can refuse a file it does not want;</item>
/// <item>bytes are readable with one, on every host, including where there is no path;</item>
/// <item>the drag highlight is OPTIONAL — GLFW never reports a drag-over, so a document has to look
/// right when <c>DispatchDropOver</c> is never called at all.</item>
/// </list>
/// </summary>
public class FileDropTests(ITestOutputHelper output)
{
    private const string Html = """
        <body>
          <div class='pane left cupri-drop'>drop here</div>
          <div class='pane right'>not a target</div>
        </body>
        """;
    private const string Css = """
        body{margin:0;font-family:sans-serif;display:flex}
        .pane{width:150px;height:100px}
        .left{background:#eee}
        .right{background:#ddd}
        .cupri-drop:drop-over{background:#d9642a}
        """;

    private static TestDoc Doc() => new(Html, Css, width: 300, height: 100);

    // ---- routing: which element did it land on ---------------------------------------------------

    /// <summary>The point picks the target, so an app can build "drop onto THIS column" rather than
    /// "drop somewhere on the window" — which is the whole reason the event carries a point.</summary>
    [Fact]
    public void A_drop_names_the_element_it_landed_on()
    {
        using var t = Doc();
        CupriDocument.FileDropEvent? got = null;
        t.Doc.OnFileDrop(e => got = e);

        new DropDriver(t.Doc).DropNamed(x: 40, y: 50, "budget.csv");

        Assert.NotNull(got);
        Assert.Equal("budget.csv", Assert.Single(got!.Value.Files).Name);
        Assert.Contains("left", got.Value.Target!.ClassList);
    }

    /// <summary>A drop on something that never claimed to be a target still fires, with a null
    /// Target. An app that accepts files anywhere on its window is the common case and must not have
    /// to mark up the whole body to get one.</summary>
    [Fact]
    public void A_drop_on_no_target_still_fires_with_a_null_target()
    {
        using var t = Doc();
        CupriDocument.FileDropEvent? got = null;
        t.Doc.OnFileDrop(e => got = e);

        new DropDriver(t.Doc).DropNamed(x: 220, y: 50, "notes.txt");

        Assert.NotNull(got);
        Assert.Null(got!.Value.Target);
        output.WriteLine($"landed at {got.Value.X:0},{got.Value.Y:0} on (null)");
    }

    /// <summary>The target is the nearest ENCLOSING marked element, not the leaf under the pointer —
    /// otherwise marking a column would fail the moment it had a child.</summary>
    [Fact]
    public void The_target_is_the_nearest_marked_ancestor()
    {
        using var t = new TestDoc(
            "<body><div class='col cupri-drop'><div class='card'>a card</div></div></body>",
            "body{margin:0;font-family:sans-serif}.col{width:200px;height:120px;padding:20px}"
            + ".card{width:160px;height:40px;background:#ccc}",
            width: 300, height: 200);

        List<string>? classes = null;
        t.Doc.OnFileDrop(e => classes = e.Target?.ClassList.ToList());
        new DropDriver(t.Doc).DropNamed(40, 40, "x.txt");   // squarely on the card

        Assert.NotNull(classes);
        Assert.Contains("col", classes!);
    }

    /// <summary>Several files at once — dropping a selection is one gesture, not several.</summary>
    [Fact]
    public void A_drop_can_carry_several_files()
    {
        using var t = Doc();
        IReadOnlyList<DroppedFile>? files = null;
        t.Doc.OnFileDrop(e => files = e.Files);

        new DropDriver(t.Doc).DropNamed(40, 50, "a.png", "b.png", "c.png");

        Assert.Equal(["a.png", "b.png", "c.png"], files!.Select(f => f.Name));
    }

    /// <summary>A document with no handler is not disturbed by a drop, and says so in advance so a
    /// host need not offer a drop cursor over it.</summary>
    [Fact]
    public void A_document_with_no_handler_accepts_nothing()
    {
        using var t = Doc();
        Assert.False(t.Doc.AcceptsFileDrop);
        t.Doc.DispatchFileDrop(40, 50, [DroppedFile.FromBytes("x.txt", [])]);   // must not throw
        t.Doc.OnFileDrop(_ => { });
        Assert.True(t.Doc.AcceptsFileDrop);
    }

    // ---- the highlight, and the host that can never show it --------------------------------------

    /// <summary>Drag-over marks the target so CSS can light it up, the way :hover does.</summary>
    [Fact]
    public void Dragging_over_a_target_marks_it()
    {
        using var t = Doc();
        t.Doc.OnFileDrop(_ => { });
        var drop = new DropDriver(t.Doc);

        drop.Over(40, 50);
        Assert.Equal("", t.FindClass("left").Element!.GetAttribute("data-drop-over"));

        drop.Over(220, 50);   // across to the pane that is not a target
        Assert.False(t.FindClass("left").Element!.HasAttribute("data-drop-over"));
    }

    /// <summary>Leaving takes the highlight with it — otherwise a cancelled drag leaves a panel lit up
    /// with nothing to turn it off.</summary>
    [Fact]
    public void Leaving_clears_the_highlight()
    {
        using var t = Doc();
        t.Doc.OnFileDrop(_ => { });
        var drop = new DropDriver(t.Doc);

        drop.Over(40, 50);
        drop.Leave();
        Assert.False(t.FindClass("left").Element!.HasAttribute("data-drop-over"));
    }

    /// <summary>…and so does the drop itself. The highlight is drag state; once the files have landed
    /// there is no drag.</summary>
    [Fact]
    public void Dropping_clears_the_highlight()
    {
        using var t = Doc();
        t.Doc.OnFileDrop(_ => { });
        new DropDriver(t.Doc).DropNamed(40, 50, "x.txt");
        Assert.False(t.FindClass("left").Element!.HasAttribute("data-drop-over"));
    }

    /// <summary>
    /// <b>The one that protects the GLFW host.</b> Our primary desktop window reports the drop and
    /// gives no warning at all beforehand, so <c>DispatchDropOver</c> is never called there. A drop
    /// dispatched cold — no Over, ever — must route exactly as one that was dragged in.
    /// </summary>
    [Fact]
    public void A_drop_with_no_drag_over_beforehand_works_identically()
    {
        using var warmed = Doc();
        using var cold = Doc();
        CupriDocument.FileDropEvent? a = null, b = null;
        warmed.Doc.OnFileDrop(e => a = e);
        cold.Doc.OnFileDrop(e => b = e);

        new DropDriver(warmed.Doc).DropNamed(40, 50, "x.txt");                                  // Over, then drop
        cold.Doc.DispatchFileDrop(40, 50, [DroppedFile.FromBytes("x.txt", [])]);                 // drop, cold

        Assert.Equal(a!.Value.Target!.ClassList, b!.Value.Target!.ClassList);
        Assert.Equal(a.Value.X, b!.Value.X);
    }

    /// <summary>The highlight is a repaint, and a host that does not repaint shows nothing — so the
    /// dispatch has to report that something changed. Moving within the same target does not: a
    /// dragover stream fires continuously and repainting every frame of it would be waste.</summary>
    [Fact]
    public void Only_a_change_of_target_asks_for_a_repaint()
    {
        using var t = Doc();
        t.Doc.OnFileDrop(_ => { });
        var drop = new DropDriver(t.Doc);

        Assert.True(drop.Over(40, 50));    // nothing -> left
        Assert.False(drop.Over(60, 60));   // still left
        Assert.True(drop.Over(220, 50));   // left -> nothing
    }

    /// <summary>The style actually applies — the attribute existing is not the same as the paint
    /// changing, and `:drop-over` is a pseudo we invented, so it is worth proving it resolves.</summary>
    [Fact]
    public void The_drop_over_pseudo_class_restyles_the_target()
    {
        using var t = Doc();
        t.Doc.OnFileDrop(_ => { });

        var before = t.FindClass("left").Style.Background;
        new DropDriver(t.Doc).Over(40, 50);
        t.Layout();
        var after = t.FindClass("left").Style.Background;

        output.WriteLine($"background {before} -> {after}");
        Assert.NotEqual(before, after);
    }

    // ---- the payload: the half that has to work in a browser -------------------------------------

    /// <summary>Metadata with no await. This is what an app filters on, and in a browser it is the
    /// only thing available without a round trip.</summary>
    [Fact]
    public void Metadata_is_readable_without_awaiting_anything()
    {
        var f = DroppedFile.FromBytes("budget.csv", Encoding.UTF8.GetBytes("a,b\n1,2\n"));
        Assert.Equal("budget.csv", f.Name);
        Assert.Equal(8, f.Size);
        Assert.Equal("text/csv", f.MediaType);
    }

    /// <summary>The media type is inferred from the name when the platform does not say, and is
    /// honest — an unknown extension is octet-stream, not a guess.</summary>
    [Theory]
    [InlineData("a.png", "image/png")]
    [InlineData("a.PNG", "image/png")]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("data.json", "application/json")]
    [InlineData("thing.cupriproj", "application/octet-stream")]
    [InlineData("noextension", "application/octet-stream")]
    public void The_media_type_is_inferred_from_the_name(string name, string expected)
        => Assert.Equal(expected, DroppedFile.FromBytes(name, []).MediaType);

    /// <summary>A browser explicitly says what it thinks the type is, and that beats our guess.</summary>
    [Fact]
    public void An_explicit_media_type_wins_over_the_extension()
        => Assert.Equal("text/plain", DroppedFile.FromBytes("odd.bin", [], "text/plain").MediaType);

    /// <summary>Bytes on every host, path or no path.</summary>
    [Fact]
    public async Task Bytes_read_back_without_a_path()
    {
        var f = DroppedFile.FromBytes("hello.txt", Encoding.UTF8.GetBytes("hello"));
        Assert.Null(f.Path);
        Assert.Equal("hello", await f.ReadTextAsync());
    }

    /// <summary>A real file on disk: metadata without opening it, bytes when asked. This is the
    /// desktop path, and the point is that it presents the same face as the browser one.</summary>
    [Fact]
    public async Task A_file_on_disk_presents_the_same_face()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cupri-drop-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, "# hi");
        try
        {
            var f = DroppedFile.FromPath(path);
            Assert.Equal(Path.GetFileName(path), f.Name);
            Assert.Equal(4, f.Size);
            Assert.Equal("text/markdown", f.MediaType);
            Assert.Equal(path, f.Path);
            Assert.Equal("# hi", await f.ReadTextAsync());
        }
        finally { File.Delete(path); }
    }

    /// <summary>The browser's shape: metadata now, bytes over a round trip that has not happened yet.
    /// Nothing is read until someone asks — which is the point, because a drop of a 4GB video should
    /// not cost 4GB to decline.</summary>
    [Fact]
    public async Task A_deferred_file_reads_nothing_until_asked()
    {
        var reads = 0;
        var f = DroppedFile.Deferred("clip.mp4", size: 4_000_000_000, "video/mp4",
            _ => { reads++; return Task.FromResult(new byte[] { 1, 2, 3 }); });

        Assert.Equal(4_000_000_000, f.Size);      // enough to refuse it
        Assert.Equal("video/mp4", f.MediaType);
        Assert.Equal(0, reads);                   // …without having touched it

        Assert.Equal(3, (await f.ReadBytesAsync()).Length);
        Assert.Equal(1, reads);
    }

    /// <summary>A dropped file becomes a CupriSource, so it feeds anything that already takes one —
    /// and carries LocalFile trust, because the user pointed at it but the app never chose it.</summary>
    [Fact]
    public async Task A_dropped_file_becomes_a_source_with_local_file_trust()
    {
        var src = await DroppedFile.FromBytes("a.txt", Encoding.UTF8.GetBytes("x")).ToSourceAsync();
        Assert.Equal(Resources.ResourceTrust.LocalFile, src.Trust);
        Assert.Equal("x", src.ReadText());
    }

    /// <summary>The end-to-end shape an app actually writes: filter on metadata, await only what
    /// survives. Proves the two halves compose without the app needing a host check.</summary>
    [Fact]
    public async Task An_app_filters_on_metadata_and_awaits_only_what_it_wants()
    {
        using var t = Doc();
        var opened = new List<string>();
        var read = new List<string>();
        t.Doc.OnFileDrop(e =>
        {
            foreach (var f in e.Files)
            {
                if (f.MediaType != "text/markdown") continue;
                opened.Add(f.Name);
                read.Add(f.ReadTextAsync().GetAwaiter().GetResult());
            }
        });

        new DropDriver(t.Doc).Drop(40, 50,
            DroppedFile.FromBytes("keep.md", Encoding.UTF8.GetBytes("# kept")),
            DroppedFile.FromBytes("skip.png", [0xFF, 0xD8]),
            DroppedFile.Deferred("huge.mp4", 9_000_000_000, "video/mp4",
                _ => throw new InvalidOperationException("a declined file must never be read")));

        Assert.Equal(["keep.md"], opened);
        Assert.Equal(["# kept"], read);
        await Task.CompletedTask;
    }

    // ---- coordinates -----------------------------------------------------------------------------

    /// <summary>Zoom is the engine's, not the host's: a host hands over window coordinates and the
    /// event reports document ones, like every other dispatch entry point.</summary>
    [Fact]
    public void The_point_is_reported_in_document_coordinates()
    {
        using var t = Doc();
        t.Doc.Zoom = 2f;
        CupriDocument.FileDropEvent? got = null;
        t.Doc.OnFileDrop(e => got = e);

        t.Doc.DispatchFileDrop(80, 100, [DroppedFile.FromBytes("x.txt", [])]);

        Assert.Equal(40f, got!.Value.X, 0.01);
        Assert.Equal(50f, got.Value.Y, 0.01);
    }

    /// <summary>An empty drop is not a drop. Some platforms announce one and then deliver nothing;
    /// an app should not see a handler call with no files.</summary>
    [Fact]
    public void An_empty_drop_does_not_reach_the_handler()
    {
        using var t = Doc();
        var fired = false;
        t.Doc.OnFileDrop(_ => fired = true);
        t.Doc.DispatchFileDrop(40, 50, []);
        Assert.False(fired);
    }

    // ---- folders: the gesture that used to behave differently on each host -----------------------

    /// <summary>
    /// A dropped FOLDER is reported as one, on both platforms, rather than discovered by exception.
    ///
    /// <para>It used to be discovered by exception, and by a different one on each host: desktop threw
    /// <see cref="UnauthorizedAccessException"/> — which is not an <see cref="IOException"/>, so it
    /// slipped past the only catch the API documents — while the browser faulted with IOException for
    /// the identical gesture. An app that handled a dropped folder correctly in a browser crashed on
    /// the desktop. Dragging a folder onto a window is ordinary, so this is a thing people hit.</para>
    /// </summary>
    [Fact]
    public void A_dropped_folder_says_so_rather_than_pretending_to_be_a_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cupri-drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var f = DroppedFile.FromPath(dir);
            output.WriteLine($"{f.Name}  IsDirectory={f.IsDirectory}  Size={f.Size}");
            Assert.True(f.IsDirectory);
            Assert.Equal(Path.GetFileName(dir), f.Name);   // not "" — a trailing separator used to eat it
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A real file is not a folder, which is the other half of the same assertion.</summary>
    [Fact]
    public async Task A_dropped_file_is_not_reported_as_a_folder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cupri-drop-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "x");
        try { Assert.False(DroppedFile.FromPath(path).IsDirectory); }
        finally { File.Delete(path); }
    }

    /// <summary>Reading one throws IOException — the SAME type on both hosts — with a message that
    /// says what went wrong instead of "access denied", which is what a folder read used to report
    /// and which reads like a permissions problem.</summary>
    [Fact]
    public async Task Reading_a_folder_fails_the_same_way_on_both_hosts()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cupri-drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Desktop: a real folder on disk.
            var desktop = await Assert.ThrowsAsync<IOException>(
                () => DroppedFile.FromPath(dir).ReadBytesAsync());

            // Browser: what the page reports for the same gesture.
            var web = await Assert.ThrowsAsync<IOException>(
                () => DroppedFile.Deferred("stuff", 0, "", _ => Task.FromResult<byte[]>([]), isDirectory: true)
                                 .ReadBytesAsync());

            output.WriteLine($"desktop: {desktop.Message}");
            output.WriteLine($"web:     {web.Message}");
            Assert.Contains("is a folder, not a file", desktop.Message);
            Assert.Contains("is a folder, not a file", web.Message);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The browser's folder never calls its read function at all — the page would have
    /// answered with a DOMException turned into something less useful, so it is refused up front.</summary>
    [Fact]
    public async Task A_browser_folder_is_refused_without_a_round_trip()
    {
        var f = DroppedFile.Deferred("project", 0, "", 
            _ => throw new InvalidOperationException("a folder must never reach the page's reader"),
            isDirectory: true);
        Assert.True(f.IsDirectory);
        await Assert.ThrowsAsync<IOException>(() => f.ReadBytesAsync());
    }

    /// <summary>An unreadable FILE — locked, or permission denied — also surfaces as IOException,
    /// so one catch covers every way a local read can fail.</summary>
    [Fact]
    public async Task An_unreadable_file_also_surfaces_as_an_io_exception()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"cupri-drop-{Guid.NewGuid():N}.txt");
        var f = DroppedFile.FromPath(missing);   // vanished between the drop and the read
        await Assert.ThrowsAsync<FileNotFoundException>(() => f.ReadBytesAsync());
        Assert.IsAssignableFrom<IOException>(
            await Record.ExceptionAsync(() => f.ReadBytesAsync()));
    }

    /// <summary>The whole point, as an app would write it: skip folders by asking, not by catching.
    /// This is the handler that used to work in a browser and crash on the desktop.</summary>
    [Fact]
    public void An_app_can_skip_folders_without_catching_anything()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cupri-drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var t = Doc();
            var taken = new List<string>();
            t.Doc.OnFileDrop(e =>
            {
                foreach (var f in e.Files)
                {
                    if (f.IsDirectory) continue;          // no try/catch anywhere
                    taken.Add(f.Name);
                }
            });

            new DropDriver(t.Doc).Drop(40, 50,
                DroppedFile.FromPath(dir),
                DroppedFile.FromBytes("notes.md", "# hi"u8.ToArray()));

            Assert.Equal(["notes.md"], taken);
        }
        finally { Directory.Delete(dir, true); }
    }
}
