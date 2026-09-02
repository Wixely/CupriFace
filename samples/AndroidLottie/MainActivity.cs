using Android.Content.PM;
using CupriFace;
using CupriFace.Android;

namespace CupriFace.Samples.AndroidLottie;

/// <summary>The whole app. The brevity is the point, and it is the SAME brevity AndroidViewer has:
/// the host package owns the surface, the input and the lifecycle, and an app names its CupriApp.
/// That the Lottie package needs no word here is the thing worth noticing — it is wired entirely
/// inside the portable <see cref="LottieApp"/>, which desktop and the web hosts run unchanged.</summary>
[Activity(Label = "CupriFace Lottie", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : CupriActivity
{
    protected override CupriApp CreateApp() => new LottieApp();
}
