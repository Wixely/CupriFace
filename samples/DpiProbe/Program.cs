using CupriFace;
using CupriFace.Binding;
using CupriFace.Shell;

// The DPI probe (#137). Drag this window between monitors at 100%, 125%, 150% and 200% and read
// what the host actually computed off the panel — D, P, T, the framebuffer and the logical client
// size — rather than inferring it from how the window looks.
//
// It reports DesktopHost.FrameScale, which is the very value the canvas, the surfaces, the damage
// rectangle and the accessibility geometry were fed. A probe that re-derived the numbers would
// agree with itself no matter what the renderer did, which is the failure mode this avoids.
//
// Switches, so the same binary tests all four combinations without a rebuild:
//
//   DpiProbe.exe                  DPI-aware, following monitor changes (the defaults)
//   DpiProbe.exe --no-dpi         CupriApp.DpiAware = false      (the pre-#137 behaviour)
//   DpiProbe.exe --no-track       aware, but stops following the window between monitors
//   DpiProbe.exe --zoom 1.25      apply an application present scale P as well, to prove T = D*P
//   CUPRIFACE_DPI=0 DpiProbe.exe  the environment kill switch, no rebuild
//   CUPRIFACE_SOFTWARE=1 …        the SDL software window instead of the GL one

var argv = Environment.GetCommandLineArgs();
var zoom = 1f;
var zoomAt = Array.IndexOf(argv, "--zoom");
if (zoomAt >= 0 && zoomAt + 1 < argv.Length) float.TryParse(argv[zoomAt + 1], out zoom);

DesktopHost.Run(new DpiProbeApp(
    dpiAware: !argv.Contains("--no-dpi"),
    trackMonitor: !argv.Contains("--no-track"),
    zoom: zoom is > 0 and <= 4 ? zoom : 1f));

sealed class DpiProbeApp(bool dpiAware, bool trackMonitor, float zoom) : CupriApp
{
    public override string Title => "CupriFace — DPI probe";
    public override int Width => 900;      // LOGICAL. On a 150% monitor this opens 1350 physical px wide.
    public override int Height => 790;   // tall enough that the crispness samples need no scroll

    public override bool DpiAware => dpiAware;
    public override bool TrackMonitorDpi => trackMonitor;

    // Re-bind twice a second so the readout follows the window across monitors without needing a
    // click. The host also repaints on its own when the scale actually changes; this is belt and
    // braces so a stuck number is visibly stuck rather than merely stale.
    public override double RefreshIntervalSeconds => 0.5;

    // An application present scale ON TOP of the monitor's, so the panel can show that T is the
    // product rather than either one alone. Left at 1 unless --zoom was passed.
    public override PresentInfo Present(float w, float h) =>
        zoom == 1f ? PresentInfo.Responsive(w, h) : PresentInfo.Zoom(w, h, zoom);

    public override object? Model => _model;
    private readonly ProbeModel _model = new();

    public override void Configure(CupriDocument doc)
    {
        // Every click reports where the document thinks the pointer landed. That is the check the
        // numbers alone cannot make: if the pointer transform were divided by D twice, the crosshair
        // would sit up and left of the cursor by a third on a 150% monitor.
        doc.OnClick("#pad", e => _model.Hit = $"{e.X:F1}, {e.Y:F1}");
    }

