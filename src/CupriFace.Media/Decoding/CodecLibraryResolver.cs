using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CupriFace.Media.Decoding;

/// <summary>
/// Finds <c>cupricodecs</c> when it lives in a <c>runtimes/&lt;rid&gt;/native/</c> tree that the
/// host doesn't resolve on its own.
///
/// Consuming the NuGet package there is nothing to do: .NET reads deps.json, picks the running
/// RID's native, and the default probe wins on the first try. But a PROJECT reference (or a
/// plain xcopy layout) has only loose files — and with six RIDs they cannot all sit flat beside
/// the app, because three pairs share a filename (<c>cupricodecs.dll</c> is both Windows RIDs,
/// and likewise the .so and .dylib). So those builds lay the natives out per-RID, and this
/// resolver picks the running one.
/// </summary>
internal static class CodecLibraryResolver
{
    internal const string LibraryName = "cupricodecs";

    [ModuleInitializer]
    internal static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(CodecLibraryResolver).Assembly, Resolve);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? search)
    {
        if (name != LibraryName) return 0;
        // Default probing first — that's the package path, and a flat layout beside the app.
        if (NativeLibrary.TryLoad(name, assembly, search, out var handle)) return handle;

        var file = OperatingSystem.IsWindows() ? "cupricodecs.dll"
                 : OperatingSystem.IsMacOS() ? "libcupricodecs.dylib"
                 : "libcupricodecs.so";
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", CurrentRid, "native", file);
        return NativeLibrary.TryLoad(path, out handle) ? handle : 0;   // 0 = "not found", not fatal
    }

    /// <summary>The RID folder name, built from the running OS and PROCESS architecture rather
    /// than read from <c>RuntimeInformation.RuntimeIdentifier</c>: that can report a portable
    /// RID (plain <c>"win"</c>) for a framework-dependent app, which matches no runtimes/ folder.
    /// Process architecture, not OS architecture, so an x64 process under emulation on an ARM
    /// machine gets the x64 native it can actually load.</summary>
    private static string CurrentRid =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
        + RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "-x64",
            Architecture.Arm64 => "-arm64",
            Architecture.X86 => "-x86",
            Architecture.Arm => "-arm",
            var other => "-" + other.ToString().ToLowerInvariant(),
        };
}
