using CupriFace.Demo.ThreeD;
using CupriFace;
using CupriFace.Experiments.GlProbe.Host;
using CupriFace.Shell;

// The integration question, which is the one that could still have changed the design: does a 3D
// renderer fit the seam CupriFace already has, or would embedding one mean changing the engine?
//
// It fits. Nothing in src/ is touched. The renderer publishes SKImages through ISurfaceSource — the
// same lane <cupri-video> and <cupri-lottie> use — and the engine composites the teapot into an
// ordinary HTML document, under ordinary CSS, beside ordinary text.
//
// Run headless with --probe to get the assertions and timings without a window.

var headless = args.Contains("--probe");

var glb = Path.Combine(AppContext.BaseDirectory, "teapot.glb");
if (!File.Exists(glb)) { Console.WriteLine($"glprobe: FAIL asset missing: {glb}"); return 1; }

Gltf model;
try { model = Gltf.Load(File.ReadAllBytes(glb)); }
catch (Exception ex) { Console.WriteLine($"glprobe: FAIL load: {ex.Message}"); return 1; }
Console.WriteLine($"glprobe: {model.Primitives.Count} primitive(s), {model.VertexCount:n0} vertices, "
    + $"{model.TriangleCount:n0} triangles, {model.Images.Count} image(s)");

var app = new TeapotApp(model);

if (!headless)
{
    DesktopHost.Run(app);
    return 0;
}

// ---- headless assertion pass ------------------------------------------------------------------
// Render the document to an image the same way a test would, so the claim "the teapot is IN the
// document" is checked against pixels rather than a screenshot someone looked at.
var doc = CupriDocument.Load(app.Html, app.Css).Bind(app.Model!);
app.Configure(doc);

Console.WriteLine("glprobe: waiting for the first 3D frame…");
var waited = 0;
while (app.Surface?.CurrentFrame is null && waited < 8000) { Thread.Sleep(100); waited += 100; }
if (app.Surface?.CurrentFrame is null)
{
    Console.WriteLine($"glprobe: FAIL no frame after {waited} ms — surface status: {app.Surface?.Status}");
    return 1;
}
Console.WriteLine($"glprobe: first frame after {waited} ms; {app.Surface.GlVersion}");
Thread.Sleep(500);      // let a few frames go by so the timings are not all first-frame costs

const int W = 700, H = 520;
using (var image = doc.RenderToImage(W, H))
{
    using var bmp = SkiaSharp.SKBitmap.FromImage(image);
    // Count SATURATED pixels, not merely "not background". The first version of this check counted
    // anything that was neither white page nor near-black, and passed while the teapot was rendering
    // as a solid black silhouette — the dark stage behind it classified as text, so the "teapot"
    // count was really anti-aliased edges. Saturation is the property only the paint-splatter
    // texture has: the page is white, the text is grey, the stage is near-black, and every one of
    // those has a channel spread near zero.
    var saturated = 0; var text = 0; var page = 0;
    var reds = new bool[256];
    for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var c = bmp.GetPixel(x, y);
            int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
            int min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
            if (max - min > 40) { saturated++; reds[c.Red] = true; }
            else if (c.Red > 240 && c.Green > 240 && c.Blue > 240) page++;
            else if (c.Red < 90 && c.Green < 90 && c.Blue < 90) text++;
        }
    var teapot = saturated;
    var tones = 0; foreach (var t in reds) if (t) tones++;

    Console.WriteLine($"glprobe: composited {W}x{H} -> page {page:n0}px, text/stage {text:n0}px, SATURATED (textured teapot) {teapot:n0}px, distinct red levels {tones}");

    // Write the composited frame out, so the claim can be LOOKED at as well as counted.
    var outPng = Path.Combine(AppContext.BaseDirectory, "composited.png");
    using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90))
    using (var fs = File.Create(outPng))
        data.SaveTo(fs);
    Console.WriteLine($"glprobe: wrote {outPng}");

    // Thresholds set from what the FAILURE modes produce, not from what makes this pass. The black
    // silhouette this check was written to catch leaves saturation near zero — a black teapot on a
    // near-black stage has channel spread only on its anti-aliased edge — while a correct render
    // measures ~3,500 here. 1,500 sits between them with room either side. `tones` is the primary
    // signal regardless: a silhouette cannot produce 100+ distinct red levels no matter its size.
    var ok = teapot > 1500 && tones > 60 && text > 500 && page > 50000;
    Console.WriteLine($"glprobe: draw {app.Surface.LastDrawMs:F2} ms, readback {app.Surface.LastReadbackMs:F2} ms, "
        + $"to-SKImage {app.Surface.LastUploadMs:F2} ms, frames {app.Surface.Frames}");
    Console.WriteLine(ok
        ? "glprobe: PASS the teapot is composited inside a CupriFace document, beside live HTML text"
        : "glprobe: FAIL the document did not contain both a textured 3D surface and its own text");
    app.Dispose();
    return ok ? 0 : 1;
}

