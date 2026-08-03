using CupriFace;
using SkiaSharp;

// Simplified bidi: LTR lines with embedded RTL (Arabic/Hebrew) runs, each shaped in its
// own direction and reordered to visual order.

const string html = """
<body>
  <div class="p">
    <div class="line">English then العربية then more English</div>
    <div class="line">Hebrew word שלום inside a sentence</div>
    <div class="line">Mixed: hello العربية and שלום end</div>
  </div>
</body>
""";

const string css = """
.p { padding:26px; background:#12141a; font-family:sans-serif; }
.line { color:#e6e9f0; font-size:22px; margin-bottom:16px; }
""";

using var doc = CupriDocument.Load(html, css);
using var image = doc.RenderToImage(680, 220, new SKColor(0x12, 0x14, 0x1a));

var outPath = Path.Combine(Environment.CurrentDirectory, "bidi.png");
using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
using (var fs = File.OpenWrite(outPath))
    data.SaveTo(fs);
Console.WriteLine($"[CupriFace] bidi mixed LTR/RTL -> {outPath}");
