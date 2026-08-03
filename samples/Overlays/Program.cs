using CupriFace;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

// Overlays: a modal dialog (backdrop + centred panel, top layer) over dimmed content,
// plus a toast pinned bottom-right — all via position:fixed + the top-layer painter.

const string html = """
<body>
  <div class="page">
    <h1>Documents</h1>
    <p class="sub">Background content sits under the dialog and is dimmed by its backdrop.</p>
    <div class="row">
      <cupri-card style="width:190px">Report Q3</cupri-card>
      <cupri-card style="width:190px">Budget.xlsx</cupri-card>
      <cupri-card style="width:190px">Notes.md</cupri-card>
    </div>

    <cupri-dialog open="true">
      <div class="dlg-title">Delete file?</div>
      <div class="dlg-body">This action cannot be undone. “Budget.xlsx” will be permanently removed.</div>
      <div class="dlg-actions">
        <cupri-button variant="ghost">Cancel</cupri-button>
        <cupri-button>Delete</cupri-button>
      </div>
    </cupri-dialog>

    <cupri-toast>Changes synced.</cupri-toast>
  </div>
</body>
""";

const string css = """
.page { padding:26px; background:#f4f5f7; font-family:sans-serif; height:470px; }
h1 { color:#1e2430; }
.sub { color:#48505c; font-size:14px; margin-top:4px; }
.row { display:flex; gap:14px; margin-top:18px; }
.dlg-title { font-size:19px; font-weight:bold; color:#1e2430; margin-bottom:10px; }
.dlg-body { color:#48505c; font-size:14px; margin-bottom:20px; }
.dlg-actions { display:flex; justify-content:flex-end; gap:10px; }
""";

Render(html, css, 720, 470, "overlays.png");

// Anchored popups: a dropdown menu below its trigger and a tooltip above an icon button.
const string popupsHtml = """
<body>
  <div class="page">
    <div class="bar">
      <cupri-menu label="File" open="true">
        <cupri-menu-item icon="download">Download</cupri-menu-item>
        <cupri-menu-item icon="edit">Rename</cupri-menu-item>
        <cupri-menu-item icon="trash">Delete</cupri-menu-item>
      </cupri-menu>
      <cupri-tooltip text="Open settings" open="true">
        <cupri-icon-button icon="settings"></cupri-icon-button>
      </cupri-tooltip>
    </div>
  </div>
</body>
""";
const string popupsCss = """
.page { padding:26px; background:#f4f5f7; font-family:sans-serif; height:290px; }
.bar { display:flex; align-items:center; gap:16px; margin-top:60px; }
""";
Render(popupsHtml, popupsCss, 520, 320, "popups.png");

// --- interaction: click backdrop closes the dialog; click trigger toggles the menu ---
var prefs = new Prefs { DialogOpen = true, MenuOpen = false };
const string interactHtml = """
<body><div style="padding:20px">
  <cupri-menu label="File" open="{{MenuOpen}}"><cupri-menu-item>New</cupri-menu-item></cupri-menu>
  <cupri-dialog open="{{DialogOpen}}"><div>Confirm?</div></cupri-dialog>
</div></body>
""";
using var idoc = CupriDocument.Load(interactHtml, "").UseComponents(ComponentRegistry.Default()).Bind(prefs);

// Click a backdrop corner (outside the centred panel) to dismiss.
using (var _ = idoc.RenderToImage(420, 300))
    Console.WriteLine($"  click backdrop corner -> handled={idoc.DispatchClick(20, 20)}");
ClickAttr(idoc, "data-cupri-toggle");  // menu trigger → open menu

Console.WriteLine($"[CupriFace] after clicks: DialogOpen={prefs.DialogOpen}, MenuOpen={prefs.MenuOpen}");
var pass = !prefs.DialogOpen && prefs.MenuOpen;
Console.WriteLine(pass ? "[CupriFace] PASS: backdrop dismissed dialog; trigger opened menu." : "[CupriFace] FAIL");

static void ClickAttr(CupriDocument doc, string attr)
{
    using var _ = doc.RenderToImage(420, 300); // lay out before hit-testing
    var node = Find(doc.Root, attr);
    if (node is null) { Console.WriteLine($"  ({attr} not found)"); return; }
    var b = HitTesting.AbsoluteBox(node);
    Console.WriteLine($"  click {attr} -> handled={doc.DispatchClick(b.X + b.W / 2, b.Y + b.H / 2)}");
}

static RenderNode? Find(RenderNode n, string attr)
{
    if (n.Element?.HasAttribute(attr) == true) return n;
    foreach (var c in n.Children) { var f = Find(c, attr); if (f is not null) return f; }
    return null;
}

static void Render(string html, string css, int w, int h, string name)
{
    using var doc = CupriDocument.Load(html, css).UseComponents(ComponentRegistry.Default());
    using var image = doc.RenderToImage(w, h, new SKColor(0xf4, 0xf5, 0xf7));
    var outPath = Path.Combine(Environment.CurrentDirectory, name);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(outPath);
    data.SaveTo(fs);
    Console.WriteLine($"[CupriFace] {name}");
}

sealed class Prefs
{
    public bool DialogOpen { get; set; }
    public bool MenuOpen { get; set; }
}