    public override string Css => """
        * { box-sizing: border-box }
        body { margin: 0; font-family: 'Segoe UI', system-ui, sans-serif; background: #12161c; color: #e8edf4 }
        .wrap { padding: 20px; display: flex; flex-direction: column; gap: 16px }
        h1 { margin: 0; font-size: 20px; font-weight: 700; color: #d9a441 }
        .sub { margin: 0; font-size: 13px; color: #8a97a8 }

        .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 16px }
        .card { background: #1b212b; border: 1px solid #2a3341; border-radius: 8px; padding: 14px }
        .card h2 { margin: 0 0 10px 0; font-size: 12px; text-transform: uppercase;
                   letter-spacing: 1px; color: #8a97a8; font-weight: 700 }
        .row { display: flex; justify-content: space-between; gap: 12px; padding: 3px 0; font-size: 14px }
        .k { color: #9fb0c4 }
        .v { font-family: Consolas, 'Courier New', monospace; color: #ffffff; font-weight: 700 }
        .big { font-size: 26px; color: #d9a441 }

        /* A 100-LOGICAL-pixel square. Measure it on screen with a ruler tool: it should be
           100 * T physical pixels — 150 at 150%, 200 at 200%. This is the whole test in one box. */
        .ruler { width: 100px; height: 100px; background: #d9a441; border-radius: 4px }
        .rulercap { font-size: 12px; color: #8a97a8; margin-top: 6px }

        /* Hairlines: real 1-LOGICAL-px boxes with a 3px gap, not a gradient — the engine has no
           repeating-linear-gradient, and actual boxes are the better test anyway. Crisp and evenly
           spaced = rasterised at device resolution; smeared or alternating thick/thin = a stretched
           raster, or a rounding error in the transform. */
        .hair { height: 40px; display: flex; gap: 3px }
        .hair span { width: 1px; background: #e8edf4 }

        #pad { height: 110px; background: #232b37; border: 1px dashed #3b4757; border-radius: 6px;
               display: flex; align-items: center; justify-content: center; cursor: crosshair }
        #pad:hover { background: #27303d }
        .padtext { font-size: 13px; color: #9fb0c4 }

        .log { margin: 0; font-family: Consolas, 'Courier New', monospace; font-size: 11px;
               color: #9fe6b4; white-space: pre-wrap; line-height: 1.5 }

        .type8  { font-size: 8px }
        .type11 { font-size: 11px }
        .type14 { font-size: 14px }
        .type20 { font-size: 20px }
        """;

    public override string Html => """
        <body>
          <div class="wrap">
            <div>
              <h1>DPI probe</h1>
              <p class="sub">Drag this window between monitors of different scaling. Everything below
                 is what the HOST computed for the last drawn frame — not a re-derivation.</p>
            </div>

            <div class="grid">
              <div class="card">
                <h2>Scales</h2>
                <div class="row"><span class="k">D — monitor (OS)</span><span class="v big">{{DeviceScale}}</span></div>
                <div class="row"><span class="k">P — app (PresentInfo.Scale)</span><span class="v">{{PresentScale}}</span></div>
                <div class="row"><span class="k">T — effective (D × P)</span><span class="v big">{{EffectiveScale}}</span></div>
                <div class="row"><span class="k">product checks out</span><span class="v">{{ProductOk}}</span></div>
              </div>

              <div class="card">
                <h2>Sizes</h2>
                <div class="row"><span class="k">framebuffer (physical px)</span><span class="v">{{Framebuffer}}</span></div>
                <div class="row"><span class="k">logical client (window)</span><span class="v">{{LogicalClient}}</span></div>
                <div class="row"><span class="k">document viewport</span><span class="v">{{Viewport}}</span></div>
                <div class="row"><span class="k">window asked for</span><span class="v">900 × 790 logical</span></div>
                <div class="row"><span class="k">expected physical</span><span class="v">{{ExpectedPhysical}}</span></div>
              </div>

              <div class="card">
                <h2>Host state</h2>
                <div class="row"><span class="k">DPI awareness</span><span class="v">{{Awareness}}</span></div>
                <div class="row"><span class="k">CupriApp.DpiAware</span><span class="v">{{Aware}}</span></div>
                <div class="row"><span class="k">CupriApp.TrackMonitorDpi</span><span class="v">{{Track}}</span></div>
                <div class="row"><span class="k">CUPRIFACE_DPI</span><span class="v">{{EnvDpi}}</span></div>
                <div class="row"><span class="k">window</span><span class="v">{{WindowKind}}</span></div>
              </div>

              <div class="card">
                <h2>Callbacks</h2>
                <div class="row"><span class="k">move events seen</span><span class="v">{{MoveEvents}}</span></div>
                <div class="row"><span class="k">framebuffer resizes seen</span><span class="v">{{ResizeEvents}}</span></div>
                <p class="sub">If these keep CLIMBING while you drag but the scale does not change,
                   the window is hearing the drag and the OS is not yet reporting a new DPI. If they
                   FREEZE mid-drag, the callback itself is not being delivered.</p>
              </div>

              <div class="card">
                <h2>Pointer</h2>
                <p class="sub">Click the pad. The coordinates are DOCUMENT units — click the exact
                   centre and they should read about half the logical client size.</p>
                <div id="pad"><span class="padtext">last click: {{Hit}}</span></div>
              </div>
            </div>

            <div class="card">
              <h2>Scale events (most recent last)</h2>
              <pre class="log">{{Events}}</pre>
            </div>

            <div class="card">
              <h2>Crispness — judge these by eye</h2>
              <div class="grid">
                <div>
                  <div class="ruler"></div>
                  <p class="rulercap">100 logical px. Should measure 100 × T physical pixels.</p>
                </div>
                <div>
                  <div class="hair"><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span><span></span></div>
                  <p class="rulercap">1px hairlines every 4px. Even and sharp = rasterised at device
                     resolution; smeared or unevenly spaced = a stretched raster.</p>
                </div>
              </div>
              <p class="type8">8px — expect this to be small and hard work even when correct; it is a stretching detector, not a legibility target.</p>
              <p class="type11">11px — small UI text, the usual caption size.</p>
              <p class="type14">14px — body text at the default size.</p>
              <p class="type20">20px — heading weight, where stem contrast is easiest to read.</p>
            </div>
          </div>
        </body>
        """;
}

