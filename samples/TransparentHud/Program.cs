using CupriFace;
using CupriFace.Shell;

// A transparent, frameless, always-on-top HUD that floats over the desktop — the same
// CupriApp model as every other sample, only with three flags flipped. The window has no
// background of its own: wherever the markup doesn't paint, the framebuffer stays fully
// transparent and the desktop (or the game / app behind it) shows through. This needs a
// compositing window manager (universal on Windows 8+/macOS/modern Linux); where none is
// present it degrades to an opaque window — the host environment's concern, not ours.
//
// Frameless also means no title bar, which means nothing to move the window by. The grab
// bar across the top is that title bar: `data-window-drag` marks an element as one, the
// engine reports how far a drag on it has travelled, and the host moves the window to
// match. No OS-specific code — transparency, frameless chrome, top-most and repositioning
// are all portable window traits.
DesktopHost.Run(new HudApp());

sealed class HudApp : CupriApp
{
    public override string Title => "CupriFace — Transparent HUD";
    public override int Width => 340;
    public override int Height => 224;

    // The three flags that make this an overlay rather than an ordinary window.
    public override bool Transparent => true;
    public override bool Frameless => true;
    public override bool TopMost => true;

    // Nothing paints the body, so the corners stay see-through; only the rounded card and its
    // contents are drawn. A translucent (alpha) card background lets the desktop tint through it.
    //
    // The bar carries data-window-drag, so pressing it and moving drags the whole window. The dots
    // beside it are pure affordance — a grip reads as grabbable in a way a blank strip does not.
    public override string Html => """
        <body>
          <div class="hud">
            <div class="bar" data-window-drag>
              <div class="grip"><span></span><span></span><span></span></div>
              <div class="title">CupriFace HUD</div>
            </div>
            <div class="body">
              <div class="row"><span class="k">FPS</span><span class="v">60</span></div>
              <div class="row"><span class="k">Frame</span><span class="v">4.2 ms</span></div>
              <div class="row"><span class="k">Draws</span><span class="v">128</span></div>
              <div class="hint">drag the bar · frameless · top-most</div>
            </div>
          </div>
        </body>
        """;

    public override string Css => """
        body { font-family:sans-serif; }
        .hud { margin:16px; border-radius:14px; overflow:hidden;
               background:#12141ad9; border:1px #b8733380; }
        /* The bar is the window's title bar. The engine gives it a grab cursor on its own once the
           host is listening, so there is no cursor rule here to drift out of step with that. */
        .bar { display:flex; align-items:center; gap:9px; padding:9px 12px;
               background:#ffffff0f; border-bottom:1px #ffffff14; }
        .grip { display:flex; gap:3px; }
        .grip span { width:3px; height:3px; border-radius:2px; background:#8b93a7; }
        .title { color:#ffffff; font-size:14px; font-weight:bold; }
        .body { padding:14px 18px 16px; }
        .row { display:flex; justify-content:space-between; margin-bottom:6px; }
        .k { color:#8b93a7; font-size:13px; }
        .v { color:#f5b301; font-size:13px; font-weight:bold; }
        .hint { color:#48505c; font-size:11px; margin-top:10px; }
        """;
}
