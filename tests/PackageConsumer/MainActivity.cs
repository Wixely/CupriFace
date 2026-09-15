using Android.App;
using Android.OS;

namespace CupriFace.PackageConsumer;

/// <summary>
/// Enough of an app for the project to be a real Android app rather than a shape. The check that
/// uses it never compiles this — it asks MSBuild which runtime the app resolved to, which is a
/// property evaluation — but a consumer project that could not build if you asked it to would be
/// testing something other than what consumers do.
/// </summary>
[Activity(Label = "package consumer", MainLauncher = true)]
public class MainActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState) => base.OnCreate(savedInstanceState);
}
