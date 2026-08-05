using CupriFace;
using CupriFace.Shell;

// A transparent, frameless, always-on-top HUD that floats over the desktop — the same
// CupriApp model as every other sample, only with three flags flipped. The window has no
// background of its own: wherever the markup doesn't paint, the framebuffer stays fully
// transparent and the desktop (or the game / app behind it) shows through. This needs a
// compositing window manager (universal on Windows 8+/macOS/modern Linux); where none is
// present it degrades to an opaque window — the host environment's concern, not ours.
//
// No OS-specific code: transparency, frameless chrome and top-most are all portable
// Silk.NET window traits (GLFW under the hood).
DesktopHost.Run(new HudApp());

sealed class HudApp : CupriApp
{
    public override string Title => "CupriFace — Transparent HUD";
    public override int Width => 340;
    public override int Height => 200;

    // The three flags that make this an overlay rather than an ordinary window.
    public override bool Transparent => true;
    public override bool Frameless => true;
    public override bool TopMost => true;

    // Nothing paints the body, so the corners stay see-through; only the rounded card and its
    // contents are drawn. A translucent (alpha) card background lets the desktop tint through it.
    public override string Html => """
        <body>
          <div class="hud">
            <div class="title">CupriFace HUD</div>
            <div class="row"><span class="k">FPS</span><span class="v">60</span></div>
            <div class="row"><span class="k">Frame</span><span class="v">4.2 ms</span></div>
            <div class="row"><span class="k">Draws</span><span class="v">128</span></div>
            <div class="hint">frameless · top-most · click-through corners</div>
          </div>
        </body>
        """;

    public override string Css => """
        body { font-family:sans-serif; }
        .hud { margin:16px; padding:16px 18px; border-radius:14px;
               background:#12141ad9; border:1px #b8733380; }
        .title { color:#ffffff; font-size:15px; font-weight:bold; margin-bottom:10px; }
        .row { display:flex; justify-content:space-between; margin-bottom:6px; }
        .k { color:#8b93a7; font-size:13px; }
        .v { color:#f5b301; font-size:13px; font-weight:bold; }
        .hint { color:#48505c; font-size:11px; margin-top:10px; }
        """;
}
