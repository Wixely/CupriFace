using CupriFace;
using CupriFace.Binding;
using SkiaSharp;

namespace CupriFace.Demo;

/// <summary>
/// One app that shows everything: a tabbed showcase of the control set, overlays, layout
/// (flex + grid), and motion — plus live binding, navigation, and interaction. A portable
/// <see cref="CupriApp"/>, so the desktop Viewer and the web hosts run the identical demo.
/// </summary>
public sealed class ShowcaseApp : CupriApp
{
    private readonly ShowcaseModel _model = new();

    public override string Title => "CupriFace — Showcase";
    public override int Width => 940;
    public override int Height => 720;
    public override SKColor Background => _model.DarkMode ? new SKColor(0x0f, 0x14, 0x20) : new SKColor(0xf4, 0xf5, 0xf7);
    public override object Model => _model;

    public override void Configure(CupriDocument doc)
    {
        doc.OnClick(".nav", e => { if (e.Element.GetAttribute("data-section") is { } s) _model.Section = s; });
        doc.OnClick(".act-dialog", _ => _model.DialogOpen = true);
        doc.OnClick(".act-toast", _ => _model.ShowToast = !_model.ShowToast);
        // Zoom via buttons — a slider would fight the live re-scale it triggers.
        doc.OnClick(".zoom-dec", _ => _model.ZoomPct = Math.Clamp(_model.ZoomPct - 10, 80, 200));
        doc.OnClick(".zoom-inc", _ => _model.ZoomPct = Math.Clamp(_model.ZoomPct + 10, 80, 200));
    }

