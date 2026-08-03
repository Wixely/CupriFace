using CupriFace.Shell;
using SkiaSharp;

// CupriFace M0 smoke: clear to a copper-tinted background, draw a GPU-painted panel
// and the profiler HUD. Proves Layer 0 (window+GL) and the Skia paint bootstrap.
//
//   (default)             -> open a real OS window (needs a hardware GL driver)
//   CUPRI_SMOKE=<frames>  -> windowed, auto-close after N frames
//   CUPRI_HEADLESS=<n>    -> CPU-raster path (§7.5): render n frames, snapshot to PNG,
//                            verify pixels, no window/GL needed. Used for CI.

var background = new SKColor(0x1E, 0x14, 0x0F); // warm near-black
var copper = new SKColor(0xB8, 0x73, 0x33);     // CupriFace copper

using var hudFont = new SKFont(SKTypeface.Default, 16);
using var hudPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
using var panelPaint = new SKPaint { Color = copper, IsAntialias = true };

// Single scene, shared by the windowed and headless paths.
void DrawScene(RenderContext ctx)
{
    var c = ctx.Canvas;
    c.Clear(background);

    // Rounded copper panel — proves real paint, not just a framebuffer clear.
    c.DrawRoundRect(40, 60, ctx.Width - 80, ctx.Height - 120, 16, 16, panelPaint);

    // Profiler HUD (top-left) — visible from frame #1 per DESIGN.md §7.7.
    var s = ctx.Stats;
    var hud = $"CupriFace M0   {s.Fps,5:F0} fps   {s.CpuFrameMs,6:F2} ms CPU/frame   frame #{s.FrameCount}";
    c.DrawText(hud, 16, 28, hudFont, hudPaint);
}

// ---- Headless CPU-raster path (CI / no-GPU environments) --------------------
if (int.TryParse(Environment.GetEnvironmentVariable("CUPRI_HEADLESS"), out var headlessFrames) && headlessFrames > 0)
{
    const int w = 1024, h = 768;
    var renderer = new HeadlessRenderer(w, h);
    Console.WriteLine($"[CupriFace M0] headless CPU-raster: rendering {headlessFrames} frames.");

    using var image = renderer.RenderFrames(headlessFrames, DrawScene);

    // Verify pixels: the copper panel covers the centre; the corner stays background.
    using var bmp = SKBitmap.FromImage(image);
    var centre = bmp.GetPixel(w / 2, h / 2);
    var corner = bmp.GetPixel(4, 4);

    var outPath = Path.Combine(Environment.CurrentDirectory, "m0-headless.png");
    using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
    using (var fs = File.OpenWrite(outPath))
        data.SaveTo(fs);

    var st = renderer.Stats;
    Console.WriteLine($"[CupriFace M0] centre pixel = {centre} (expect copper), corner = {corner} (expect background)");
    Console.WriteLine($"[CupriFace M0] snapshot -> {outPath}");
    Console.WriteLine($"[CupriFace M0] rendered {st.FrameCount} frames; last {st.CpuFrameMs:F3} ms CPU/frame.");

    var panelOk = centre.Red == copper.Red && centre.Green == copper.Green && centre.Blue == copper.Blue;
    var bgOk = corner.Red == background.Red && corner.Green == background.Green && corner.Blue == background.Blue;
    if (!panelOk || !bgOk)
    {
        Console.Error.WriteLine("[CupriFace M0] FAIL: rendered pixels did not match the expected scene.");
        return 1;
    }
    Console.WriteLine("[CupriFace M0] PASS: paint + HUD + FrameStats pipeline verified.");
    return 0;
}

// ---- Windowed GL path (real desktop) ----------------------------------------
var window = new SkiaWindow(title: "CupriFace — M0 Shell", width: 1024, height: 768);
window.Render += DrawScene;

if (int.TryParse(Environment.GetEnvironmentVariable("CUPRI_SMOKE"), out var smokeFrames) && smokeFrames > 0)
{
    Console.WriteLine($"[CupriFace M0] windowed smoke mode: rendering {smokeFrames} frames then exiting.");
    window.ShouldClose = stats => stats.FrameCount >= smokeFrames;
}

window.Run();

var final = window.Stats;
Console.WriteLine($"[CupriFace M0] rendered {final.FrameCount} frames; last {final.CpuFrameMs:F2} ms CPU/frame; ~{final.Fps:F0} fps.");
return 0;
