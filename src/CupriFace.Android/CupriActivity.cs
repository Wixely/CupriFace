using Android.OS;
using Android.Views;

namespace CupriFace.Android;

/// <summary>
/// The author-facing API — the closest Android allows to <c>DesktopHost.Run(new MyApp())</c>:
///
/// <code>
/// [Activity(MainLauncher = true,
///     ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
/// public class MainActivity : CupriActivity
/// {
///     protected override CupriApp CreateApp() => new MyApp();
/// }
/// </code>
///
/// Declaring <c>ConfigurationChanges</c> as above is part of the contract: rotation then resizes
/// the surface instead of destroying the Activity, the document survives, and the next frame
/// simply lays out at the new size — the same thing a desktop window resize does.
/// </summary>
public abstract class CupriActivity : global::Android.App.Activity
{
    private AndroidHost? _host;
    private CupriHostView? _view;
    private Handler? _pump;
    private global::Java.Lang.Runnable? _pumpTick;

    /// <summary>The app to run. Called once, in OnCreate.</summary>
    protected abstract CupriApp CreateApp();

    /// <summary>Optional host-composition hook, the parity of DesktopHost's <c>configure</c>
    /// parameter: attach platform capabilities to the document here (not in CupriApp.Configure,
    /// which is portable code).</summary>
    protected virtual void ConfigureDocument(CupriDocument document) { }

    /// <summary>The running host; available after OnCreate.</summary>
    protected AndroidHost Host => _host ?? throw new InvalidOperationException("not created yet");

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // No system title bar: the app IS the chrome. (Without this, the default Material theme
        // put a black ActionBar with the app's label above every CupriFace app — the first thing
        // a real device showed that no emulator gate had asserted on.)
        ActionBar?.Hide();

        _host = new AndroidHost(this, CreateApp(), ConfigureDocument);
        _host.FullscreenRequested += cmd => SetImmersive(cmd switch
        {
            Interaction.WindowCommand.EnterFullscreen => true,
            Interaction.WindowCommand.ExitFullscreen => false,
            _ => !_immersive,
        });
        _view = new CupriHostView(this, _host);

