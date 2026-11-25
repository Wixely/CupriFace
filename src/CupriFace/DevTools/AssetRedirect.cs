using System.Collections.Concurrent;
using System.Reflection;

namespace CupriFace.DevTools;

/// <summary>
/// Serves an embedded asset from the file it was built from, while hot reload is watching that file.
///
/// <para>An embedded resource is a copy taken at compile time, so editing <c>Assets/App.html</c>
/// changes nothing a running app can see. Redirecting the READ — rather than having hot reload read
/// the file itself — means <c>CupriApp.Html</c> is simply asked again, and an app that builds its
/// markup from the asset (substituting, concatenating, choosing by section) gets its own code run on
/// the new text instead of having it bypassed.</para>
///
/// <para>Only consulted when <see cref="CupriDevTools.IsSupported"/>, and only for an assembly hot
/// reload has registered a source root for.</para>
/// </summary>
internal static class AssetRedirect
{
    private static readonly ConcurrentDictionary<Assembly, string> Roots = new();

    /// <summary>The metadata key the resource build targets stamp the project directory under.</summary>
    internal const string RootMetadataKey = "CupriFace.AssetsRoot";

    public static void Register(Assembly assembly, string root) => Roots[assembly] = root;

    public static void Clear() => Roots.Clear();

    /// <summary>The file an embedded asset came from, when one is registered and it still exists.</summary>
    public static string? FileFor(Assembly assembly, string logicalName)
    {
        if (!Roots.TryGetValue(assembly, out var root)) return null;
        var path = Path.Combine(root, logicalName.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Find the project directory an assembly's <c>Assets/…</c> resources were embedded from.
    ///
    /// <para>First the directory the build stamped into the assembly (CupriFace.Resources.targets
    /// does this in a Debug build), which is exact. Then two guesses from where the assembly is
    /// running, for a project built without the stamp: the folders above the output directory —
    /// the app's own project, when it runs from <c>bin/Debug/net10.0</c> — and, at each of those, a
    /// sibling folder named after the assembly, which is how a library project sits beside the app
    /// that references it.</para>
    /// </summary>
    public static string? FindRoot(Assembly assembly, string logicalName)
    {
        var relative = logicalName.Replace('/', Path.DirectorySeparatorChar);
        foreach (var attr in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (attr.Key == RootMetadataKey && attr.Value is { Length: > 0 } stamped
                && File.Exists(Path.Combine(stamped, relative)))
                return stamped;

        var name = assembly.GetName().Name;
        string? dir;
        try { dir = Path.GetDirectoryName(assembly.Location); }
        catch (NotSupportedException) { dir = null; }   // single-file bundles have no location
        if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, relative))) return dir;
            if (name is not null && File.Exists(Path.Combine(dir, name, relative))) return Path.Combine(dir, name);
        }
        return null;
    }
}
