using CupriFace;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

// Keyboard focus + tab order, headless: Tab moves focus across controls, typing reaches the
// focused text field, Space/Enter activate the focused checkbox/button, and a focus ring is
// drawn (focus-visible — only after Tab). No mouse involved.
var m = new Model();

const string html = """
<body><div class="page">
  <div class="row"><span class="lbl">Name</span>
    <cupri-textfield value="{{Name}}" placeholder="Name…"></cupri-textfield></div>
  <div class="row"><cupri-checkbox checked="{{Agree}}"></cupri-checkbox><span class="lbl">I agree</span></div>
  <div class="row"><cupri-button class="inc">Add one</cupri-button><span class="val">Count: {{Count}}</span></div>
</div></body>
""";
const string css = ".page{padding:26px;background:#f4f5f7;font-family:sans-serif;display:flex;flex-direction:column;gap:16px;} .row{display:flex;align-items:center;gap:12px;} .lbl{color:#48505c;} .val{color:#1e2430;font-weight:bold;}";

const int W = 460, H = 240;
using var doc = CupriDocument.Load(html, css).UseComponents(ComponentRegistry.Default()).Bind(m);
doc.OnClick(".inc", _ => m.Count++);
using (var _ = doc.RenderToImage(W, H)) { } // lay out

// Tab to the text field (stop 0) and type — proves the field is keyboard-focused.
doc.DispatchKey(null, EditKey.Tab);
foreach (var ch in "Ada") doc.DispatchKey(ch.ToString(), EditKey.None);
var typed = m.Name;                                  // expect "Ada"

// Tab to the checkbox (stop 1); Space toggles it.
doc.DispatchKey(null, EditKey.Tab);
Snap("kbd-checkbox.png");                             // focus ring on the checkbox
doc.DispatchKey(null, EditKey.Space);
var agreed = m.Agree;                                 // expect true

// Tab to the button (stop 2); Enter activates its click handler.
doc.DispatchKey(null, EditKey.Tab);
Snap("kbd-button.png");                               // focus ring on the button
doc.DispatchKey(null, EditKey.Enter);
var count = m.Count;                                  // expect 1

// Shift+Tab back to the checkbox; Space toggles it off again.
doc.DispatchKey(null, EditKey.ShiftTab);
doc.DispatchKey(null, EditKey.Space);
var unagreed = m.Agree;                               // expect false

Console.WriteLine($"[CupriFace] typed={typed} agreed={agreed} count={count} unagreed={unagreed}");
var pass = typed == "Ada" && agreed && count == 1 && !unagreed;
Console.WriteLine(pass
    ? "[CupriFace] PASS: Tab order, typing into focused field, Space/Enter activation, Shift+Tab."
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
    public bool Agree { get; set; }
    public int Count { get; set; }
}
