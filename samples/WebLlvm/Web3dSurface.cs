using CupriFace.Demo.ThreeD;
using CupriFace.Gl;

namespace CupriFace.Samples.WebLlvm;

/// <summary>
/// The Showcase's 3D viewport in the BROWSER — the host where none of the desktop strategy works,
/// and where the package earns its keep most obviously.
///
/// <para>A wasm host renders to an <c>SKBitmap</c> and presents through <c>putImageData</c>, so
/// there is no GPU context to share and nothing to hand back. <c>GlViewport</c> takes the engine's
/// other lane instead: it declares itself host-composited, the painter punches a transparent hole at
/// the element's box, and the host creates a real <c>&lt;canvas&gt;</c> underneath and keeps it glued
/// there through scrolling, every <c>overflow</c> ancestor's clip and any transform on the chain.
/// That machinery is <c>&lt;cupri-video&gt;</c>'s, unchanged and reused.</para>
///
/// <para><b>Nothing in the engine was added for 3D.</b> <c>ISurfaceSource</c>, <c>HostComposited</c>
/// and the underlay seam are all public API a video already used.</para>
/// </summary>
internal static class Web3dSurface
{
    /// <summary>The stage colour behind the viewport — <c>.stage3d</c> in <c>ShowcaseApp.css</c>.</summary>
    private const float R = 0x0b / 255f, G = 0x0f / 255f, B = 0x18 / 255f;

    internal static GlViewport? TryAttach(CupriDocument doc, Action<string>? log = null)
    {
        log ??= _ => { };
        var content = TeapotContent.FromEmbeddedAsset(m => log("3d: " + m));
        if (content is null) return null;

        return GlViewport.Attach(doc, "showcase3d", content, new GlViewportOptions
        {
            Log = m => log("3d: " + m),
            // OPAQUE, and this is the only line in the file that differs from the desktop's — for a
            // real property of host compositing rather than a style choice. The hole is punched with
            // BlendMode.Src, so it erases everything already painted at that box INCLUDING the
            // element's own CSS background. On a desktop the model is drawn over that background and
            // picks it up for free; here there is nothing behind the canvas but the page. Clearing
            // transparent left the web viewport white while the desktop one was near-black, from
            // identical markup. The underlay has to supply the backdrop the hole took away.
            ClearColor = (R, G, B, 1f),
        });
    }
}
