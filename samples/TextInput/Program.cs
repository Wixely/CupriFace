using CupriFace;
using CupriFace.Components;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

// Text input, headless: focus a field (click), type, backspace, insert, move caret —
// verifying the two-way bound string, the caret, and placeholder → value.
var form = new Form();

const string html = """
<body>
  <div class="page">
    <div class="lbl">Name</div>
    <cupri-textfield value="{{Name}}" placeholder="Type your name…"></cupri-textfield>
    <div class="bound">Bound value: “{{Name}}”</div>
  </div>
</body>
""";
const string css = """
.page { padding:28px; background:#f4f5f7; font-family:sans-serif; }
.lbl { color:#48505c; font-size:14px; margin-bottom:8px; }
.bound { color:#1e2430; font-size:15px; margin-top:18px; }
""";

using var doc = CupriDocument.Load(html, css).UseComponents(ComponentRegistry.Default()).Bind(form);
using (var _ = doc.RenderToImage(460, 180)) { }   // lay out

Snap("text-empty.png");                             // placeholder shown

// Focus the field, then type.
var field = Find(doc.Root, n => n.Element?.GetAttribute("role") == "textbox");
var b = HitTesting.AbsoluteBox(field!);
doc.DispatchClick(b.X + b.W / 2, b.Y + b.H / 2);
foreach (var ch in "Hello") doc.DispatchKey(ch.ToString(), EditKey.None);
Snap("text-typed.png");                             // "Hello" + caret

doc.DispatchKey(null, EditKey.Backspace);           // "Hell"
doc.DispatchKey("o!", EditKey.None);                // "Hello!"
doc.DispatchKey(null, EditKey.Home);                // caret → start
doc.DispatchKey("> ", EditKey.None);                // "> Hello!"
Snap("text-edited.png");

Console.WriteLine($"[CupriFace] bound value = \"{form.Name}\"");
Console.WriteLine(form.Name == "> Hello!"
    ? "[CupriFace] PASS: type / backspace / insert / caret-home all applied."
    : "[CupriFace] FAIL");
return form.Name == "> Hello!" ? 0 : 1;

void Snap(string name)
{
    using var image = doc.RenderToImage(460, 180, new SKColor(0xf4, 0xf5, 0xf7));
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

sealed class Form
{
    public string Name { get; set; } = "";
}
