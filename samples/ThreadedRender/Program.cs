using CupriFace;
using CupriFace.Threading;
using SkiaSharp;

// Render-thread split (§7.2): the main thread lays out + builds immutable DisplayList
// snapshots and commits them; a dedicated render thread rasterises them to a CPU surface.
// Verifies the two run on different threads and frames come out the render side.

const string html = """
<body><div class="p"><div class="t">Threaded render</div>
<div class="s">Layout + commit on the UI thread; raster on a dedicated render thread.</div></div></body>
""";
const string css = """
.p { padding:30px; background:#12141a; font-family:sans-serif; }
.t { color:#B87333; font-size:28px; font-weight:bold; margin-bottom:10px; }
.s { color:#cfd6e4; font-size:16px; }
""";

const int w = 620, h = 160;
var bg = new SKColor(0x12, 0x14, 0x1a);
using var doc = CupriDocument.Load(html, css);

var mainThread = Environment.CurrentManagedThreadId;
var renderThread = -1;
var presented = 0;
var frameReady = new ManualResetEventSlim(false);
var outPath = Path.Combine(Environment.CurrentDirectory, "threaded.png");

using var renderer = new ThreadedRenderer(image =>
{
    renderThread = Environment.CurrentManagedThreadId;
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(outPath, data.ToArray());
    Interlocked.Increment(ref presented);
    frameReady.Set();
});

for (var i = 0; i < 3; i++)
{
    var snapshot = doc.BuildDisplayList(w, h); // layout + build on THIS (main) thread
    renderer.Commit(snapshot, w, h, bg);        // rasterised on the render thread
    frameReady.Wait(2000);
    frameReady.Reset();
}

Console.WriteLine($"[CupriFace] main thread = {mainThread}, render thread = {renderThread}");
Console.WriteLine($"[CupriFace] frames rasterised by render thread = {renderer.FramesRendered}, snapshot -> {outPath}");
var pass = renderThread != -1 && renderThread != mainThread && renderer.FramesRendered >= 1;
Console.WriteLine(pass
    ? "[CupriFace] PASS: commit on UI thread, raster on a separate render thread."
    : "[CupriFace] FAIL: render thread did not run.");
return pass ? 0 : 1;
