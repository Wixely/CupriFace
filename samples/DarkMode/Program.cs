using AngleSharp.Dom;
using CupriFace.Demo;
using CupriFace.Dom;
using CupriFace.Interaction;
using SkiaSharp;

// Toggles the ShowcaseApp's Dark mode switch and renders before/after — verifying the
// CSS custom-property theme (light tokens vs body.dark tokens).
var app = new ShowcaseApp();
using var doc = app.CreateDocument();

Snap("theme-light.png");
Click(n => n.Element?.GetAttribute("role") == "switch" && n.Element?.GetAttribute("data-bind-checked") == "DarkMode");
Snap("theme-dark.png");
Console.WriteLine("[CupriFace] rendered light + dark themes.");

void Snap(string name)
{
    using var image = doc.RenderToImage(app.Width, app.Height, app.Background);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(Path.Combine(Environment.CurrentDirectory, name));
    data.SaveTo(fs);
    Console.WriteLine($"[CupriFace] {name}");
}

void Click(Func<RenderNode, bool> match)
{
    using var _ = doc.RenderToImage(app.Width, app.Height, app.Background);
    var node = Find(doc.Root, match);
    if (node is null) { Console.WriteLine("  (target not found)"); return; }
    var b = HitTesting.AbsoluteBox(node);
    doc.DispatchClick(b.X + b.W / 2, b.Y + b.H / 2);
}

static RenderNode? Find(RenderNode n, Func<RenderNode, bool> match)
{
    if (match(n)) return n;
    foreach (var c in n.Children) { var f = Find(c, match); if (f is not null) return f; }
    return null;
}
