using CupriFace;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Interaction;

// Agent debug channel: drive a form headlessly, then print doc.DebugDump() — the JSON an
// AI agent reads to diagnose a live form (layout boxes, focus/caret, bound values, a11y).
var form = new Form();

const string html = """
<body>
  <div class="page">
    <div class="row"><span class="lbl">Name</span>
      <cupri-textfield value="{{Name}}" placeholder="Type your name…"></cupri-textfield></div>
    <div class="row"><span class="lbl">Quantity</span>
      <cupri-number value="{{Quantity}}" min="0" max="20" step="1"></cupri-number></div>
    <cupri-switch checked="{{Notify}}">Notifications</cupri-switch>
  </div>
</body>
""";
const string css = ".page{padding:24px;font-family:sans-serif;} .row{display:flex;align-items:center;gap:12px;margin-bottom:14px;} .lbl{width:90px;color:#48505c;}";

using var doc = CupriDocument.Load(html, css).UseComponents(ComponentRegistry.Default()).Bind(form);
using (var _ = doc.RenderToImage(460, 220)) { } // lay out

// Focus the number field and type an over-max value → invalid buffer, model keeps last good.
var num = Find(doc.Root, n => n.Element?.GetAttribute("role") == "spinbutton")!;
var b = HitTesting.AbsoluteBox(num);
doc.DispatchClick(b.X + b.W / 2, b.Y + b.H / 2);
foreach (var ch in "99") doc.DispatchKey(ch.ToString(), EditKey.None); // buffer "2099" > max

var dump = doc.DebugDump(460, 220);
Console.WriteLine(dump);

// Sanity checks an agent would rely on.
bool Has(string s) => dump.Contains(s, StringComparison.Ordinal);
var ok =
    Has("\"focus\"") && Has("\"key\": \"Quantity\"") && Has("\"buffer\": \"2099\"") &&
    Has("\"bufferValid\": false") && Has("\"invalid\"") &&        // over-max flagged
    Has("\"bindings\"") && Has("\"Name\"") && Has("\"Quantity\"") && // bound values present
    Has("\"a11y\"") && Has("spinbutton") && Has("\"box\"");         // semantics + layout boxes

Console.WriteLine(ok
    ? "\n[CupriFace] PASS: DebugDump exposes focus/buffer/validity, bindings, a11y, and layout boxes."
    : "\n[CupriFace] FAIL");
return ok ? 0 : 1;

static RenderNode? Find(RenderNode n, Func<RenderNode, bool> match)
{
    if (match(n)) return n;
    foreach (var c in n.Children) { var f = Find(c, match); if (f is not null) return f; }
    return null;
}

sealed class Form
{
    public string Name { get; set; } = "Ada";
    public int Quantity { get; set; } = 20;
    public bool Notify { get; set; } = true;
}
