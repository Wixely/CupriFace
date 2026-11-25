[assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof(CupriFace.DevTools.HotReloadHandler))]

namespace CupriFace.DevTools;

/// <summary>
/// The runtime's hook into a C# hot reload — the debugger's (C# Dev Kit, Visual Studio) or
/// <c>dotnet watch</c>'s. After a code edit has been applied, every live
/// <see cref="HotReloadSession"/> re-reads its app's <c>Html</c> and <c>Css</c>, so markup written
/// as a C# string, or a model property whose value changed, shows up without a restart.
///
/// <para>Under NativeAOT and in a trimmed build the runtime cannot apply code edits at all, never
/// calls this, and the feature switch removes what it would have called. Nothing about an AOT build
/// changes by having it here.</para>
/// </summary>
internal static class HotReloadHandler
{
    /// <summary>Called first. The engine keeps no reflection caches keyed by type, so there is
    /// nothing to clear; the method exists because the runtime looks for the pair.</summary>
    public static void ClearCache(Type[]? updatedTypes) { }

    /// <summary>Called after the edit is applied, on a thread of the runtime's choosing.</summary>
    public static void UpdateApplication(Type[]? updatedTypes)
    {
        if (CupriDevTools.IsSupported) HotReloadSession.NotifyCodeUpdated();
    }
}