        // Edge-to-edge, DELIBERATELY, wherever insets can be controlled (API 30+): Android 15
        // forces it anyway — the system bars become transparent overlays and a view that ignores
        // them puts its bottom nav underneath the gesture bar, which is exactly what the first
        // real-device test hit (unpressable tabs). Opting in everywhere gives one behaviour
        // instead of two. A container pads the view by the LIVE insets — system bars, display
        // cutout, and the IME: the keyboard inset is how "AdjustResize" works in this world (the
        // view shrinks, the next frame lays out, ScrollCaretIntoView keeps the caret visible).
        // The padding band shows the container's background, painted the app's own colour so the
        // strip behind the transparent status bar belongs to the app, not to a default black.
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && Window is { } w)
        {
            w.SetDecorFitsSystemWindows(false);
#pragma warning disable CA1422 // bar colours: deprecated AT 35 (enforced transparent there) — set below it
            if (!OperatingSystem.IsAndroidVersionAtLeast(35))
            {
                w.SetStatusBarColor(global::Android.Graphics.Color.Transparent);
                w.SetNavigationBarColor(global::Android.Graphics.Color.Transparent);
            }
#pragma warning restore CA1422

            var bg = _host.AppBackground;
            // A light app needs DARK bar icons or the clock vanishes into the background.
            var luminance = (0.299 * bg.Red + 0.587 * bg.Green + 0.114 * bg.Blue) / 255.0;
            if (luminance > 0.5 && w.InsetsController is { } ic)
            {
                var light = (int)(WindowInsetsControllerAppearance.LightStatusBars
                                  | WindowInsetsControllerAppearance.LightNavigationBars);
                ic.SetSystemBarsAppearance(light, light);
            }

            // Let the window extend INTO a notch/punch-hole. Without this the cutout area is simply
            // unavailable, so "true fullscreen" could never reach the top of the phone.
            if (OperatingSystem.IsAndroidVersionAtLeast(28) && w.Attributes is { } attrs)
                attrs.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;

            var container = new global::Android.Widget.FrameLayout(this);
            container.SetBackgroundColor(new global::Android.Graphics.Color(bg.Red, bg.Green, bg.Blue, bg.Alpha));
            container.AddView(_view);
            _padder = new InsetsPadder();
            container.SetOnApplyWindowInsetsListener(_padder);
            SetContentView(container);
        }
        else
        {
            SetContentView(_view);
            // Pre-30: the legacy resize path — the window itself shrinks under the keyboard.
            Window?.SetSoftInputMode(SoftInput.AdjustResize);
        }

        // The eventless work (refresh cadence while idle, image decodes finishing) needs a slow
        // heartbeat; 250 ms is imperceptible for both and costs nothing measurable.
        _pump = new Handler(Looper.MainLooper!);
        _pumpTick = new global::Java.Lang.Runnable(() =>
        {
            _host?.Pump();
            _pump?.PostDelayed(_pumpTick!, 250);
        });
        _pump.PostDelayed(_pumpTick, 250);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _view?.OnResume();
        // A WhenDirty view paints nothing until asked: after a pause/resume round-trip the
        // surface is fresh and blank, and only an explicit render request fills it again.
        _host?.MarkDirty();
    }

    protected override void OnPause() { _view?.OnPause(); base.OnPause(); }

    /// <summary>Applies the live window insets (system bars, display cutout, the soft keyboard)
    /// as padding, so the app's content sits in the truly-usable rectangle while the container's
    /// background fills the strips behind the transparent bars.</summary>
    private InsetsPadder? _padder;

    private sealed class InsetsPadder : global::Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        /// <summary>True fullscreen means the CUTOUT too. Hiding the system bars zeroes their
        /// insets, but a notch's inset survives — which left a blank band across the top of the
        /// phone exactly as tall as the status bar used to be, while the bottom went properly
        /// edge-to-edge. Immersive keeps only the keyboard inset, because a keyboard covering the
        /// field you are typing into is not fullscreen, it is a bug.</summary>
        public bool Immersive;

        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var kinds = Immersive
                    ? WindowInsets.Type.Ime()
                    : WindowInsets.Type.SystemBars() | WindowInsets.Type.Ime() | WindowInsets.Type.DisplayCutout();
                var i = insets.GetInsets(kinds);
                v.SetPadding(i.Left, i.Top, i.Right, i.Bottom);
            }
            return insets;
        }
    }

    protected override void OnDestroy()
    {
        _pump?.RemoveCallbacksAndMessages(null);
        _host?.Dispose();
        base.OnDestroy();
    }

    /// <summary>Back is Android's Escape: an open overlay/menu consumes it and stays put;
    /// otherwise the platform's back behaviour proceeds (finish, or pop — Phase 6 adds the
    /// app stack here). The decision lives on the document thread, so the answer comes back
    /// asynchronously via a reentrancy flag.</summary>
    private bool _backFallthrough;

#pragma warning disable CS0672, CA1422 // OnBackPressed: deprecated for predictive back, which this
                                       // app does not opt into; it remains the delivered path.
    public override void OnBackPressed()
    {
        if (_backFallthrough || _host is null)
        {
            _backFallthrough = false;
            base.OnBackPressed();
            return;
        }
        _host.EscapeThen(() =>
        {
            if (_host.TryPop()) return;                  // a pushed app: Back returns to the previous one
            _backFallthrough = true;
            OnBackPressed();
        });
    }
#pragma warning restore CS0672, CA1422

    // ---- immersive fullscreen (the document's ⛶ request) -------------------------------------

    private bool _immersive;

    private void SetImmersive(bool on)
    {
        _immersive = on;
        if (_padder is not null)
        {
            _padder.Immersive = on;
            _view?.RequestApplyInsets();     // re-pad now; insets do not change on their own here
        }
        if (Window is not { } w) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (on)
            {
                w.InsetsController?.Hide(WindowInsets.Type.SystemBars());
                if (w.InsetsController is { } c)
                    c.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
            else w.InsetsController?.Show(WindowInsets.Type.SystemBars());
        }
        else if (_view is not null)
        {
#pragma warning disable CA1422 // the pre-API-30 path, by definition running on old APIs
            _view.SystemUiFlags = on
                ? SystemUiFlags.ImmersiveSticky | SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation
                : SystemUiFlags.Visible;
#pragma warning restore CA1422
        }
    }
}
