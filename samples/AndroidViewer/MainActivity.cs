using Android.Content.PM;
using Android.OS;
using CupriFace;
using CupriFace.Dom;
using CupriFace.Android;
using CupriFace.Demo;

namespace CupriFace.AndroidViewer;

/// <summary>The whole app — this brevity is the point: the host package owns the surface, the
/// input, the lifecycle, the IME and (Phase 8) TalkBack; an app names its CupriApp. The
/// phone-first MobileApp is the default, with the full desktop Showcase pushed on demand from
/// its About page — the parity proof, one tap away, with Back returning here.</summary>
[Activity(Label = "CupriFace", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : CupriActivity
{
    protected override CupriApp CreateApp()
    {
        var app = new MobileApp();
        app.LaunchRequested = _ => Host.Push(new ShowcaseApp());
        return app;
    }

    /// <summary>
    /// Start straight in the Showcase on a named section when the launch intent asks:
    /// <c>adb shell am start -n &lt;activity&gt; --es section 3d</c>.
    ///
    /// <para>The desktop Viewer has taken <c>--section</c> for exactly this reason since it was
    /// first useful, and the phone had no equivalent - so any page more than the front screen was
    /// reachable only by tapping through, which a gate can do only by guessing coordinates. Three
    /// lines here turn "drive the UI blind" into "ask for the page".</para>
    /// </summary>
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (Intent?.GetStringExtra("section") is { Length: > 0 } section)
            Host.Push(new ShowcaseApp(section));
    }

    /// <summary>
    /// The composition root, exactly where the desktop Viewer and the browser sample wire theirs.
    /// ShowcaseApp contributes one element with data-cupri-surface and no reference to a renderer.
    ///
    /// <para>This runs for the PUSHED Showcase too, not only the app created above: AndroidHost
    /// hands the same callback to every document it builds, including the ones SwapApp creates when
    /// Back pops or the About page launches the Showcase. That is why no new Push overload was
    /// needed - the hook already reached every app on the stack.</para>
    /// </summary>
    protected override void ConfigureDocument(CupriDocument document) =>
        Teapot3dSurface.TryAttach(document, m => global::Android.Util.Log.Info("cupri", m));
}
