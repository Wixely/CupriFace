using CupriFace;
using CupriFace.Binding;
using CupriFace.Resources;

namespace CupriFace.Demo;

/// <summary>
/// The phone-first sample: bottom navigation, 48dp touch targets, and one page per thing the
/// mobile work has to prove — a long virtual list (fling), a form (soft keyboard + IME), settings
/// (switches and sliders under a finger). PORTABLE on purpose: the same class runs on Android
/// (samples/AndroidViewer), on desktop (<c>Viewer --app mobile</c> — the fast dev loop), and in
/// the browser, which is the whole point of the engine.
///
/// Unlike the Showcase there is no <c>Present</c> override: the responsive default means logical
/// pixels ARE density-independent pixels, so <c>@media (max-width: …)</c> queries see the real
/// device width and the layout reflows instead of shrinking.
/// </summary>
public sealed class MobileApp : CupriApp
{
    private readonly MobileModel _model = new();

    protected override CupriSource MarkupSource => Assets.MobileApp.Html;
    protected override CupriSource StyleSource => Assets.MobileApp.Css;
    public override string Title => "CupriFace Mobile";
    public override int Width => 400;      // the desktop dev-loop window; phones ignore this
    public override int Height => 800;
    public override object? Model => _model;

    /// <summary>The About page's "Open Showcase" seam: the HOST decides what launching means
    /// (Android pushes a second app onto its stack; desktop could open a window). Raised with the
    /// element's <c>data-launch</c> value.</summary>
    public Action<string>? LaunchRequested;

    public override void Configure(CupriDocument doc)
    {
        // The CI gate's observable: taps on the marked switch toggle the model HERE and print a
        // marker Console line — which lands in logcat on Android — carrying the post-toggle state.
        // Two taps must print true then false; that alternation is the gate's proof that touch
        // activation happens exactly once per tap, on finger-up.
        doc.OnAction("data-gate-toggle", e =>
        {
            _model.Notify = !_model.Notify;
            Console.WriteLine($"cupri-gate: toggle={_model.Notify}");
            return true;
        });

        // The fling gate reads the list's scroll offset after momentum settles; the marker comes
        // from the Android host (it owns the frame loop and sees the fling end).

        // True fullscreen: no status bar, no navigation bar — a game/kiosk presentation. The host
        // performs it (the engine owns pixels, not the window); returning FALSE lets the switch
        // still flip its own bound value through the ordinary path, so the row reads correctly.
        doc.OnAction("data-fullscreen", e =>
        {
            doc.RequestWindowCommand(_model.Fullscreen
                ? CupriFace.Interaction.WindowCommand.ExitFullscreen      // about to become false
                : CupriFace.Interaction.WindowCommand.EnterFullscreen);
            return false;
        });

        doc.OnAction("data-launch", e =>
        {
            LaunchRequested?.Invoke(e.Value);
            return true;
        });

        // Two fingers on the photo tile: pinch to scale, twist to rotate, drag to move. This uses
        // the RECOGNISER (OnManipulate) rather than raw pointers — the same seam underneath, with
        // the arithmetic and the focal point already right. The first version of this sample did
        // the trigonometry by hand and scaled about the tile's centre instead of the point between
        // the fingers, which is exactly the mistake the recogniser exists to stop repeating.
        // doc.OnPointer is still there for anything this does not describe.
        doc.OnManipulate("data-gesture", g =>
        {
            _model.TileScale = Math.Clamp(g.Scale, 0.4, 3.0);
            _model.TileRotation = g.Rotation;
            _model.TilePanX = g.PanX;
            _model.TilePanY = g.PanY;
            return true;
        });

        doc.OnClick(".tile-reset", _ =>
        {
            _model.TileScale = 1;
            _model.TileRotation = 0;
            _model.TilePanX = 0;
            _model.TilePanY = 0;
        });
    }
}

[CupriBindable]
public sealed partial class MobileModel
{
    /// <summary>Which build this actually is. A sideloaded APK looks identical to the one it
    /// replaced, and an install that silently didn't happen sends you hunting bugs that were fixed
    /// two releases ago — so the app states its own version where a tester can see it.</summary>
    public string Build { get; } = BuildInfo.Describe();

    public string Page { get; set; } = "home";

    public string PgHome => Page == "home" ? "flex" : "none";
    public string PgList => Page == "list" ? "flex" : "none";
    public string PgForm => Page == "form" ? "flex" : "none";
    public string PgSettings => Page == "settings" ? "flex" : "none";
    public string PgAbout => Page == "about" ? "flex" : "none";

    public string NavHome => Page == "home" ? "nav-item active" : "nav-item";
    public string NavList => Page == "list" ? "nav-item active" : "nav-item";
    public string NavForm => Page == "form" ? "nav-item active" : "nav-item";
    public string NavSettings => Page == "settings" ? "nav-item active" : "nav-item";
    public string NavAbout => Page == "about" ? "nav-item active" : "nav-item";

    // Settings — Notify is the gate's tap target (toggled in Configure so the marker prints).
    public bool Notify { get; set; }
    public bool Dark { get; set; }
    public bool Fullscreen { get; set; }

    // The pinch/rotate tile. Written by the OnPointer handler, read back as a CSS transform.
    public double TileScale { get; set; } = 1;
    public double TileRotation { get; set; }
    public double TilePanX { get; set; }
    public double TilePanY { get; set; }
    public string TileTransform
    {
        get
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return $"transform:translate({TilePanX.ToString("0.#", c)}px,{TilePanY.ToString("0.#", c)}px) " +
                   $"scale({TileScale.ToString("0.##", c)}) rotate({TileRotation.ToString("0.#", c)}deg)";
        }
    }
    public string TileReadout =>
        $"{TileScale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}× · " +
        $"{TileRotation.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}°";

    // The autofill demo's fields. autocomplete is what tells a password manager what to offer.
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public int Volume { get; set; } = 60;
    public string ThemeClass => Dark ? "dark" : "";

    // Form — one field per keyboard kind (text / numeric / password / multiline).
    public string Name { get; set; } = "";
    public int Amount { get; set; } = 42;
    public string Secret { get; set; } = "";
    public string Notes { get; set; } = "";

    // List — enough rows that flinging matters, virtualised so only a screenful exists.
    public List<string> Rows { get; set; } =
        Enumerable.Range(1, 500).Select(i => $"Row {i} — tap and fling").ToList();
}
