using AngleSharp.Dom;
using CupriFace;
using CupriFace.Demo;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

// Verifies the scaling modes by replicating the host: app.Present(window) → scale the
// canvas and lay out at the logical size. Navigates the Settings tab via real clicks.
var app = new ShowcaseApp();
using var doc = app.CreateDocument();

Nav("settings");                                     // open Settings
// The model's default is "responsive", not "none" — so these two used to be rendered in responsive
// mode and named after a mode they never showed. Selecting it explicitly is the difference between
// a sample that demonstrates fixed scaling and one that claims to.
PickRadio("none");
Present(940, 720, "scale-none-a.png");
Present(1220, 820, "scale-none-b.png");              // same fixed design size, extra background

PickRadio("responsive");
Present(1220, 520, "scale-responsive-wide.png");     // reflows wide + short
Present(620, 860, "scale-responsive-tall.png");      // reflows narrow + tall

PickRadio("zoom");
for (var i = 0; i < 6; i++)                           // 100% + 6×10 = 160%
    ClickCenter(n => n.Element?.GetAttribute("class")?.Contains("zoom-inc") == true);
Present(940, 720, "scale-zoom160.png");              // everything 1.6×, less fits

PickRadio("hybrid");
Present(1500, 720, "scale-hybrid.png");              // fit height, reflow width

void Present(int winW, int winH, string name)
{
    var p = app.Present(winW, winH);
    var info = new SKImageInfo(winW, winH, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    surface.Canvas.Clear(app.Background);
    surface.Canvas.Scale(p.Scale);
    doc.Render(surface.Canvas, p.LogicalWidth, p.LogicalHeight);
    surface.Canvas.Flush();
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(Path.Combine(Environment.CurrentDirectory, name));
    data.SaveTo(fs);
    Console.WriteLine($"[CupriFace] {name}: logical={p.LogicalWidth:F0}x{p.LogicalHeight:F0} scale={p.Scale:F2}");
}

void Nav(string section) => ClickCenter(n => n.Element?.GetAttribute("data-section") == section);
void PickRadio(string value) => ClickCenter(n => n.Element?.GetAttribute("role") == "radio" && n.Element?.GetAttribute("value") == value);

void ClickCenter(Func<RenderNode, bool> match)
{
    using var _ = doc.RenderToImage(940, 720, app.Background); // lay out at design size to hit-test
    var node = Find(doc.Root, match);
    if (node is null) return;
    var b = HitTesting.AbsoluteBox(node);
    doc.DispatchClick(b.X + b.W / 2, b.Y + b.H / 2);
}

static RenderNode? Find(RenderNode n, Func<RenderNode, bool> match)
{
    if (match(n)) return n;
    foreach (var c in n.Children) { var f = Find(c, match); if (f is not null) return f; }
    return null;
}
