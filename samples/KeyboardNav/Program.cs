using CupriFace;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

// A11y refinements, headless: arrow-key nav within a radio group, arrow-nudge a slider,
// focus-trap inside an open overlay, and Escape to close it. Keyboard only.
var m = new Model();

const string html = """
<body><div class="page">
  <div class="row"><span class="lbl">Name</span>
    <cupri-textfield value="{{Name}}" placeholder="Name…"></cupri-textfield></div>
  <div class="row"><span class="lbl">Size</span>
    <cupri-radio group="{{Size}}" value="small"></cupri-radio><span class="lbl">S</span>
    <cupri-radio group="{{Size}}" value="medium"></cupri-radio><span class="lbl">M</span>
    <cupri-radio group="{{Size}}" value="large"></cupri-radio><span class="lbl">L</span></div>
  <div class="row"><span class="lbl">Vol</span>
    <cupri-slider min="0" max="100" value="{{Vol}}" style="width:200px"></cupri-slider></div>
  <div class="row">
    <cupri-select value="{{Choice}}" open="{{SelOpen}}">
      <cupri-option value="a">Apple</cupri-option>
      <cupri-option value="b">Banana</cupri-option>
      <cupri-option value="c">Cherry</cupri-option>
    </cupri-select></div>
</div></body>
""";
const string css = ".page{padding:24px;background:#f4f5f7;font-family:sans-serif;display:flex;flex-direction:column;gap:14px;} .row{display:flex;align-items:center;gap:10px;} .lbl{color:#48505c;}";

const int W = 460, H = 300;
using var doc = CupriDocument.Load(html, css).UseComponents(ComponentRegistry.Default()).Bind(m);
using (var _ = doc.RenderToImage(W, H)) { }

// Tab to the field and type, then Tab to the first radio.
doc.DispatchKey(null, EditKey.Tab);
foreach (var ch in "Ada") doc.DispatchKey(ch.ToString(), EditKey.None);
doc.DispatchKey(null, EditKey.Tab);                 // → radio "small" (Size starts "small")

// Arrow within the radio group moves + selects.
doc.DispatchKey(null, EditKey.Down);                // → medium
doc.DispatchKey(null, EditKey.Down);                // → large
var size = m.Size;                                  // expect "large"
Snap("kbdnav-radio.png");

// Tab to the slider; Right arrow nudges the value by a step (range/20 = 5).
doc.DispatchKey(null, EditKey.Tab);                 // → slider
doc.DispatchKey(null, EditKey.Right);               // 50 → 55
doc.DispatchKey(null, EditKey.Right);               // 55 → 60
var vol = m.Vol;                                    // expect 60

// Tab to the select trigger; Enter opens it → focus is trapped in the listbox.
doc.DispatchKey(null, EditKey.Tab);                 // → select trigger
doc.DispatchKey(null, EditKey.Enter);               // open
var opened = m.SelOpen;                             // expect true
Snap("kbdnav-open.png");

// Down within the trapped listbox, then Enter picks that option (and closes).
doc.DispatchKey(null, EditKey.Down);                // option 0 → option 1
doc.DispatchKey(null, EditKey.Enter);               // pick "Banana"
var choice = m.Choice; var closedAfterPick = !m.SelOpen;   // expect "b", closed

// Re-open (Tab back to the trigger) and close with Escape.
for (var i = 0; i < 6; i++) doc.DispatchKey(null, EditKey.Tab); // land back on the select trigger
doc.DispatchKey(null, EditKey.Enter);               // open
var reopened = m.SelOpen;
doc.DispatchKey(null, EditKey.Escape);              // close via Escape
var escaped = !m.SelOpen;

Console.WriteLine($"[CupriFace] size={size} vol={vol} opened={opened} choice={choice} " +
    $"closedAfterPick={closedAfterPick} reopened={reopened} escaped={escaped}");
var pass = size == "large" && vol == 60 && opened && choice == "b" && closedAfterPick && reopened && escaped;
Console.WriteLine(pass
    ? "[CupriFace] PASS: radio arrows, slider nudge, overlay focus-trap + pick, Escape."
    : "[CupriFace] FAIL");
return pass ? 0 : 1;

void Snap(string name)
{
    using var image = doc.RenderToImage(W, H, new SKColor(0xf4, 0xf5, 0xf7));
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(Path.Combine(Environment.CurrentDirectory, name));
    data.SaveTo(fs);
    Console.WriteLine($"[CupriFace] {name}");
}

sealed class Model
{
    public string Name { get; set; } = "";
    public string Size { get; set; } = "small";
    public int Vol { get; set; } = 50;
    public string Choice { get; set; } = "a";
    public bool SelOpen { get; set; }
}
