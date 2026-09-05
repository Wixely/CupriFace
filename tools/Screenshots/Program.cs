using CupriFace;
using CupriFace.Demo;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

// Regenerates docs/screenshots/*.png from the Showcase.
//
//   dotnet run --project tools/Screenshots
//
// HEADLESS ON PURPOSE. These are published in the README, so the safest way to produce them is one
// that cannot capture anything but the document: no window, no title bar, no desktop behind it, no
// file path in a caption. RenderToImage draws the same display list a host would and nothing else,
// so a screenshot cannot leak a username, a machine name or whatever happened to be on screen —
// which is exactly how such things get published by accident.
//
// The Showcase's own pages are all synthetic demo data. The two worth a second look are Diagnostics
// (process metrics — RAM, GC counts, threads, uptime; no paths or identities) and 3D, which prints
// the GPU string the driver reports. That one names the machine's graphics hardware, which is the
// point of the row, and is checked by eye before committing.

const int W = 940, H = 720;
const int Scale = 2;      // the resolution the committed screenshots have always been
var outDir = Path.Combine(FindRepoRoot(), "docs", "screenshots");
Directory.CreateDirectory(outDir);

// One page per shot. The 3D page needs a moment: its surface renders on a private context and
// publishes frames asynchronously, so a capture taken immediately gets the empty panel.
(string Section, string File, bool Dark, int WaitMs)[] shots =
[
    ("controls",   "inputs.png",       false, 0),
    ("controls",   "inputs-dark.png",  true,  0),
    ("components", "components.png",   false, 0),
    ("charts",     "charts.png",       false, 0),
    ("images",     "images.jpg",       false, 0),   // a photo page: JPEG, as it always was
    ("3d",         "threed.png",       false, 4000),
    ("overlays",   "overlays.png",     false, 0),
    ("layout",     "layout.png",       false, 0),
    ("motion",     "motion.png",       false, 0),
    ("styling",    "styling.png",      false, 0),
    ("settings",   "settings.png",     false, 0),
    ("diag",       "diagnostics.png",  false, 600),
];

foreach (var (section, file, dark, waitMs) in shots)
{
    var app = new ShowcaseApp(section);
    using var doc = app.CreateDocument();
    if (app.Model is ShowcaseModel m) m.DarkMode = dark;

    // The overlays page is worth showing with something actually open — a screenshot of the buttons
    // that would open a dialog says less than the dialog.
    if (section == "overlays" && app.Model is ShowcaseModel om) om.DialogOpen = true;

    // The 3D page needs a surface attached, exactly as a host would at its composition root.
    // Headless has no GRContext, so this takes the private-context readback path — the same one a
    // software window uses, which is why it works here at all.
    IDisposable? surface = null;
    if (section == "3d")
    {
        // What a host does every frame, and what this tool must do for the same reason: these images
        // are captured at 2x, so a viewport told nothing would render at logical resolution and be
        // upscaled into a panel that is twice as many pixels. One line, and the difference is visible.
        doc.Surfaces.DeviceScale = Scale;
        // Draw the model, but not the row that names this machine's GPU. See ShowDriverRow: the
        // point of headless capture is that a published image shows the document and nothing about
        // the box it was generated on, and that is a rule rather than a per-string judgement.
        if (app.Model is ShowcaseModel gm) gm.ShowDriverRow = false;
        surface = CupriFace.Samples.Viewer.Teapot3dSurface.TryAttach(doc, _ => { });
    }

    doc.Refresh();
    using (doc.RenderToImage(W, H)) { }          // first frame: lays out and warms images

    if (waitMs > 0)
    {
        // Poll rather than sleep once: whichever finishes first, we stop waiting.
        var until = DateTime.UtcNow.AddMilliseconds(waitMs);
        while (DateTime.UtcNow < until)
        {
            // A HOST polls AnyTicking every frame, and a surface uses that poll to decide it is on
            // screen and start producing. RenderToImage alone does not, so without this the 3D
            // viewport renders an empty panel for ever — correct behaviour for a surface nobody
            // asked to tick, and a blank screenshot.
            _ = doc.Surfaces.AnyTicking;
            using (doc.RenderToImage(W, H)) { }
            Thread.Sleep(120);
        }
    }

    using var img = Capture(doc, app, W, H, Scale);
    // PNG for UI (flat colour, sharp text); JPEG for the page that is mostly a photograph, where
    // PNG costs four times the bytes for no visible gain.
    var jpeg = file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
    using var data = img.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, jpeg ? 88 : 95);
    var path = Path.Combine(outDir, file);
    using (var f = File.Create(path)) data.SaveTo(f);
    surface?.Dispose();
    Console.WriteLine($"{file,-20} {new FileInfo(path).Length / 1024,5} KB");
}

Console.WriteLine($"\nwrote {shots.Length} images to {outDir}");

// Render at 2x: lay out at the logical size and paint into a surface of twice the pixels, with the
// canvas scaled — which is what a host does on a HiDPI display. RenderToImage(w*2, h*2) would lay
// out a viewport twice as WIDE instead, giving a different screenshot rather than a sharper one.
// The committed images have always been 2x; capturing at 1x quietly halved the README's quality.
static SKImage Capture(CupriDocument doc, CupriApp app, int logicalW, int logicalH, int scale)
{
    var info = new SKImageInfo(logicalW * scale, logicalH * scale, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    var canvas = surface.Canvas;
    canvas.Clear(app.Background);
    canvas.Save();
    canvas.Scale(scale);
    doc.Render(canvas, logicalW, logicalH);
    canvas.Restore();
    return surface.Snapshot();
}

static string FindRepoRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriFace.slnx"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("repo root not found");
}
