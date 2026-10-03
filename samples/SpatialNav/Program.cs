using CupriFace;
using CupriFace.Accessibility;
using CupriFace.Diagnostics;
using CupriFace.Interaction;
using CupriFace.Samples.SpatialNav;
using CupriFace.Shell;
using SkiaSharp;

// Thirty scattered boxes driven by the arrow keys, and one key (M) that switches whether the arrows
// move by GEOMETRY or in DOCUMENT order — the point being to see the difference rather than read
// about it. See SpatialNavApp for what each cluster is for.
//
//   (default)         -> a real window: arrows move, Enter activates, M switches mode
//   CUPRI_HEADLESS=1  -> no window: drive the same keys in both modes and write two PNGs, so the
//                        behaviour is checkable on a machine with no GPU (and in CI)

if (Environment.GetEnvironmentVariable("CUPRI_HEADLESS") is { Length: > 0 })
    return HeadlessProof.Run();

DesktopHost.Run(new SpatialNavApp());
return 0;

/// <summary>
/// The same four keypresses in each mode, written to two PNGs. This exists because the sample's
/// entire claim is that the two modes differ — if they ever stop differing, a window would still
/// open and still look fine, and the demo would be silently showing nothing.
/// </summary>
internal static class HeadlessProof
{
    private const int W = 1040, H = 720;

    public static int Run()
    {
        // The sample is a teaching artefact, so check its own markup first: if it used a property
        // the engine silently ignores it would be teaching that too, and nothing would say so.
        var probe = new SpatialNavApp();
        var report = CupriDoctor.Check(probe.Html, probe.Css, width: W, height: H, model: probe.Model);
        if (!report.IsClean)
        {
            Console.Error.WriteLine("[SpatialNav] the sample's own markup is not clean:");
            Console.Error.WriteLine(report.ToString());
            return 1;
        }
        Console.WriteLine("[SpatialNav] CupriDoctor: clean");

        // A FRESH app per walk. The model — and so every box's Hits counter and the HUD text —
        // belongs to the app, so reusing one leaks the first walk's activation into the second
        // walk's screenshot.
        //
        // Tab in, then three Downs from the top-left box. In document order that walks ACROSS row A,
        // because Down means "next in the markup". Spatially it descends column 1 and carries on
        // into cluster C, which is the honest answer: C1 really is the nearest thing below A9.
        using var off = Walk(new SpatialNavApp(arrowNavigation: false), out var endedOff);
        using var on = Walk(new SpatialNavApp(arrowNavigation: true), out var endedOn);

        Save(off, "spatialnav-off.png");
        Save(on, "spatialnav-on.png");

        Console.WriteLine("[SpatialNav] Tab, Down, Down, Down:");
        Console.WriteLine($"[SpatialNav]   ArrowNavigation=false -> {endedOff}  (document order)");
        Console.WriteLine($"[SpatialNav]   ArrowNavigation=true  -> {endedOn}  (geometry)");
        Console.WriteLine("[SpatialNav] wrote spatialnav-off.png and spatialnav-on.png");

        // The two ways of showing a selection, side by side — the engine's ring and the sample's own
        // :focus styling. A picture is the only way to check this one: it is painted, not laid out.
        using (var styled = FocusStyle(on: true)) Save(styled, "spatialnav-focus-styled.png");
        using (var ring = FocusStyle(on: false)) Save(ring, "spatialnav-focus-ring.png");

        // The diagonal case, in cluster B, which is where the order-dependence showed up: Right and
        // Down pressed together must be ONE move to B2 (the box actually on the diagonal) and must
        // give the SAME answer whichever key the hardware reported first.
        var rightFirst = Corner(EditKey.Right, EditKey.Down);
        var downFirst = Corner(EditKey.Down, EditKey.Right);
        Console.WriteLine($"[SpatialNav] Right+Down from B1 -> {rightFirst};  Down+Right -> {downFirst}");
        if (rightFirst != downFirst || !rightFirst.StartsWith("B2", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "[SpatialNav] FAIL: a corner move should be B2 both ways round — it is order-dependent again.");
            return 1;
        }
        Console.WriteLine("[SpatialNav] PASS: a corner move is one move, and the same one either way.");

        if (endedOff == endedOn)
        {
            Console.Error.WriteLine(
                "[SpatialNav] FAIL: both modes ended on the same box — the toggle did nothing.");
            return 1;
        }
        Console.WriteLine("[SpatialNav] PASS: the two modes navigate differently.");
        return 0;
    }

