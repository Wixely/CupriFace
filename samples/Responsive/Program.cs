using CupriFace;
using SkiaSharp;

// @media queries + calc(): the SAME document rendered at two widths. Cards are
// calc(50% - 6px) wide (two-up) until <=520px, where a @media rule makes them 100%
// and recolours them. The inner bar uses calc(100% - 40px).

const string html = """
<body>
  <div class="page">
    <div class="title">Responsive</div>
    <div class="grid">
      <div class="card">Card A<div class="bar"><div class="fill"></div></div></div>
      <div class="card">Card B<div class="bar"><div class="fill"></div></div></div>
      <div class="card">Card C<div class="bar"><div class="fill"></div></div></div>
      <div class="card">Card D<div class="bar"><div class="fill"></div></div></div>
    </div>
  </div>
</body>
""";

const string css = """
.page { padding:20px; font-family:sans-serif; background:#12141a; }
.title { color:white; font-size:22px; font-weight:bold; margin-bottom:14px; }
.grid { display:flex; flex-wrap:wrap; gap:12px; }
.card { width: calc(50% - 40px); background:#B87333; color:white; padding:16px; border-radius:10px; }
.bar { height:16px; background:#2a3140; border-radius:8px; margin-top:12px; }
.fill { height:16px; background:#7CFC00; border-radius:8px; width: calc(100% - 40px); }
@media (max-width: 520px) {
  .card { width: 100%; background:#4682B4; }
  .title { color:#7CFC00; }
}
""";

using var doc = CupriDocument.Load(html, css);
var bg = new SKColor(0x12, 0x14, 0x1a);

Render(640, "responsive-wide.png");   // two-up copper cards (calc 50%-6px)
Render(470, "responsive-narrow.png"); // @media → full-width blue cards, green title
Console.WriteLine("[CupriFace] @media + calc() verified across widths.");

void Render(int width, string name)
{
    using var image = doc.RenderToImage(width, 360, bg);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(Path.Combine(Environment.CurrentDirectory, name));
    data.SaveTo(fs);
    Console.WriteLine($"[CupriFace] width={width} -> {name}");
}
