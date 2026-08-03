using CupriFace;
using SkiaSharp;

// A fixed-height list whose content overflows (overflow:scroll). Rendered at scroll top,
// then after wheel-scrolling — content shifts and the scrollbar thumb tracks.
var rows = string.Concat(Enumerable.Range(1, 20)
    .Select(i => $"<div class='item {(i % 2 == 0 ? "alt" : "")}'>Row {i} — scrollable content</div>"));

var html = $"""
<body>
  <div class="page">
    <div class="h">Scrollable list (overflow: scroll)</div>
    <div class="list">{rows}</div>
    <div class="foot">Wheel over the list to scroll · thumb on the right</div>
  </div>
</body>
""";

const string css = """
.page { padding:24px; background:#f4f5f7; font-family:sans-serif; }
.h { font-size:18px; font-weight:bold; color:#1e2430; margin-bottom:12px; }
.list { height:300px; overflow:scroll; background:white; border-radius:12px; border:1px #e6e9f0; padding:8px; }
.item { padding:13px 16px; border-radius:8px; color:#1e2430; font-size:15px; }
.item.alt { background:#f4f6f9; }
.foot { color:#8b93a7; font-size:13px; margin-top:12px; }
""";

using var doc = CupriDocument.Load(html, css);
using (var _ = doc.RenderToImage(600, 440)) { }   // lay out first

Snap("scroll-top.png");
doc.DispatchWheel(300, 200, 300);                   // scroll down ~300px
Snap("scroll-mid.png");
doc.DispatchWheel(300, 200, 400);                   // clamps at the bottom
Snap("scroll-bottom.png");
Console.WriteLine("[CupriFace] rendered scroll-top / -mid / -bottom.");

void Snap(string name)
{
    using var image = doc.RenderToImage(600, 440, new SKColor(0xf4, 0xf5, 0xf7));
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(Path.Combine(Environment.CurrentDirectory, name));
    data.SaveTo(fs);
    Console.WriteLine($"[CupriFace] {name}");
}