    public override string Html => """
    <body class="{{ThemeClass}}">
      <div class="app">
        <div class="sidebar">
          <div class="brand">CupriFace</div>
          <div class="nav {{NavControls}}" data-section="controls">Controls</div>
          <div class="nav {{NavOverlays}}" data-section="overlays">Overlays</div>
          <div class="nav {{NavLayout}}" data-section="layout">Layout</div>
          <div class="nav {{NavMotion}}" data-section="motion">Motion</div>
          <div class="nav {{NavSettings}}" data-section="settings">Settings</div>
          <div class="tip">Resize the window to see scaling</div>
        </div>

        <div class="content">
          <!-- CONTROLS -->
          <div class="section" style="display:{{SecControls}}">
            <div class="title">Controls</div>
            <div class="row">
              <cupri-icon name="home"></cupri-icon><cupri-icon name="search"></cupri-icon>
              <cupri-icon name="bell"></cupri-icon><cupri-icon name="user"></cupri-icon>
              <cupri-icon name="heart" style="color:#e0245e"></cupri-icon>
              <cupri-icon name="star" style="color:#f5b301"></cupri-icon>
              <cupri-icon name="settings"></cupri-icon><cupri-icon name="trash"></cupri-icon>
            </div>
            <div class="row">
              <cupri-button>Primary</cupri-button>
              <cupri-button variant="ghost">Ghost</cupri-button>
              <cupri-icon-button icon="settings"></cupri-icon-button>
              <cupri-checkbox checked="{{Notifications}}"></cupri-checkbox>
              <span class="lbl">Notifications</span>
              <cupri-switch checked="{{DarkMode}}"></cupri-switch>
              <span class="lbl">Dark mode</span>
            </div>
            <div class="row">
              <span class="lbl">Size</span>
              <cupri-radio group="{{Size}}" value="small"></cupri-radio><span class="lbl">Small</span>
              <cupri-radio group="{{Size}}" value="medium"></cupri-radio><span class="lbl">Medium</span>
              <cupri-radio group="{{Size}}" value="large"></cupri-radio><span class="lbl">Large</span>
            </div>
            <div class="row">
              <span class="lbl">Volume</span>
              <cupri-slider min="0" max="100" value="{{Volume}}" style="width:220px"></cupri-slider>
              <span class="val">{{Volume}}</span>
            </div>
            <div class="row">
              <cupri-chip>Design</cupri-chip><cupri-chip closable="true">Removable</cupri-chip>
              <cupri-avatar initials="AM"></cupri-avatar><cupri-badge>NEW</cupri-badge>
              <cupri-spinner></cupri-spinner>
              <cupri-progress value="62" max="100" style="width:160px"></cupri-progress>
            </div>
            <cupri-alert type="success">Everything is working.</cupri-alert>
            <cupri-alert type="warning">This is a warning banner.</cupri-alert>
            <div class="row">
              <cupri-card style="width:150px"><cupri-stat label="Users" value="12.4k"></cupri-stat></cupri-card>
              <cupri-card style="width:150px"><cupri-stat label="Revenue" value="$8.9k"></cupri-stat></cupri-card>
              <cupri-card style="width:150px"><cupri-stat label="Uptime" value="99.9%"></cupri-stat></cupri-card>
            </div>
          </div>

          <!-- OVERLAYS -->
          <div class="section" style="display:{{SecOverlays}}">
            <div class="title">Overlays</div>
            <p class="sub">Top-layer dialog, dropdown menu, tooltip and toast.</p>
            <div class="row">
              <cupri-button class="act-dialog">Open dialog</cupri-button>
              <cupri-button variant="ghost" class="act-toast">Toggle toast</cupri-button>
              <cupri-menu label="Menu" open="{{MenuOpen}}">
                <cupri-menu-item icon="download">Download</cupri-menu-item>
                <cupri-menu-item icon="edit">Rename</cupri-menu-item>
                <cupri-menu-item icon="trash">Delete</cupri-menu-item>
              </cupri-menu>
              <cupri-tooltip text="A tooltip" open="true">
                <cupri-icon-button icon="info"></cupri-icon-button>
              </cupri-tooltip>
            </div>
          </div>

          <!-- LAYOUT -->
          <div class="section" style="display:{{SecLayout}}">
            <div class="title">Layout</div>
            <p class="sub">Flexbox with grow, and CSS Grid.</p>
            <div class="flexdemo">
              <div class="fb a">flex 1</div><div class="fb b">flex 2</div><div class="fb a">flex 1</div>
            </div>
            <div class="grid">
              <div class="gcell head">Header · span 3</div>
              <div class="gcell">A</div><div class="gcell">B</div><div class="gcell">C</div>
              <div class="gcell wide">Wide · span 2</div><div class="gcell">D</div>
            </div>
          </div>

          <!-- SETTINGS -->
          <div class="section" style="display:{{SecSettings}}">
            <div class="title">Settings</div>
            <p class="sub">Scaling controls how the UI reacts when the window resizes.</p>
            <div class="setrow">
              <cupri-radio group="{{Scaling}}" value="none"></cupri-radio>
              <div><div class="opt">None</div><div class="optd">Fixed design size; resizing reveals background, no reflow.</div></div>
            </div>
            <div class="setrow">
              <cupri-radio group="{{Scaling}}" value="responsive"></cupri-radio>
              <div><div class="opt">Responsive</div><div class="optd">Reflows to fill the window, like a web page.</div></div>
            </div>
            <div class="setrow">
              <cupri-radio group="{{Scaling}}" value="zoom"></cupri-radio>
              <div><div class="opt">Zoom {{ZoomPct}}%</div><div class="optd">Hard scale factor, like changing display DPI.</div></div>
            </div>
            <div class="setrow indent">
              <span class="lbl">Zoom</span>
              <cupri-icon-button icon="minus" class="zoom-dec"></cupri-icon-button>
              <span class="zoomval">{{ZoomPct}}%</span>
              <cupri-icon-button icon="plus" class="zoom-inc"></cupri-icon-button>
            </div>
            <div class="setrow">
              <cupri-radio group="{{Scaling}}" value="hybrid"></cupri-radio>
              <div><div class="opt">Hybrid zoom</div><div class="optd">Zoom to the smaller axis, reflow the longer one.</div></div>
            </div>
          </div>

          <!-- MOTION -->
          <div class="section" style="display:{{SecMotion}}">
            <div class="title">Motion</div>
            <p class="sub">CSS transforms and a @keyframes animation.</p>
            <div class="stage">
              <div class="box rot">rotate</div>
              <div class="box scaled">scale</div>
              <div class="box moved">move</div>
              <div class="box spin">spin</div>
            </div>
          </div>
        </div>
      </div>

      <cupri-dialog open="{{DialogOpen}}">
        <div class="dlg-title">Dialog</div>
        <div class="dlg-body">A modal in the top layer. Click the backdrop or OK to dismiss.</div>
        <div class="dlg-actions"><cupri-button data-cupri-dismiss="true">OK</cupri-button></div>
      </cupri-dialog>
      <cupri-toast style="display:{{ToastDisplay}}">Toast shown — click “Toggle toast” to hide.</cupri-toast>
    </body>
    """;

