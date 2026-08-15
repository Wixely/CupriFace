using Android.Content.PM;
using CupriFace;
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
}
