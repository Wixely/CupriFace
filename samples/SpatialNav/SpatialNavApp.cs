using CupriFace;
using CupriFace.Interaction;
using SkiaSharp;

namespace CupriFace.Samples.SpatialNav;

/// <summary>
/// Thirty boxes, scattered, driven by the arrow keys — and one key that switches HOW the arrows
/// move, so the difference is something you can watch rather than something you have to take on
/// trust.
///
/// <para><b>Press M.</b> With <c>ArrowNavigation</c> off (the default, and the right default for an
/// ordinary application) an arrow is Tab by another name: it steps to the next control in DOCUMENT
/// order, so Down and Right do the same thing and the selection hops around the screen in the order
/// the markup happens to be written. With it on, the arrows become a keyboard D-pad and the
/// selection goes where you pointed.</para>
///
/// <para>The layout is deliberately irregular, because a neat grid hides most of what the scoring
/// does. Four clusters, each chosen to show one thing:</para>
/// <list type="bullet">
/// <item><b>A, a tidy 4x3 block</b> — the easy case, and the one where document order looks almost
/// reasonable until you press Down and travel sideways.</item>
/// <item><b>B, two staggered columns</b> — the case the beam rule exists for. From B1, the box in
/// the OTHER column (B2) is nearer than the next one down its own column (B3), so scoring on raw
/// distance would drift you sideways. Hold Down and watch it stay in the column.</item>
/// <item><b>C, a sparse diagonal</b> — nothing is directly below anything, so this is where the 45°
/// cone decides what counts as "down" at all. Some presses correctly do nothing.</item>
/// <item><b>D, a row of different-width chips</b> — Left and Right across unequal boxes, where
/// centre-to-centre distance and edge distance disagree.</item>
/// </list>
///
/// <para>Enter activates the focused box and bumps its own counter, which is the other half of a
/// controller UI: navigate, then confirm. Tab is left alone in both modes — the flag adds a way to
/// move, it does not take the ordinary one away.</para>
/// </summary>
public sealed class SpatialNavApp : CupriApp
{
    public override string Title => "CupriFace — spatial navigation (press M)";
    public override int Width => 1040;
    public override int Height => 720;
    public override SKColor Background => new(0x14, 0x16, 0x1C);
    public override object? Model => _model;

    private readonly NavModel _model = new();

    // ---- the board -----------------------------------------------------------------------------
    // Positions are DATA, not hand-typed markup: 30 boxes through one data-repeat template, so the
    // clusters can be reasoned about as numbers and a bound {{Hits}} can change per box at runtime.
    private static readonly (int X, int Y, int W, int H, string Label)[] Layout =
    [
        // A — a tidy 4x3 block. Document order reads across each row, which is exactly why "Down"
        // in the off mode walks sideways.
        (32, 124, 100, 58, "A1"), (150, 124, 100, 58, "A2"), (268, 124, 100, 58, "A3"), (386, 124, 100, 58, "A4"),
        (32, 200, 100, 58, "A5"), (150, 200, 100, 58, "A6"), (268, 200, 100, 58, "A7"), (386, 200, 100, 58, "A8"),
        (32, 276, 100, 58, "A9"), (150, 276, 100, 58, "A10"), (268, 276, 100, 58, "A11"), (386, 276, 100, 58, "A12"),

        // B — two staggered columns. From B1, B2 (other column, 110px down) is NEARER than B3 (same
        // column, 236px down). The beam rule is the only reason Down stays in the column.
        (600, 124, 120, 66, "B1"), (740, 234, 120, 66, "B2"),
        (600, 360, 120, 66, "B3"), (740, 424, 120, 66, "B4"),
        (600, 500, 120, 66, "B5"), (740, 584, 120, 66, "B6"),

        // C — a sparse diagonal. Nothing sits directly below anything, so the 45° cone decides
        // whether "down" finds anything at all.
        (60, 352, 90, 50, "C1"), (180, 407, 90, 50, "C2"), (300, 462, 90, 50, "C3"),
        (420, 517, 90, 50, "C4"), (540, 572, 90, 50, "C5"),

        // D — unequal chips in a row, where centre distance and edge distance disagree.
        (60, 656, 70, 48, "D1"), (146, 656, 92, 48, "D2"), (254, 656, 62, 48, "D3"),
        (332, 656, 112, 48, "D4"), (460, 656, 74, 48, "D5"), (550, 656, 104, 48, "D6"),
        (670, 656, 84, 48, "D7"),
    ];

    private readonly bool _startMode;

