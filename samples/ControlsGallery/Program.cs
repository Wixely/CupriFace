using CupriFace;
using CupriFace.Accessibility;
using CupriFace.Binding;
using CupriFace.Components;
using SkiaSharp;

// CupriFace M5 demo: author custom elements (<cupri-slider>, <cupri-switch>,
// <cupri-progress>, <cupri-button>, <cupri-badge>) bound to a C# model. The registry
// expands each into themed, accessible primitive subtrees (DESIGN.md §10).

var settings = new Settings
{
    Volume = 72,
    Brightness = 45,
    Notifications = true,
    DarkMode = false,
    Download = 63,
};

const string html = """
<body>
  <div class="panel">
    <div class="titlebar">
      <span class="title">Settings</span>
      <cupri-badge>CupriFace M5</cupri-badge>
    </div>

    <div class="field">
      <span class="label">Volume</span>
      <cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider>
      <span class="val">{{Volume}}</span>
    </div>
    <div class="field">
      <span class="label">Brightness</span>
      <cupri-slider min="0" max="100" value="{{Brightness}}"></cupri-slider>
      <span class="val">{{Brightness}}</span>
    </div>

    <div class="row">
      <span class="label">Notifications</span>
      <cupri-switch checked="{{Notifications}}"></cupri-switch>
    </div>
    <div class="row">
      <span class="label">Dark mode</span>
      <cupri-switch checked="{{DarkMode}}"></cupri-switch>
    </div>

    <div class="field">
      <span class="label">Downloading</span>
      <cupri-progress value="{{Download}}" max="100"></cupri-progress>
    </div>

    <div class="actions">
      <cupri-button variant="ghost">Cancel</cupri-button>
      <cupri-button>Save changes</cupri-button>
    </div>
  </div>
</body>
""";

const string css = """
body { background:#e7eaf0; }
.panel { width:520px; background:white; border-radius:16px; padding:26px; font-family:sans-serif;
         margin:24px; }
.titlebar { display:flex; align-items:center; justify-content:space-between; margin-bottom:22px; }
.title { font-size:23px; font-weight:bold; color:#1e2430; }
.field { display:flex; align-items:center; margin-bottom:16px; }
.row { display:flex; align-items:center; justify-content:space-between; margin-bottom:16px; }
.label { width:150px; color:#48505c; font-size:15px; }
.val { width:40px; text-align:right; color:#1e2430; font-weight:bold; font-size:15px; }
cupri-slider { flex:1; }
cupri-progress { flex:1; }
.actions { display:flex; justify-content:flex-end; gap:12px; margin-top:6px; }
""";

using var doc = CupriDocument
    .Load(html, css)
    .UseComponents(ComponentRegistry.Default())
    .Bind(settings);

const int w = 568, h = 470;
using var image = doc.RenderToImage(w, h, new SKColor(0xe7, 0xea, 0xf0));

var outPath = Path.Combine(Environment.CurrentDirectory, "m5-controls.png");
using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
using (var fs = File.OpenWrite(outPath))
    data.SaveTo(fs);

// The platform-neutral semantics tree (§5) — what a screen reader would traverse.
var a11y = doc.BuildAccessibilityTree(w, h);
Console.WriteLine("[CupriFace M5/M7] accessibility semantics tree:");
Console.Write(AccessibilityTree.Dump(a11y));
Console.WriteLine($"[CupriFace] rendered -> {outPath}");

[CupriBindable]
sealed partial class Settings
{
    public int Volume { get; set; }
    public int Brightness { get; set; }
    public bool Notifications { get; set; }
    public bool DarkMode { get; set; }
    public int Download { get; set; }
}
