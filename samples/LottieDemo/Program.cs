using CupriFace;
using CupriFace.Components;
using CupriFace.Lottie;
using CupriFace.Shell;

// The optional CupriFace.Lottie package, opted into. Two registrations, which is the whole setup:
// UseLottie() on the registry teaches the app the <cupri-lottie> element, UseLottie() on the document
// opens a player per animation and retires it when its element goes.
//
// The animation rides the engine's live-surface lane, so object-fit sizing, damage-clipped
// repainting and render-on-demand all apply to it without this sample (or the package) doing
// anything about them. A paused animation stops ticking and the window goes idle again.
//
// It is the same lane a <cupri-video> frame takes HERE, on a desktop host. That is worth saying
// carefully rather than generally: on the web hosts video is host-composited — the engine punches a
// transparent hole and the browser decodes underneath — so there the two take different paths and
// Lottie is the one the engine still draws itself.
DesktopHost.Run(new LottieApp());

sealed class LottieApp : CupriApp
{
    private readonly Model _model = new();

    public override string Title => "CupriFace — Lottie";
    public override int Width => 620;
    public override int Height => 380;
    public override object Model => _model;

    // The element has to be in the vocabulary before markup can use it.
    public override ComponentRegistry Components => base.Components.UseLottie();

    public override void Configure(CupriDocument doc)
    {
        // …and the document needs to know where to load animations from. This assembly, because
        // Assets/ is embedded into it.
        doc.UseLottie(GetType().Assembly);
        doc.OnClick(".toggle", _ => _model.Playing = !_model.Playing);
    }

    public override string Html => """
        <body>
          <div class="wrap">
            <div class="title">cupri-lottie</div>
            <p class="sub">Skia renders this — Skottie is a Skia module, so the optional package is
              managed bindings over the native library the engine already loads. No extra natives.</p>
            <div class="row">
              <cupri-lottie src="Assets/cupri-spinner.json" width="120" height="120"
                            autoplay="{{Playing}}" label="Loading"></cupri-lottie>
              <cupri-lottie src="Assets/cupri-spinner.json" width="64" height="64"></cupri-lottie>
              <cupri-lottie src="Assets/cupri-spinner.json" width="40" height="40"></cupri-lottie>
              <div class="col">
                <cupri-button class="toggle">{{PlayLabel}}</cupri-button>
                <span class="hint">One file, three sizes — the surface is rendered once at its own
                  size and scaled by the engine, exactly as a video frame is.</span>
              </div>
            </div>
          </div>
        </body>
        """;

    public override string Css => """
        body { font-family:sans-serif; background:#f4f5f7; color:#1e2430; }
        .wrap { padding:26px 30px; }
        .title { font-size:20px; font-weight:bold; }
        .sub { color:#48505c; font-size:13px; max-width:520px; margin:8px 0 22px; }
        .row { display:flex; align-items:center; gap:26px; }
        .col { display:flex; flex-direction:column; gap:10px; max-width:210px; }
        .hint { color:#6b7688; font-size:12px; }
        """;
}

// No [CupriBindable] here: that attribute drives the AOT-clean generated accessors, which this
// two-property sample does not need — reflection binding covers it.
public sealed class Model
{
    public bool Playing { get; set; } = true;
    public string PlayLabel => Playing ? "Pause" : "Play";
}
