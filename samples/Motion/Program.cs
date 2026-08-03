using CupriFace;
using SkiaSharp;

// Transforms + @keyframes animation. Static transforms (rotate/scale/translate) stay
// fixed; the "spin" box is animated — rendered at three times to show progression.

const string html = """
<body>
  <div class="stage">
    <div class="box rot">rotate</div>
    <div class="box scaled">scale</div>
    <div class="box moved">move</div>
    <div class="box spin">spin</div>
  </div>
</body>
""";

const string css = """
.stage { display:flex; gap:26px; padding:40px; background:#12141a; align-items:center; height:170px; font-family:sans-serif; }
.box { width:96px; height:96px; border-radius:14px; display:flex; align-items:center; justify-content:center; color:white; font-weight:bold; }
.rot { background:#B87333; transform: rotate(20deg); }
.scaled { background:#4682B4; transform: scale(1.25); }
.moved { background:#7CFC00; color:#12141a; transform: translate(0px,-24px); }
.spin { background:#FF7F50; animation: spin 2s linear infinite; }
@keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }
""";

using var doc = CupriDocument.Load(html, css);
var bg = new SKColor(0x12, 0x14, 0x1a);

foreach (var (t, name) in new[] { (0.0, "motion-t0.png"), (0.5, "motion-t1.png"), (1.0, "motion-t2.png") })
{
    doc.Animate(t); // spin: 2s period → t=0,0.5,1.0 → 0°, 90°, 180°
    using var image = doc.RenderToImage(560, 250, bg);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(Path.Combine(Environment.CurrentDirectory, name));
    data.SaveTo(fs);
    Console.WriteLine($"[CupriFace] t={t}s -> {name}");
}
Console.WriteLine("[CupriFace] static transforms fixed; 'spin' box rotates across frames.");
