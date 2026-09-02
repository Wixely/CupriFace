using System.Reflection;
using AngleSharp.Dom;
using CupriFace.Components;

namespace CupriFace.Lottie;

/// <summary>
/// Turning the optional package on: one registration for the element, one for the players.
///
/// <code>
/// public override ComponentRegistry Components => base.Components.UseLottie();
/// public override void Configure(CupriDocument doc) => doc.UseLottie(GetType().Assembly);
/// </code>
///
/// <para>Two calls rather than one because they answer to different owners: the registry is the app's
/// component vocabulary, the document is where live sources live. It is the same split
/// <c>UseComponents</c> and <c>UseVideo</c> already have.</para>
/// </summary>
public static class LottieExtensions
{
    /// <summary>Add <c>&lt;cupri-lottie&gt;</c> to a component registry.</summary>
    public static ComponentRegistry UseLottie(this ComponentRegistry registry) =>
        registry.Register(new LottieComponent());

    /// <summary>Wire Lottie players to this document: open one per <c>&lt;cupri-lottie src&gt;</c> as it
    /// appears, retire it when its element goes.
    ///
    /// <para><paramref name="assembly"/> is where embedded <c>src</c> values are resolved from — the
    /// app's own assembly, the same place <c>UseImages</c> looks. A <c>src</c> naming a file on disk is
    /// read from disk instead.</para>
    ///
    /// <para>Rides <see cref="CupriDocument.OnRebuilt"/>, because the DOM is rebuilt on every model
    /// change and a player has to survive that without being reopened — reopening would restart every
    /// animation on the page each keystroke.</para></summary>
    public static CupriDocument UseLottie(this CupriDocument doc, Assembly? assembly = null)
    {
        var players = new Dictionary<string, LottiePlayer>(StringComparer.Ordinal);
        doc.OnRebuilt(dom =>
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var el in dom.QuerySelectorAll("[data-cupri-surface]"))
            {
                var key = el.GetAttribute("data-cupri-surface")!;
                if (!key.StartsWith("lottie:", StringComparison.Ordinal)) continue;
                seen.Add(key);
                if (players.ContainsKey(key)) continue;          // already open: a rebuild is not a reload

                var src = key["lottie:".Length..];
                if (Load(src, assembly) is not { } json) continue;
                var loop = el.GetAttribute("loop") is not "false";
                var autoplay = el.GetAttribute("autoplay") is not "false";
                if (LottiePlayer.TryCreate(json, loop, autoplay) is not { } player) continue;

                players[key] = player;
                doc.Surfaces.Register(key, player);
            }

            // Retire players whose element has gone — a section switched away, a row removed. Component
            // expansion skips display:none subtrees, so this stops an animation the moment it is hidden
            // rather than leaving it burning frames behind a hidden panel.
            foreach (var gone in players.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                doc.Surfaces.Unregister(gone);
                players[gone].Dispose();
                players.Remove(gone);
            }
        });

        // No per-frame pump here on purpose. A ticking surface already keeps a render-on-demand
        // host awake (HasActiveAnimations folds in Surfaces.AnyTicking), and the player advances its
        // own clock when the paint path asks for a frame — so the animation is driven by the thing
        // that actually draws it, and a paused one costs nothing.
        return doc;
    }

    private static byte[]? Load(string src, Assembly? assembly)
    {
        if (File.Exists(src)) return File.ReadAllBytes(src);
        if (assembly is null) return null;
        // Embedded resources are named by the generator as <asm>.<path with dots>; match on the tail so
        // an app can write src="Assets/spinner.json" and not care about the prefix.
        var wanted = src.Replace('/', '.').Replace('\\', '.');
        var name = assembly.GetManifestResourceNames()
                           .FirstOrDefault(n => n.EndsWith(wanted, StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
