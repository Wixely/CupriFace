using CupriFace;
using CupriFace.Resources;
using SkiaSharp;

namespace CupriFace.Demo;

/// <summary>
/// A gallery of the first-party control set — icons + v1 controls. A portable
/// <see cref="CupriApp"/>, so the desktop Viewer and the web hosts show the identical set.
/// </summary>
public sealed class ControlsApp : CupriApp
{
    public override string Title => "CupriFace — Controls";
    public override int Width => 800;
    public override int Height => 720;
    public override SKColor Background => new(0xf4, 0xf5, 0xf7);

    // Markup and styles are editable files under Assets/, embedded at compile time (typed via `Assets`).
    protected override CupriSource MarkupSource => Assets.ControlsApp.Html;
    protected override CupriSource StyleSource => Assets.ControlsApp.Css;
}
