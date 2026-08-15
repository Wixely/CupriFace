using System.Reflection;

namespace CupriFace.Demo;

/// <summary>
/// What build am I looking at? Trivial on desktop (you launched it), genuinely hard on a phone:
/// a sideloaded APK is indistinguishable from the one it replaced, and Android refuses in-place
/// updates across CI's per-build debug keys — so an install that quietly failed leaves you testing
/// yesterday's app against today's fix notes. The samples show this string; a real app should too.
/// </summary>
public static class BuildInfo
{
    /// <summary>e.g. "v0.2.3 · built 2026-08-15 21:04 UTC". The version comes from the assembly's
    /// informational version (CI passes the tag), the stamp from an AssemblyMetadata attribute set
    /// at compile time — see DemoApp.csproj.</summary>
    public static string Describe()
    {
        var asm = typeof(BuildInfo).Assembly;

        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? asm.GetName().Version?.ToString() ?? "unknown";
        // Source-control builds append "+<commit sha>"; the hash is noise on a phone screen.
        if (version.IndexOf('+') is > 0 and var plus) version = version[..plus];

        var stamp = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
                       .FirstOrDefault(a => a.Key == "BuildStamp")?.Value;

        return stamp is { Length: > 0 } ? $"v{version} · built {stamp} UTC" : $"v{version}";
    }
}
