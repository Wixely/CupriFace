using CupriFace;
using CupriFace.Components;
using CupriFace.Lottie;

namespace CupriFace.Samples.AndroidLottie;

/// <summary>
/// The optional CupriFace.Lottie package, opted into on a phone. PORTABLE on purpose — there is not
/// one Android-specific line in this class; the host is the only thing that differs from the desktop
/// sample, which is the claim the whole engine rests on.
///
/// <para>Four elements share ONE player here, because the surface key is the src. That is what makes
/// the small row honest: the animation is rendered once at its own 120x120 and scaled to each element
/// by the engine, rather than four animations being run at four sizes.</para>
/// </summary>
public sealed class LottieApp : CupriApp
{
    /// <summary>The one animation, named the way the package resolves it: against this assembly's
    /// embedded resources, so the string is identical on every host.</summary>
    private const string Src = "Assets/cupri-spinner.json";

    private readonly LottieModel _model = new();

    public override string Title => "CupriFace — Lottie";
    public override int Width => 400;      // the desktop dev-loop window; phones ignore this
    public override int Height => 800;
    public override object Model => _model;

    // The element has to be in the vocabulary before markup can use it.
    public override ComponentRegistry Components => base.Components.UseLottie();

    public override void Configure(CupriDocument doc)
    {
        // …and the document needs to know where to load animations from. This assembly, because
        // Assets/ is embedded into it.
        doc.UseLottie(GetType().Assembly);

        // Say — once, from the DEVICE — what Skottie actually parsed. The natives being present in all
        // four Android ABIs was already known from the archives; this line is the part that was only
        // ever inferred, and it prints a shape that could only come from the file having been read and
        // understood by Skia on the phone. Registered AFTER UseLottie so the player already exists.
        var announced = false;
        doc.OnRebuilt(_ =>
        {
            if (announced || doc.Surfaces.Get(LottieKey) is not LottiePlayer p) return;
            announced = true;
            Console.WriteLine($"cupri-lottie: skottie parsed {p.NaturalSize?.W}x{p.NaturalSize?.H} " +
                              $"duration={p.Duration:F2}s");
        });

        doc.OnClick(".toggle", _ =>
        {
            _model.Playing = !_model.Playing;
            // Lands in logcat under the `cupri` tag (the host redirects Console), so a gate can assert
            // the alternation the same way the MobileApp switch does.
            Console.WriteLine($"cupri-lottie: playing={_model.Playing}");
        });
    }

    /// <summary>The key the package registers a player under — "lottie:" + the src.</summary>
    private const string LottieKey = "lottie:" + Src;

    // The hero binds autoplay; the three small ones deliberately do NOT. An absent autoplay is "no
    // opinion", so Pause reaches the shared player and all four stop together — which is the whole
    // point of the tri-state, and would have been undone if a bare element voted "play".
    public override string Html => """
        <body>
          <div class="wrap">
            <div class="title">cupri-lottie</div>
            <p class="sub">Skottie is a module of Skia, so the optional package is managed bindings
              over the same libSkiaSharp the engine already loads on this phone. The APK carries no
              extra native code for it.</p>

            <div class="hero">
              <cupri-lottie src="Assets/cupri-spinner.json" width="200" height="200"
                            autoplay="{{Playing}}" label="Loading"></cupri-lottie>
            </div>

            <div class="row">
              <cupri-lottie src="Assets/cupri-spinner.json" width="72" height="72"></cupri-lottie>
              <cupri-lottie src="Assets/cupri-spinner.json" width="52" height="52"></cupri-lottie>
              <cupri-lottie src="Assets/cupri-spinner.json" width="36" height="36"></cupri-lottie>
            </div>
            <p class="hint">One file, four elements, one player — the key is the src, so the frame is
              rendered once at its own size and scaled to each element by the engine.</p>

            <cupri-button class="toggle">{{PlayLabel}}</cupri-button>
            <p class="state">{{StateLine}}</p>
          </div>
        </body>
        """;

    public override string Css => """
        body { font-family:sans-serif; background:#f4f5f7; color:#1e2430; }
        .wrap { padding:20px 18px 28px; }
        .title { font-size:22px; font-weight:bold; }
        .sub { color:#48505c; font-size:14px; margin:8px 0 18px; }
        .hero { display:flex; justify-content:center; margin:6px 0 14px; }
        .row { display:flex; align-items:center; justify-content:center; gap:22px; }
        .hint { color:#6b7688; font-size:12px; margin:12px 0 20px; text-align:center; }
        /* 48dp is the platform's minimum touch target, and logical pixels ARE dp here because the
           responsive default leaves the density mapping to the host. */
        .toggle { display:block; width:100%; min-height:48px; font-size:16px; }
        .state { color:#6b7688; font-size:13px; text-align:center; margin-top:10px; }
        """;
}

/// <summary>No [CupriBindable]: the generated accessors are for AOT-clean binding, which this
/// three-property model does not need — reflection binding covers it, exactly as LottieDemo's does.</summary>
public sealed class LottieModel
{
    public bool Playing { get; set; } = true;
    public string PlayLabel => Playing ? "Pause" : "Play";
    public string StateLine => Playing ? "playing" : "paused — nothing ticks, the host is idle";
}
