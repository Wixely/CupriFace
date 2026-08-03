using CupriFace;
using CupriFace.Binding;
using SkiaSharp;

namespace CupriFace.Demo;

/// <summary>
/// A complete CupriFace app defined once — markup, styles, model, and a click handler —
/// with zero platform code. `samples/Viewer` runs it on the desktop; `samples/Web` runs
/// the exact same class in the browser (WASM → canvas).
/// </summary>
public sealed class SettingsApp : CupriApp
{
    private readonly Settings _model = new()
    {
        Volume = 72,
        Brightness = 45,
        Notifications = true,
        DarkMode = false,
        Download = 63,
    };

    public override string Title => "CupriFace — Settings";
    public override int Width => 600;
    public override int Height => 520;
    public override SKColor Background => new(0xe7, 0xea, 0xf0);
    public override object Model => _model;

    public override void Configure(CupriDocument doc) =>
        doc.OnClick(".save", _ => _model.Download = 100); // "Save" completes the download bar

    public override string Html => """
    <body>
      <div class="panel">
        <div class="titlebar"><span class="title">Settings</span>
          <cupri-badge>one app · desktop + web</cupri-badge></div>
        <div class="field"><span class="label">Volume</span>
          <cupri-slider min="0" max="100" value="{{Volume}}"></cupri-slider><span class="val">{{Volume}}</span></div>
        <div class="field"><span class="label">Brightness</span>
          <cupri-slider min="0" max="100" value="{{Brightness}}"></cupri-slider><span class="val">{{Brightness}}</span></div>
        <div class="row"><span class="label">Notifications</span>
          <cupri-switch checked="{{Notifications}}"></cupri-switch></div>
        <div class="row"><span class="label">Dark mode</span>
          <cupri-switch checked="{{DarkMode}}"></cupri-switch></div>
        <div class="field"><span class="label">Downloading</span>
          <cupri-progress value="{{Download}}" max="100"></cupri-progress></div>
        <div class="actions">
          <cupri-button variant="ghost">Cancel</cupri-button>
          <cupri-button class="save">Save changes</cupri-button>
        </div>
      </div>
    </body>
    """;

    public override string Css => """
    body { background:#e7eaf0; }
    .panel { width:520px; background:white; border-radius:16px; padding:26px; font-family:sans-serif; margin:24px; }
    .titlebar { display:flex; align-items:center; justify-content:space-between; margin-bottom:22px; }
    .title { font-size:23px; font-weight:bold; color:#1e2430; }
    .field { display:flex; align-items:center; margin-bottom:16px; }
    .row { display:flex; align-items:center; justify-content:space-between; margin-bottom:16px; }
    .label { width:150px; color:#48505c; font-size:15px; }
    .val { width:40px; text-align:right; color:#1e2430; font-weight:bold; font-size:15px; }
    cupri-slider { flex:1; }
    cupri-progress { flex:1; }
    .actions { display:flex; justify-content:flex-end; gap:12px; margin-top:6px; }
    """;
}

[CupriBindable]
public sealed partial class Settings
{
    public int Volume { get; set; }
    public int Brightness { get; set; }
    public bool Notifications { get; set; }
    public bool DarkMode { get; set; }
    public int Download { get; set; }
}
