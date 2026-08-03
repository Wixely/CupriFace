using CupriFace;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

// The remaining first-party native controls, driven headlessly: tabs, accordion, select,
// textarea, tree (+ popover/drawer/table rendered). Asserts each writes back to the model.
var m = new Model();

const string html = """
<body><div class="page">
  <cupri-tabs value="{{Tab}}">
    <cupri-tab id="overview" label="Overview">Overview panel.</cupri-tab>
    <cupri-tab id="settings" label="Settings">Settings panel.</cupri-tab>
  </cupri-tabs>

  <cupri-accordion>
    <cupri-accordion-item label="Details" open="{{AccOpen}}">Hidden details here.</cupri-accordion-item>
  </cupri-accordion>

  <div class="row"><span class="lbl">Size</span>
    <cupri-select value="{{Size}}" open="{{SizeOpen}}">
      <cupri-option value="small">Small</cupri-option>
      <cupri-option value="medium">Medium</cupri-option>
      <cupri-option value="large">Large</cupri-option>
    </cupri-select></div>

  <cupri-textarea value="{{Notes}}" placeholder="Notes…"></cupri-textarea>

  <cupri-tree>
    <cupri-tree-item label="Root" open="{{TreeOpen}}">
      <cupri-tree-item label="Child A"></cupri-tree-item>
      <cupri-tree-item label="Child B"></cupri-tree-item>
    </cupri-tree-item>
  </cupri-tree>

  <cupri-table>
    <cupri-row header><cupri-cell>Item</cupri-cell><cupri-cell>Qty</cupri-cell></cupri-row>
    <cupri-row><cupri-cell>Apple</cupri-cell><cupri-cell>3</cupri-cell></cupri-row>
    <cupri-row><cupri-cell>Pear</cupri-cell><cupri-cell>5</cupri-cell></cupri-row>
  </cupri-table>

  <cupri-popover label="Info" open="{{Pop}}">A popover panel.</cupri-popover>
</div></body>
""";
const string css = """
.page { padding:22px; background:#f4f5f7; font-family:sans-serif; display:flex; flex-direction:column; gap:16px; }
.row { display:flex; align-items:center; gap:12px; }
.lbl { width:60px; color:#48505c; }
""";

const int W = 560, H = 760;
using var doc = CupriDocument.Load(html, css).UseComponents(ComponentRegistry.Default()).Bind(m);
Snap("native-initial.png");

// Tabs: click the "Settings" header → bound Tab switches, active panel changes.
Click(n => n.Element?.GetAttribute("data-set-value") == "settings");
var tab = m.Tab;                                    // expect "settings"

// Accordion: click the header → open flag toggles on.
Click(n => n.Element?.ClassList.Contains("cupri-acc-header") == true);
var acc = m.AccOpen;                                // expect true

// Select: open it, then pick "large" → value set + dropdown closes.
Click(n => n.Element?.ClassList.Contains("cupri-select-trigger") == true);
var opened = m.SizeOpen;                            // expect true
Snap("native-select-open.png");
Click(n => n.Element?.GetAttribute("data-set-value") == "large");
var size = m.Size; var closed = !m.SizeOpen;        // expect "large", closed

// Textarea: focus, type two lines with Enter between → newline in the bound value.
Click(n => n.Element?.HasAttribute("data-multiline") == true);
foreach (var ch in "Line1") doc.DispatchKey(ch.ToString(), EditKey.None);
doc.DispatchKey(null, EditKey.Enter);
foreach (var ch in "Line2") doc.DispatchKey(ch.ToString(), EditKey.None);
var notes = m.Notes;                                // expect "Line1\nLine2"
Snap("native-textarea.png");

// Tree: collapse the root via its twist → open flag toggles off.
Click(n => n.Element?.ClassList.Contains("cupri-tree-twist") == true && n.Element.HasAttribute("data-cupri-toggle"));
var tree = m.TreeOpen;                              // expect false

Console.WriteLine($"[CupriFace] tab={tab} acc={acc} opened={opened} size={size} closed={closed} notes={notes.Replace("\n", "\\n")} tree={tree}");
var pass = tab == "settings" && acc && opened && size == "large" && closed
    && notes == "Line1\nLine2" && !tree;
Console.WriteLine(pass
    ? "[CupriFace] PASS: tabs, accordion, select, textarea (newline), tree all bound + interactive."
    : "[CupriFace] FAIL");
return pass ? 0 : 1;

void Click(Func<RenderNode, bool> match)
{
    using (var _ = doc.RenderToImage(W, H)) { }     // lay out current tree (host renders each frame)
    var n = Find(doc.Root, match);
    if (n is null) { Console.WriteLine("[CupriFace] (no match to click)"); return; }
    var b = HitTesting.AbsoluteBox(n);
    doc.DispatchClick(b.X + b.W / 2, b.Y + b.H / 2);
}

void Snap(string name)
{
    using var image = doc.RenderToImage(W, H, new SKColor(0xf4, 0xf5, 0xf7));
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(Path.Combine(Environment.CurrentDirectory, name));
    data.SaveTo(fs);
    Console.WriteLine($"[CupriFace] {name}");
}

static RenderNode? Find(RenderNode n, Func<RenderNode, bool> match)
{
    if (match(n)) return n;
    foreach (var c in n.Children) { var f = Find(c, match); if (f is not null) return f; }
    return null;
}

sealed class Model
{
    public string Tab { get; set; } = "overview";
    public bool AccOpen { get; set; }
    public string Size { get; set; } = "medium";
    public bool SizeOpen { get; set; }
    public string Notes { get; set; } = "";
    public bool TreeOpen { get; set; } = true;
    public bool Pop { get; set; }
}
