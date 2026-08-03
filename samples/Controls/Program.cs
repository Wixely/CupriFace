using AngleSharp.Dom;
using CupriFace;
using CupriFace.Components;
using CupriFace.Demo;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

// Renders the ShowcaseApp the Viewer runs, clicking through tabs to verify sections,
// then exercises slider drag and menu-item hover headlessly.
var app = new ShowcaseApp();
using var doc = app.CreateDocument();
int w = app.Width, h = app.Height;
var bg = app.Background;

Snap(doc, w, h, bg, "showcase-controls.png");
Click(doc, w, h, bg, n => n.Element?.GetAttribute("data-section") == "layout");
Snap(doc, w, h, bg, "showcase-layout.png");
Click(doc, w, h, bg, n => n.Element?.GetAttribute("data-section") == "overlays");
Click(doc, w, h, bg, n => n.Element?.ClassList.Contains("act-dialog") == true);
Snap(doc, w, h, bg, "showcase-overlays.png");

// --- slider drag: press at ~30%, drag to ~80% ---
var drag = new DragModel { Volume = 10 };
using (var sdoc = CupriDocument
    .Load("<body><cupri-slider min='0' max='100' value='{{Volume}}' style='width:220px;margin:24px'></cupri-slider></body>", "")
    .UseComponents(ComponentRegistry.Default()).Bind(drag))
{
    using (var _ = sdoc.RenderToImage(280, 90)) { }
    var slider = Find(sdoc.Root, n => n.Element?.GetAttribute("role") == "slider");
    if (slider is not null)
    {
        var b = HitTesting.AbsoluteBox(slider);
        sdoc.DispatchClick(b.X + b.W * 0.30f, b.Y + b.H / 2);
        sdoc.DispatchPointerMove(b.X + b.W * 0.80f, b.Y + b.H / 2);
        sdoc.DispatchPointerUp(b.X + b.W * 0.80f, b.Y + b.H / 2);
    }
    Console.WriteLine($"[CupriFace] slider drag → Volume={drag.Volume} (expect ~72–80)");
}

// --- menu-item hover: highlight follows the pointer ---
using (var hdoc = CupriDocument
    .Load("<body><div style='padding:16px'><cupri-menu label='File' open='true'>" +
          "<cupri-menu-item>Download</cupri-menu-item><cupri-menu-item>Delete</cupri-menu-item></cupri-menu></div></body>", "")
    .UseComponents(ComponentRegistry.Default()))
{
    HoverSnap(hdoc, "hover-before.png", null);
    var item = Find(hdoc.Root, n => n.Element?.ClassList.Contains("cupri-menu-item") == true);
    (float x, float y)? at = item is null ? null : (HitTesting.AbsoluteBox(item).X + 24, HitTesting.AbsoluteBox(item).Y + 14);
    HoverSnap(hdoc, "hover-after.png", at);
}
Console.WriteLine("[CupriFace] rendered showcase + drag/hover checks.");

static void HoverSnap(CupriDocument doc, string name, (float x, float y)? hover)
{
    using (var _ = doc.RenderToImage(260, 170)) { }   // lay out first
    if (hover is { } p) doc.DispatchPointerMove(p.x, p.y);
    using var image = doc.RenderToImage(260, 170, SKColors.White);
    Save(image, name);
}

static void Snap(CupriDocument doc, int w, int h, SKColor bg, string name)
{
    using var image = doc.RenderToImage(w, h, bg);
    Save(image, name);
}

static void Save(SKImage image, string name)
{
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(Path.Combine(Environment.CurrentDirectory, name));
    data.SaveTo(fs);
    Console.WriteLine($"[CupriFace] {name}");
}

static void Click(CupriDocument doc, int w, int h, SKColor bg, Func<RenderNode, bool> match)
{
    using var _ = doc.RenderToImage(w, h, bg);
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

sealed class DragModel
{
    public int Volume { get; set; }
}
