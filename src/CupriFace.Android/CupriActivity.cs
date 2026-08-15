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

        _host = new AndroidHost(this, CreateApp(), ConfigureDocument);
        _host.FullscreenRequested += cmd => SetImmersive(cmd switch
        {
            Interaction.WindowCommand.EnterFullscreen => true,
            Interaction.WindowCommand.ExitFullscreen => false,
            _ => !_immersive,
        });
        _view = new CupriHostView(this, _host);
        SetContentView(_view);
        // The soft keyboard RESIZES the surface rather than overlaying it: the next frame lays out
        // at the smaller height and the engine's own ScrollCaretIntoView keeps the caret visible —
        // no host-side pan logic to get wrong.
        Window?.SetSoftInputMode(SoftInput.AdjustResize);

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

    protected override void OnResume() { base.OnResume(); _view?.OnResume(); }
    protected override void OnPause() { _view?.OnPause(); base.OnPause(); }

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
        _host.EscapeThen(() => { _backFallthrough = true; OnBackPressed(); });
    }
#pragma warning restore CS0672, CA1422

    // ---- immersive fullscreen (the document's ⛶ request) -------------------------------------

    private bool _immersive;

    private void SetImmersive(bool on)
    {
        _immersive = on;
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