/// <summary>An ordinary CupriFace app. The only unusual line is the surface registration — the
/// element itself is a plain div wearing <c>data-cupri-surface</c>, the same attribute a Lottie or a
/// video carries, and everything around it is HTML and CSS the engine already understood.</summary>
internal sealed class TeapotApp(Gltf model) : CupriApp, IDisposable
{
    private const string Key = "teapot3d";
    private readonly Model _model = new();

    public TeapotSurface? Surface { get; private set; }

    public override string Title => "CupriFace — a 3D surface";
    public override int Width => 700;
    public override int Height => 520;
    public override object Model => _model;

    public override void Configure(CupriDocument doc)
    {
        // 512x512 offscreen regardless of the CSS box: the surface has a natural size and the engine
        // scales it, exactly as it does a video frame or a Lottie.
        Surface = new TeapotSurface(model, doc.Surfaces, 512, 512, Console.WriteLine);
        doc.Surfaces.Register(Key, Surface);
        doc.OnClick(".spin", _ => _model.Note = "the engine repainted because the surface asked it to");
    }

    public override string Html => """
        <body>
          <div class="wrap">
            <div class="title">A 3D viewport is just a surface</div>
            <p class="sub">The teapot on the right is OpenGL, rendered offscreen on its own context and
              published through <b>ISurfaceSource</b> — the same lane a video frame and a Lottie take.
              Nothing in the engine was changed to allow it.</p>
            <div class="row">
              <div class="stage">
                <div data-cupri-surface="teapot3d" class="viewport"></div>
              </div>
              <div class="col">
                <p class="lead">This text is laid out by the engine.</p>
                <p class="body">It wraps, it is styled by CSS, and it sits beside the 3D rather than
                  on top of a canvas that owns the window. The surface has a natural size of 512×512
                  and is scaled into whatever box the CSS gives it.</p>
                <cupri-button class="spin">Repaint</cupri-button>
                <p class="note">{{Note}}</p>
              </div>
            </div>
          </div>
        </body>
        """;

    public override string Css => """
        body { font-family:sans-serif; background:#ffffff; color:#1e2430; }
        .wrap { padding:22px 26px; }
        .title { font-size:20px; font-weight:bold; }
        .sub { color:#48505c; font-size:13px; margin:8px 0 18px; max-width:620px; }
        .row { display:flex; gap:24px; align-items:flex-start; }
        .stage { background:#0f1115; border-radius:10px; padding:8px; }
        .viewport { width:300px; height:300px; }
        .col { display:flex; flex-direction:column; gap:10px; max-width:300px; }
        .lead { font-size:15px; font-weight:bold; }
        .body { color:#48505c; font-size:13px; }
        .note { color:#b87333; font-size:12px; }
        """;

    public void Dispose() => Surface?.Dispose();
}

public sealed class Model
{
    public string Note { get; set; } = "";
}
