using CupriFace;
using CupriFace.Binding;
using CupriFace.Resources;
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

    // Markup and styles are editable files under Assets/, embedded at compile time (typed via `Assets`).
    protected override CupriSource MarkupSource => Assets.SettingsApp.Html;
    protected override CupriSource StyleSource => Assets.SettingsApp.Css;
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