    /// <summary>The mode to open in. The HUD is built from it during <see cref="Configure"/>, so it
    /// has to be known before the document exists — setting <c>doc.ArrowNavigation</c> afterwards
    /// would leave the label describing the mode the app is no longer in.</summary>
    public SpatialNavApp(bool arrowNavigation = false)
    {
        _startMode = arrowNavigation;
        foreach (var (x, y, w, h, label) in Layout)
            _model.Boxes.Add(new Box { X = x, Y = y, W = w, H = h, Label = label });
    }

    public override string Html => """
        <body class="{{FocusClass}}">
          <div class="hud">
            <div class="hudtop">
              <span class="h">Spatial navigation</span>
              <span class="last">activated: {{Last}}</span>
              <span class="mode" style="color:{{ModeColor}}">{{ModeLabel}}</span>
              <span class="diag" style="color:{{DiagColor}}">{{DiagLabel}}</span>
              <span class="diag" style="color:#9AA3B5">{{FocusLabel}}</span>
            </div>
            <div class="obs">engine: {{LastInput}}</div>
            <div class="hint">{{ModeHint}}</div>
            <div class="keys">↑ ↓ ← →  move  ·  Enter  activate  ·  Tab  document order  ·  M  mode  ·  D  diagonals  ·  F  focus style  ·  [ ]  window ±20ms  ·  R  reset</div>
          </div>

          <div class="box" role="button" data-repeat="Boxes" data-nav-box="{{Label}}"
               style="left:{{X}}px;top:{{Y}}px;width:{{W}}px;height:{{H}}px">
            <span class="lbl">{{Label}}</span>
            <span class="hits">{{HitsLabel}}</span>
          </div>
        </body>
        """;

    // No :focus rule and no border on focus: both would be wrong here. The engine paints its own
    // focus ring OUTSIDE the box (so nothing reflows as the selection moves) and --cupri-focus is
    // the supported way to recolour it. A border would grow the box and shift the whole board.
    public override string Css => """
        :root { --cupri-focus: #E39B52; }
        body { margin:0; background:#14161C; font-family:sans-serif; position:relative; }

        .hud { height:118px; box-sizing:border-box; padding:14px 24px; background:#1B1F28; }
        .obs { margin-top:5px; font-size:12px; color:#8FB8D8; font-family:monospace; }
        .hudtop { display:flex; align-items:center; justify-content:space-between; }
        .h { color:#F2F4F8; font-size:20px; font-weight:bold; }
        .last { color:#9AA3B5; font-size:13px; }
        .mode { font-size:15px; font-weight:bold; font-variant-numeric:tabular-nums; }
        .diag { font-size:13px; font-weight:bold; }
        .hint { color:#9AA3B5; font-size:13px; margin-top:6px; }
        .keys { color:#5F6879; font-size:12px; margin-top:6px; }

        /* The DEFAULT needs no rule at all: the engine paints a focus ring outside the box, which is
           layout-neutral and recoloured by --cupri-focus above. Right for an application.

           A GAME usually wants the selection to look like selection rather than like a form field,
           and `:focus` is the hook for that — `outline:none` is what takes the engine's ring off, the
           same idiom as on the web. Both are shown here because the difference is the point. */
        .styled .box:focus {
            background:#4A3F22;
            box-shadow: inset 0 0 0 3px #E39B52;
            color:#FFF3DF;
            outline: none;
        }

        .box { position:absolute; box-sizing:border-box;
               display:flex; align-items:center; justify-content:center; gap:8px;
               background:#242A36; border-radius:8px; color:#C9D1E0; font-size:14px; }
        .lbl { font-weight:bold; }
        .hits { color:#E39B52; font-size:12px; font-variant-numeric:tabular-nums; }
        """;

