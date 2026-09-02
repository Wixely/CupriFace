using CupriFace;
using CupriFace.Components;
using CupriFace.Lottie;
using CupriFace.Web;

// Does Lottie work in the browser? The renderer is Skia's own, and its entry points are present in
// the WASM libSkiaSharp archive — but "the symbol is in the archive" and "the symbol survives an
// Emscripten link and runs" are different claims, and only building this settles the second one.
//
// WebWasm next door is the same host running the Showcase WITHOUT this package, so the difference
// between the two published sizes is what Lottie costs a web app.
WebHost.Run(new WasmLottieApp());

sealed class WasmLottieApp : CupriApp
{
    public override string Title => "CupriFace — Lottie on WASM";
    public override ComponentRegistry Components => base.Components.UseLottie();
    public override void Configure(CupriDocument doc) => doc.UseLottie(GetType().Assembly);

    public override string Html => """
        <body>
          <div class="wrap">
            <div class="t">cupri-lottie, in a browser</div>
            <cupri-lottie src="Assets/cupri-spinner.json" width="140" height="140"
                          label="Loading"></cupri-lottie>
            <p class="s">Skia renders this, inside the wasm module — the engine draws the frames
              itself. A <b>video</b> on this host works the other way round: the engine punches a
              transparent hole and the browser decodes and composites underneath it. So these are the
              same pixels the desktop build produces, and no part of drawing them is the browser's.</p>
          </div>
        </body>
        """;

    public override string Css => """
        body { font-family:sans-serif; background:#f4f5f7; color:#1e2430; }
        .wrap { padding:28px; display:flex; flex-direction:column; align-items:flex-start; gap:14px; }
        .t { font-size:19px; font-weight:bold; }
        .s { color:#48505c; font-size:13px; max-width:420px; }
        """;
}