/// <summary>
/// Everything the panel binds to, read fresh on every re-bind so the values track the window.
///
/// <para><b>[CupriBindable] and partial are not decoration.</b> Without the attribute the binding
/// engine resolves properties by reflection, and the trimmer removes that metadata — the published
/// single-file build crashed on its first bind with "Property Get method was not found" while the
/// ordinary Debug build was perfectly happy. A diagnostic binary that only works untrimmed is not
/// much of a diagnostic, so the generator does the accessors instead.</para>
/// </summary>
[CupriBindable]
public sealed partial class ProbeModel
{
    public string Hit { get; set; } = "—";

    private static HostScaleView S => new(DesktopHost.FrameScale);

    public string DeviceScale => $"{S.D:0.###}×";
    public string PresentScale => $"{S.P:0.###}×";
    public string EffectiveScale => $"{S.T:0.###}×";

    // Guards the one invariant the whole change rests on. If this ever says NO, the host applied
    // something other than the product and nothing else on the panel is to be trusted.
    public string ProductOk => MathF.Abs(S.D * S.P - S.T) < 0.001f ? "yes  (T = D × P)" : "NO — MISMATCH";

    public string Framebuffer => $"{S.Scale.FramebufferWidth} × {S.Scale.FramebufferHeight}";
    public string LogicalClient => $"{S.Scale.LogicalClientWidth:0.#} × {S.Scale.LogicalClientHeight:0.#}";
    // The window in logical units is NOT the viewport the document lays out at once the app applies
    // its own scale: at P=1.25 a 900-unit window is a 720-unit page. Both are shown because a
    // tester comparing one against a ruler needs to know which one they are looking at.
    public string Viewport =>
        $"{S.Scale.LogicalClientWidth / S.P:0.#} × {S.Scale.LogicalClientHeight / S.P:0.#}";

    public string ExpectedPhysical => $"{900 * S.D:0.#} × {790 * S.D:0.#}";

    public string MoveEvents => DpiTrace.MoveEvents.ToString();
    public string ResizeEvents => DpiTrace.ResizeEvents.ToString();

    // Newest last, so a drag reads top-to-bottom in the order it happened.
    public string Events =>
        DpiTrace.Recent is { Count: > 0 } r
            ? string.Join(Environment.NewLine, r)
            : "(none yet — drag this window to a monitor with different scaling)";

    public string Awareness => DesktopHost.DpiAwareness;
    public string Aware => Environment.GetCommandLineArgs().Contains("--no-dpi") ? "false" : "true";
    public string Track => Environment.GetCommandLineArgs().Contains("--no-track") ? "false" : "true";
    public string EnvDpi => Environment.GetEnvironmentVariable("CUPRIFACE_DPI") ?? "(unset — on)";
    public string WindowKind =>
        Environment.GetEnvironmentVariable("CUPRIFACE_SOFTWARE") is "1" or "true" or "TRUE"
            ? "SDL software (forced)" : "GL, or SDL on fallback";

    private readonly record struct HostScaleView(CupriFace.Hosting.HostScale Scale)
    {
        public float D => Scale.DeviceScale;
        public float P => Scale.PresentScale;
        public float T => Scale.EffectiveScale;
    }
}