    public override string Css => """
    body { --cupri-bg:#f4f5f7; --cupri-surface:#ffffff; --cupri-text:#1e2430; --cupri-muted:#48505c;
           --cupri-border:#e6e9f0; --cupri-sidebar:#1e2430; --cupri-sidebar-text:#c8d0dc; --cupri-nav-active:#2f3b4d;
           background:var(--cupri-bg); }
    body.dark { --cupri-bg:#0f1420; --cupri-surface:#1b2233; --cupri-text:#e6e9f0; --cupri-muted:#8b93a7;
                --cupri-border:#2a3346; --cupri-sidebar:#0b0f18; --cupri-sidebar-text:#aeb6c6; --cupri-nav-active:#243049; }
    .app { display:flex; height:100%; font-family:sans-serif; }
    .sidebar { width:190px; background:var(--cupri-sidebar); padding:18px 14px; display:flex; flex-direction:column; gap:6px; }
    .brand { color:white; font-size:19px; font-weight:bold; margin-bottom:16px; }
    .nav { color:var(--cupri-sidebar-text); padding:10px 12px; border-radius:8px; font-size:15px; }
    .nav.active { background:var(--cupri-nav-active); color:white; font-weight:bold; }
    .tip { color:#6b7688; font-size:12px; margin-top:auto; }
    .content { flex:1; padding:26px; background:var(--cupri-bg); }
    .title { font-size:22px; font-weight:bold; color:var(--cupri-text); margin-bottom:14px; }
    .sub { color:var(--cupri-muted); font-size:14px; margin-bottom:16px; }
    .row { display:flex; align-items:center; flex-wrap:wrap; gap:14px; margin-bottom:14px; }
    .lbl { color:var(--cupri-muted); font-size:14px; }
    .val { color:var(--cupri-text); font-weight:bold; font-size:14px; width:34px; }
    cupri-alert { margin-bottom:10px; }

    .flexdemo { display:flex; gap:12px; margin-bottom:18px; }
    .fb { padding:20px; border-radius:10px; color:white; font-weight:bold; text-align:center; }
    .fb.a { flex:1; background:#B87333; } .fb.b { flex:2; background:#4682B4; }
    .grid { display:grid; grid-template-columns: repeat(3, 1fr); gap:12px; }
    .gcell { background:#1b2233; color:#e6e9f0; border-radius:10px; padding:16px; font-size:14px; }
    .gcell.head { grid-column: span 3; background:#B87333; color:white; font-weight:bold; }
    .gcell.wide { grid-column: span 2; background:#2f3b4d; }

    .stage { display:flex; gap:26px; align-items:center; height:150px; }
    .box { width:92px; height:92px; border-radius:14px; display:flex; align-items:center;
           justify-content:center; color:white; font-weight:bold; }
    .rot { background:#B87333; transform: rotate(18deg); }
    .scaled { background:#4682B4; transform: scale(1.2); }
    .moved { background:#7CFC00; color:#12141a; transform: translate(0px,-20px); }
    .spin { background:#FF7F50; animation: showcase-spin 1.4s linear infinite; }
    @keyframes showcase-spin { from { transform: rotate(0deg); } to { transform: rotate(360deg); } }

    .dlg-title { font-size:19px; font-weight:bold; color:#1e2430; margin-bottom:10px; }
    .dlg-body { color:#48505c; font-size:14px; margin-bottom:20px; }
    .dlg-actions { display:flex; justify-content:flex-end; }

    .setrow { display:flex; align-items:center; gap:12px; margin-bottom:14px; }
    .setrow.indent { margin-left:32px; margin-bottom:18px; }
    .opt { color:var(--cupri-text); font-size:15px; font-weight:bold; }
    .optd { color:var(--cupri-muted); font-size:13px; }
    .zoomval { color:var(--cupri-text); font-size:15px; font-weight:bold; width:56px; text-align:center; }
    """;

    public override PresentInfo Present(float w, float h) => _model.Scaling switch
    {
        "none" => new PresentInfo(Width, Height, 1f),                                  // fixed design size
        "zoom" => Zoomed(w, h, _model.ZoomPct / 100f),                                 // hard DPI-like scale
        "hybrid" => Zoomed(w, h, MathF.Min(w / Width, h / Height)),                    // fit the tighter axis, reflow the other
        _ => new PresentInfo(w, h, 1f),                                                // responsive
    };

    private static PresentInfo Zoomed(float w, float h, float z)
    {
        z = Math.Clamp(z, 0.25f, 4f);
        return new PresentInfo(w / z, h / z, z);
    }
}

[CupriBindable]
public sealed partial class ShowcaseModel
{
    public string Section { get; set; } = "controls";

    public string SecControls => Section == "controls" ? "block" : "none";
    public string SecOverlays => Section == "overlays" ? "block" : "none";
    public string SecLayout => Section == "layout" ? "block" : "none";
    public string SecMotion => Section == "motion" ? "block" : "none";
    public string SecSettings => Section == "settings" ? "block" : "none";
    public string NavControls => Section == "controls" ? "active" : "";
    public string NavOverlays => Section == "overlays" ? "active" : "";
    public string NavLayout => Section == "layout" ? "active" : "";
    public string NavMotion => Section == "motion" ? "active" : "";
    public string NavSettings => Section == "settings" ? "active" : "";

    public string Scaling { get; set; } = "none";
    public int ZoomPct { get; set; } = 100;
    public string ThemeClass => DarkMode ? "dark" : "";

    public int Volume { get; set; } = 60;
    public bool Notifications { get; set; } = true;
    public bool DarkMode { get; set; }
    public string Size { get; set; } = "medium";
    public bool DialogOpen { get; set; }
    public bool MenuOpen { get; set; }
    public bool ShowToast { get; set; }
    public string ToastDisplay => ShowToast ? "block" : "none";
}
