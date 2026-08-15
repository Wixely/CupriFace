using Android.Content.PM;
using CupriFace;
using CupriFace.Android;
using CupriFace.Demo;

namespace CupriFace.AndroidViewer;

/// <summary>The whole app. This brevity is the point: the host package owns the surface, the
/// input, the lifecycle and (in later phases) the IME and TalkBack — an app names its CupriApp.</summary>
[Activity(Label = "CupriFace", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : CupriActivity
{
    protected override CupriApp CreateApp() => new ShowcaseApp();
}
