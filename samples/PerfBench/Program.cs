using System.Diagnostics;
using CupriFace;
using CupriFace.Demo;
using SkiaSharp;

// Where does a frame's time go? Times the phases of the ShowcaseApp render pipeline at the
// viewer's size, so we know whether rebuild / layout+build / rasterize dominates (i.e. whether
// dirty-region rendering would actually help). Native numbers; WASM is materially slower, but
// the *relative* breakdown is what decides the optimization.
const int W = 940, H = 720;
var app = new ShowcaseApp();
using var doc = app.CreateDocument();
using var bitmap = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
using var canvas = new SKCanvas(bitmap);

double Time(int iters, Action a)
{
    a(); a(); // warm up
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < iters; i++) a();
    sw.Stop();
    return sw.Elapsed.TotalMilliseconds / iters;
}

const int N = 200;
var tRebuild = Time(N, () => doc.Refresh());                         // parse + bind + expand + style + tree
var tLayoutBuild = Time(N, () => doc.BuildDisplayList(W, H));        // layout + paint display-list build
var tRender = Time(N, () => { canvas.Clear(app.Background); doc.Render(canvas, W, H); }); // + rasterize
var tRasterOnly = tRender - tLayoutBuild;

Console.WriteLine($"ShowcaseApp @ {W}x{H} — ms per op (native, avg of {N}):");
Console.WriteLine($"  Refresh (full DOM rebuild)  : {tRebuild,7:F3} ms   ← runs on every click/keystroke");
Console.WriteLine($"  Layout + build display list : {tLayoutBuild,7:F3} ms");
Console.WriteLine($"  Rasterize (full {W}x{H})     : {tRasterOnly,7:F3} ms   ← what dirty-region would shrink");
Console.WriteLine($"  Full Render (layout+build+raster): {tRender,7:F3} ms");
Console.WriteLine($"  Interaction total (Refresh+Render): {tRebuild + tRender,7:F3} ms");
Console.WriteLine();
Console.WriteLine($"  Rebuild share of an interaction : {100 * tRebuild / (tRebuild + tRender),4:F0}%");
Console.WriteLine($"  Rasterize share of a render     : {100 * tRasterOnly / tRender,4:F0}%");

// Break the rebuild into phases to see what to optimize.
var phases = new Dictionary<string, double>();
var count = 0;
CupriDocument.ProfileHook = (name, ms) => { phases[name] = phases.GetValueOrDefault(name) + ms; if (name == "style+tree") count++; };
for (var i = 0; i < N; i++) doc.Refresh();
CupriDocument.ProfileHook = null;
Console.WriteLine($"\n  Rebuild phase breakdown (avg of {count}):");
foreach (var (name, ms) in phases.OrderByDescending(p => p.Value))
    Console.WriteLine($"    {name,-20}: {ms / count,7:F3} ms");

// Allocation per Refresh (WASM GC is much slower than native, so this matters there).
var g0 = GC.CollectionCount(0);
var a0 = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < N; i++) doc.Refresh();
var allocKb = (GC.GetAllocatedBytesForCurrentThread() - a0) / 1024.0 / N;
var gcs = GC.CollectionCount(0) - g0;
Console.WriteLine($"\n  Allocated per Refresh : {allocKb,7:F1} KB   ({gcs} gen0 GCs over {N} refreshes)");