    public override void Configure(CupriDocument doc)
    {
        doc.ArrowNavigation = _startMode;
        Apply(doc);   // …and the HUD must describe that mode before the first frame is drawn

        // What the engine made of each event, on screen. This is doc.InputObserved, which exists
        // because a dispatch returns ONE bool: "nothing arrived", "arrived and meant nothing" and
        // "ignored because you said so" were the same answer. Press an arrow with M on and then off
        // and the line changes while the keypress does not.
        //
        // Refreshed here deliberately: the observation is raised AFTER the engine's own refresh, so
        // without this the HUD would always show the PREVIOUS event — which looks exactly like a
        // diagnostic dropping the one you care about.
        doc.InputObserved += o => { _model.LastInput = o.ToString(); doc.Refresh(); };

        // M switches the mode. This is the whole demo: the same arrow keys, two behaviours, live.
        doc.OnShortcut(KeyMods.None, "m", () =>
        {
            doc.ArrowNavigation = !doc.ArrowNavigation;
            Apply(doc);
        });

        // D turns on corner moves. Try it in cluster B with Right and Down together: without it,
        // where you land depends on which key the hardware reported first.
        doc.OnShortcut(KeyMods.None, "d", () =>
        {
            doc.DiagonalNavigation = !doc.DiagonalNavigation;
            Apply(doc);
        });

        // The window is the one number to turn when corners "do not work": two keys a hand meant to
        // press together are routinely 50-100ms apart, and that varies by person and keyboard.
        doc.OnShortcut(KeyMods.None, "]", () =>
        {
            doc.DiagonalWindowSeconds = Math.Min(0.4, doc.DiagonalWindowSeconds + 0.02);
            Apply(doc);
        });
        doc.OnShortcut(KeyMods.None, "[", () =>
        {
            doc.DiagonalWindowSeconds = Math.Max(0.02, doc.DiagonalWindowSeconds - 0.02);
            Apply(doc);
        });

        // F switches between the engine's ring and the sample's own :focus styling. Both are real
        // answers: the ring is right for an application, a filled tile is right for a game.
        doc.OnShortcut(KeyMods.None, "f", () =>
        {
            _model.Styled = !_model.Styled;
            Apply(doc);
        });

        doc.OnShortcut(KeyMods.None, "r", () =>
        {
            foreach (var b in _model.Boxes) b.Hits = 0;
            _model.Last = "—";
        });

        // Enter/Space on the focused box. Deliberately an action rather than a click handler: this
        // is the path a controller's A button takes, and it must be the same path as the keyboard's.
        doc.OnAction("data-nav-box", e =>
        {
            var box = _model.Boxes.Find(b => b.Label == e.Value);
            if (box is null) return false;
            box.Hits++;
            _model.Last = box.Label;
            return true;
        });
    }

    private void Apply(CupriDocument doc)
    {
        var on = doc.ArrowNavigation;
        _model.ModeLabel = on ? "ARROWS: D-PAD (spatial)" : "ARROWS: DOCUMENT ORDER";
        _model.ModeColor = on ? "#7ED491" : "#9AA3B5";
        _model.FocusClass = _model.Styled ? "styled" : "";
        _model.FocusLabel = _model.Styled ? "FOCUS: :focus styling" : "FOCUS: engine ring";
        var diag = doc.DiagonalNavigation;
        // Which path is in play matters more than the number: with key releases the window is not
        // consulted at all, so showing a window value here would be a lie on this host.
        _model.DiagLabel = diag
            ? doc.ReportsKeyUp
                ? "DIAGONALS: ON · held keys, no delay"
                : $"DIAGONALS: ON · window {doc.DiagonalWindowSeconds * 1000:F0}ms"
            : "DIAGONALS: off  (press D)";
        _model.DiagColor = diag ? "#7ED491" : "#5F6879";
        _model.ModeHint = on
            ? (diag
                ? (doc.ReportsKeyUp
                    ? "Hold Right and press Down (or the reverse) in cluster B: one move to B2. No timing involved — the corner is recognised because the first key is still down, so any gap works."
                    : $"Right + Down together in cluster B should be ONE move to B2. If you land on B4 the two keys were more than {doc.DiagonalWindowSeconds * 1000:F0}ms apart — press ] to widen the window.")
                : "Arrows move to the nearest box in that direction. Hold ↓ in cluster B — it stays in the column even though the other column is nearer. Press D for corner moves.")
            : "Arrows step through the markup like Tab, so ↓ and → do the same thing. This is the default, and the right one for an ordinary app. Press M.";
    }
}

/// <summary>What the markup binds to. <c>Boxes</c> feeds the single <c>data-repeat</c> template, so
/// adding a box is a line of data rather than a line of HTML.</summary>
public sealed class NavModel
{
    public List<Box> Boxes { get; } = [];
    public string ModeLabel { get; set; } = "";
    public string ModeHint { get; set; } = "";
    public string ModeColor { get; set; } = "#9AA3B5";
    public string DiagLabel { get; set; } = "";
    public bool Styled { get; set; }
    public string FocusClass { get; set; } = "";
    public string FocusLabel { get; set; } = "";
    public string DiagColor { get; set; } = "#5F6879";
    public string Last { get; set; } = "—";
    public string LastInput { get; set; } = "—";
}

public sealed class Box
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public string Label { get; set; } = "";
    public int Hits { get; set; }

    /// <summary>"x3", or nothing at all until the box has been activated — thirty boxes each
    /// reading "0" is noise that hides the one number that changed.</summary>
    public string HitsLabel => Hits == 0 ? "" : $"×{Hits}";
}