    /// <summary>Tab to B1, then press two arrows as a pair, and report where focus ended up.</summary>
    private static string Corner(EditKey first, EditKey second)
    {
        using var doc = new SpatialNavApp(arrowNavigation: true).CreateDocument();
        doc.DiagonalNavigation = true;
        using (doc.RenderToImage(W, H)) { }

        // Tab lands on A1; walk to B1, which is the first box of the staggered cluster. Each step
        // needs the window to expire, the way a host's frame loop expires it.
        doc.DispatchKey(null, EditKey.Tab);
        for (var i = 0; i < 4; i++) { doc.DispatchKey(null, EditKey.Right); Release(doc, i); }

        doc.DispatchKey(null, first);                  // …and now the pair, with nothing in between
        doc.DispatchKey(null, second);
        using (doc.RenderToImage(W, H)) { }
        return FocusedLabel(doc);
    }

    /// <summary>Let a held press time out. A host does this from its frame loop; nothing else will.</summary>
    private static void Release(CupriDocument doc, int step)
    {
        doc.Animate(step * 10.0);
        doc.Animate(step * 10.0 + 1.0);
        using (doc.RenderToImage(W, H)) { }
    }

    /// <summary>One frame with the selection on a box, styled either way.</summary>
    private static SKImage FocusStyle(bool on)
    {
        var app = new SpatialNavApp(arrowNavigation: true);
        using var doc = app.CreateDocument();
        using (doc.RenderToImage(W, H)) { }
        doc.DispatchKey(null, EditKey.Tab);
        if (on) doc.DispatchKey("f", EditKey.None);      // the sample's own toggle
        using (doc.RenderToImage(W, H)) { }
        return doc.RenderToImage(W, H, new SKColor(0x14, 0x16, 0x1C));
    }

    private static SKImage Walk(SpatialNavApp app, out string ended)
    {
        using var doc = app.CreateDocument();          // the app already carries the mode
        using (doc.RenderToImage(W, H)) { }           // warm layout before driving input

        doc.DispatchKey(null, EditKey.Tab);
        for (var i = 0; i < 3; i++)
        {
            doc.DispatchKey(null, EditKey.Down);
            using (doc.RenderToImage(W, H)) { }       // a host lays out every frame; so must this
        }
        doc.DispatchKey(null, EditKey.Enter);          // activate, so a ×1 counter shows up too
        ended = FocusedLabel(doc);
        return doc.RenderToImage(W, H, new SKColor(0x14, 0x16, 0x1C));
    }

    /// <summary>Focus on a plain control lives in the accessibility tree, not in an attribute:
    /// <c>data-focus</c> is set for an editable field, so a focused box has nothing to look for.</summary>
    private static string FocusedLabel(CupriDocument doc) =>
        Find(doc.BuildAccessibilityTree(W, H)) ?? "(nothing focused)";

    private static string? Find(AccessibilityNode n)
    {
        if (n.Focused) return n.Name ?? "";
        foreach (var c in n.Children) { var f = Find(c); if (f is not null) return f; }
        return null;
    }

    private static void Save(SKImage img, string name)
    {
        var path = Path.Combine(Environment.CurrentDirectory, name);
        using var data = img.Encode(SKEncodedImageFormat.Png, 95);
        using var fs = File.Create(path);
        data.SaveTo(fs);
        Console.WriteLine($"[SpatialNav]   -> {path}");
    }
}
