using System.Diagnostics.CodeAnalysis;

namespace CupriFace.DevTools;

/// <summary>
/// The switch the development tools hang off: the F12 inspector, hot reload of markup and styles,
/// and the doctor that runs on every save.
///
/// <para><b>On in a Debug build of an app, off in every other.</b> The switch is the app's, not
/// this library's — the library ships as a Release package either way — so the build sets it: the
/// CupriFace.Shell package's build targets add a <c>RuntimeHostConfigurationOption</c> named
/// <c>CupriFace.DevTools.IsSupported</c> whose value follows the app's <c>Configuration</c>. Set
/// the MSBuild property <c>CupriDevTools</c> to <c>true</c> or <c>false</c> to decide yourself.</para>
///
/// <para><b>Off means gone, not hidden.</b> The property is a feature switch the trimmer
/// understands: with it false, a trimmed or NativeAOT publish folds every <c>if
/// (CupriDevTools.IsSupported)</c> to false and removes the code behind it, so a Release binary
/// carries neither the inspector nor a file watcher — nothing to find, nothing to switch back on.
/// Every call into this namespace from a host must sit behind that test for this to hold.</para>
/// </summary>
public static class CupriDevTools
{
    /// <summary>The runtime configuration name the build sets.</summary>
    public const string SwitchName = "CupriFace.DevTools.IsSupported";

    /// <summary>Whether this process was built with the development tools. False unless the app's
    /// build said otherwise — so an app built without the CupriFace build targets gets none.</summary>
    [FeatureSwitchDefinition(SwitchName)]
    public static bool IsSupported { get; } = AppContext.TryGetSwitch(SwitchName, out var on) && on;
}
