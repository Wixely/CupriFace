using CupriFace;
using SkiaSharp;

// Grid completeness: minmax() column, grid-row: span 2 (row spanning), grid-auto-rows.

const string html = """
<body>
  <div class="g">
    <div class="cell side">Sidebar<br/>grid-row: span 2</div>
    <div class="cell">A</div>
    <div class="cell">B</div>
    <div class="cell">C</div>
    <div class="cell">D</div>
  </div>
</body>
""";

const string css = """
.g { display:grid; grid-template-columns: minmax(180px, 1fr) 1fr 1fr; grid-auto-rows:82px;
     gap:12px; padding:20px; background:#12141a; font-family:sans-serif; }
.cell { background:#1b2233; color:#cfd6e4; border-radius:10px; padding:14px; }
.side { grid-row: span 2; background:#B87333; color:white; }
""";

using var doc = CupriDocument.Load(html, css);
using var image = doc.RenderToImage(720, 250, new SKColor(0x12, 0x14, 0x1a));

var outPath = Path.Combine(Environment.CurrentDirectory, "grid-advanced.png");
using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
using (var fs = File.OpenWrite(outPath))
    data.SaveTo(fs);
Console.WriteLine($"[CupriFace] grid rowSpan + minmax -> {outPath}");
