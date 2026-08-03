using AngleSharp.Dom;
using CupriFace;
using CupriFace.Binding;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

// Interaction demo (headless): simulate mouse clicks → hit-test → control behaviour
// → two-way write-back to the model → re-render. Verifies clicks actually work,
// with before/after snapshots and console assertions.

var settings = new Settings { Volume = 25, Brightness = 60, Notifications = false, DarkMode = true, Download = 40 };

const string html = """
<body>
  <div class="panel">
    <div class="field"><span class="label">Volume</span>
      <cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider><span class="val">{{Volume}}</span></div>
    <div class="row"><span class="label">Notifications</span>
      <cupri-switch checked="{{Notifications}}"></cupri-switch></div>
    <div class="row"><span class="label">Dark mode</span>
      <cupri-switch checked="{{DarkMode}}"></cupri-switch></div>
    <div class="actions"><cupri-button class="save">Save</cupri-button></div>
  </div>
</body>
""";

const string css = """
body { background:#e7eaf0; }
.panel { width:460px; background:white; border-radius:16px; padding:26px; font-family:sans-serif; margin:24px; }
.field { display:flex; align-items:center; margin-bottom:18px; }
.row { display:flex; align-items:center; justify-content:space-between; margin-bottom:18px; }
.label { width:150px; color:#48505c; font-size:15px; }
.val { width:40px; text-align:right; color:#1e2430; font-weight:bold; font-size:15px; }
cupri-slider { flex:1; }
.actions { display:flex; justify-content:flex-end; }
""";

var saved = false;
using var doc = CupriDocument.Load(html, css)
    .UseComponents(ComponentRegistry.Default())
    .Bind(settings);
doc.OnClick(".save", _ => saved = true);

const int w = 508, h = 320;
var bg = new SKColor(0xe7, 0xea, 0xf0);

Snapshot("interactive-before.png");
Console.WriteLine($"before: Volume={settings.Volume}, Notifications={settings.Notifications}, saved={saved}");

// 1) Click the Notifications switch → should toggle false → true.
ClickRole("switch", nth: 0, atRatioX: 0.5);
// 2) Layout is refreshed; render, then drag the Volume slider to ~80%.
Layout();
ClickRole("slider", nth: 0, atRatioX: 0.8);
// 3) Click the Save button → user handler sets saved=true.
Layout();
ClickSelector(".save");

Snapshot("interactive-after.png");
Console.WriteLine($"after:  Volume={settings.Volume}, Notifications={settings.Notifications}, saved={saved}");

var pass = settings.Notifications && settings.Volume is >= 70 and <= 90 && saved;
Console.WriteLine(pass
    ? "[CupriFace] PASS: switch toggled, slider moved via click, button handler fired."
    : "[CupriFace] FAIL: interaction did not produce expected state.");
return pass ? 0 : 1;

// ---- helpers ----
void Layout() { using var _ = doc.RenderToImage(w, h, bg); }

void Snapshot(string name)
{
    using var image = doc.RenderToImage(w, h, bg);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(Path.Combine(Environment.CurrentDirectory, name));
    data.SaveTo(fs);
}

void ClickRole(string role, int nth, double atRatioX)
{
    var node = FindByRole(doc.Root, role, ref nth);
    if (node is null) { Console.WriteLine($"  (no {role} found)"); return; }
    var box = HitTesting.AbsoluteBox(node);
    var x = box.X + (float)(atRatioX * box.W);
    var y = box.Y + box.H / 2f;
    Console.WriteLine($"  click {role} at ({x:F0},{y:F0}) -> handled={doc.DispatchClick(x, y)}");
}

void ClickSelector(string selector)
{
    var node = FindBySelector(doc.Root, selector);
    if (node is null) { Console.WriteLine($"  (no {selector} found)"); return; }
    var box = HitTesting.AbsoluteBox(node);
    Console.WriteLine($"  click {selector} -> handled={doc.DispatchClick(box.X + box.W / 2f, box.Y + box.H / 2f)}");
}

static RenderNode? FindByRole(RenderNode node, string role, ref int nth)
{
    if (node.Element?.GetAttribute("role") == role)
    {
        if (nth == 0) return node;
        nth--;
    }
    foreach (var c in node.Children)
    {
        var f = FindByRole(c, role, ref nth);
        if (f is not null) return f;
    }
    return null;
}

static RenderNode? FindBySelector(RenderNode node, string selector)
{
    if (node.Element is { } el)
    {
        try { if (el.Matches(selector)) return node; } catch { }
    }
    foreach (var c in node.Children)
    {
        var f = FindBySelector(c, selector);
        if (f is not null) return f;
    }
    return null;
}

[CupriBindable]
sealed partial class Settings
{
    public int Volume { get; set; }
    public int Brightness { get; set; }
    public bool Notifications { get; set; }
    public bool DarkMode { get; set; }
    public int Download { get; set; }
}
