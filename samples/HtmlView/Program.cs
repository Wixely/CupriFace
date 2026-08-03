using CupriFace;
using SkiaSharp;

// CupriFace M1 demo: render a real HTML + CSS document (nested flex, block flow,
// text, colours, borders, padding, gap) to a PNG via the CPU-raster path.

const string html = """
<body>
  <div class="app">
    <div class="topbar">
      <span class="title">CupriFace</span>
      <div class="dot"></div>
      <span class="badge">M3</span>
    </div>
    <div class="body">
      <div class="sidebar">
        <div class="nav-item">Dashboard</div>
        <div class="nav-item active">Layout</div>
        <div class="nav-item">Styles</div>
        <div class="nav-item">Components</div>
      </div>
      <div class="content">
        <h1>Managed HTML + CSS</h1>
        <p class="lead">This screen is HTML and CSS laid out by a pure C# flexbox engine and painted with Skia. No browser, no JavaScript.</p>
        <div class="cards">
          <div class="card">Flexbox<br/>row &amp; column, grow, gap</div>
          <div class="card wide">Cascade &amp; inheritance from real CSS selectors</div>
          <div class="card">Word-wrapped text with per-line alignment</div>
        </div>
        <p class="i18n">HarfBuzz shaping: office difficult final — AV WoV kerning — Ελληνικά — Кириллица — العربية</p>
        <div class="tags">
          <div class="tag">flex-wrap</div><div class="tag">gap</div><div class="tag">grow</div>
          <div class="tag">shrink</div><div class="tag">justify</div><div class="tag">align</div>
          <div class="tag">absolute</div><div class="tag">overflow</div><div class="tag">radius</div>
          <div class="tag">border</div><div class="tag">cascade</div><div class="tag">inherit</div>
        </div>
      </div>
    </div>
  </div>
</body>
""";

const string css = """
.app { display:flex; flex-direction:column; height:600px; font-family:sans-serif; }
.topbar { position:relative; display:flex; align-items:center; justify-content:space-between;
          background:#B87333; padding:16px 24px; }
.dot { position:absolute; top:10px; left:120px; width:10px; height:10px;
       background:#7CFC00; border-radius:5px; }
.title { color:white; font-size:22px; font-weight:bold; }
.badge { color:#B87333; background:white; font-weight:bold; padding:4px 10px; border-radius:12px; }
.body { display:flex; flex:1; }
.sidebar { width:210px; background:#1e2430; padding:16px; display:flex; flex-direction:column; gap:8px; }
.nav-item { color:#c8d0dc; padding:10px 12px; border-radius:6px; font-size:15px; }
.nav-item.active { color:white; background:#2f3b4d; font-weight:bold; }
.content { flex:1; background:#f4f5f7; padding:28px; }
h1 { color:#1e2430; }
.lead { color:#48505c; font-size:16px; max-width:520px; margin-bottom:20px; }
.cards { display:flex; gap:16px; }
.card { flex:1; background:white; border:2px steelblue; border-radius:8px;
        padding:16px; color:#2b3240; font-size:14px; }
.card.wide { flex:2; background:#eef4fb; }
.i18n { color:#48505c; font-size:16px; margin-top:20px; }
.tags { display:flex; flex-wrap:wrap; gap:8px; width:360px; margin-top:16px;
        padding:12px; background:#e9edf2; border-radius:8px; }
.tag { background:#B87333; color:white; font-size:13px; padding:4px 10px; border-radius:10px; }
""";

using var doc = CupriDocument.Load(html, css);

const int w = 900, h = 600;
using var image = doc.RenderToImage(w, h, new SKColor(0x12, 0x12, 0x14));

var outPath = Path.Combine(Environment.CurrentDirectory, "m1-html.png");
using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
using (var fs = File.OpenWrite(outPath))
    data.SaveTo(fs);

Console.WriteLine($"[CupriFace M1] rendered {w}x{h} HTML document -> {outPath}");
