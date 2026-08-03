using CupriFace;
using SkiaSharp;

// CSS Grid demo: repeat(4, 1fr) tracks, column spans, gap, content-sized rows,
// items stretched to their cells — laid out by the managed grid engine.

const string html = """
<body>
  <div class="dash">
    <div class="card header"><span class="title">Analytics</span><span class="sub">CupriFace · CSS Grid</span></div>

    <div class="card stat"><div class="n">12.4k</div><div class="l">Active users</div></div>
    <div class="card stat"><div class="n">98.2%</div><div class="l">Uptime</div></div>
    <div class="card stat"><div class="n">3.1s</div><div class="l">Avg latency</div></div>
    <div class="card stat"><div class="n">$8.9k</div><div class="l">Revenue</div></div>

    <div class="card wide">
      <div class="n">Traffic</div>
      <p class="body">A wide panel spanning three of the four columns via <b>grid-column: span 3</b>. Rows are sized to content and every cell stretches its item to fill.</p>
    </div>
    <div class="card">
      <div class="n">Top pages</div>
      <p class="body">/home<br/>/pricing<br/>/docs<br/>/blog</p>
    </div>

    <div class="card footer">grid-template-columns: repeat(4, 1fr) · gap · spans · auto rows</div>
  </div>
</body>
""";

const string css = """
.dash { display:grid; grid-template-columns: repeat(4, 1fr); gap:16px;
        padding:24px; background:#0f1420; font-family:sans-serif; }
.card { background:#1b2233; border-radius:12px; padding:18px; color:#e6e9f0; }
.header { grid-column: span 4; background:#B87333; display:flex;
          align-items:center; justify-content:space-between; }
.header .title { font-size:24px; font-weight:bold; color:white; }
.header .sub { color:#ffe8d2; font-size:14px; }
.stat .n { font-size:28px; font-weight:bold; color:white; }
.stat .l { color:#8b93a7; font-size:13px; margin-top:4px; }
.wide { grid-column: span 3; }
.wide .n, .card .n { font-size:18px; font-weight:bold; color:white; margin-bottom:8px; }
.body { color:#aab2c5; font-size:14px; }
.footer { grid-column: span 4; background:#141a28; color:#8b93a7; font-size:13px; }
""";

using var doc = CupriDocument.Load(html, css);

const int w = 920, h = 560;
using var image = doc.RenderToImage(w, h, new SKColor(0x0f, 0x14, 0x20));

var outPath = Path.Combine(Environment.CurrentDirectory, "grid.png");
using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
using (var fs = File.OpenWrite(outPath))
    data.SaveTo(fs);

Console.WriteLine($"[CupriFace] rendered CSS Grid dashboard -> {outPath}");
