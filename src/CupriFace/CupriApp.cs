using CupriFace.Components;
using CupriFace.Resources;
using SkiaSharp;

namespace CupriFace;

/// <summary>How the document is presented into the window (see <see cref="CupriApp.Present"/>).</summary>
public readonly record struct PresentInfo(float LogicalWidth, float LogicalHeight, float Scale);

/// <summary>
/// A portable application definition: markup, styles, components, model, and behaviour —
/// with **no** windowing or platform dependency. The same subclass runs on a desktop host
/// (GL/SDL window) or a web host (WASM → &lt;canvas&gt;); "exporting to a website" is just
/// recompiling this class against the web host. See <c>DesktopHost</c> and <c>CupriView</c>.
/// </summary>
public abstract class CupriApp
{
    /// <summary>Where the markup is loaded from (embedded resource, file, or URL). Preferred over
    /// overriding <see cref="Html"/> directly — see <see cref="EmbeddedAsset"/>.</summary>
    protected virtual CupriSource? MarkupSource => null;

    /// <summary>Where the stylesheet is loaded from. Optional (an app may inline all CSS in its markup).</summary>
    protected virtual CupriSource? StyleSource => null;

    /// <summary>The document markup. Defaults to reading <see cref="MarkupSource"/>; override either.</summary>
    public virtual string Html => MarkupSource?.ReadText()
        ?? throw new InvalidOperationException($"{GetType().Name} must override Html or MarkupSource.");

    /// <summary>The stylesheet. Defaults to reading <see cref="StyleSource"/> (empty if none).</summary>
    public virtual string Css => StyleSource?.ReadText() ?? "";

    /// <summary>Convenience: an embedded resource in this app's own assembly, e.g.
    /// <c>EmbeddedAsset("Assets/App.html")</c>. The generated <c>Assets</c> class is the typed way.</summary>
    protected CupriSource EmbeddedAsset(string logicalName) => CupriSource.Embedded(GetType().Assembly, logicalName);

    public virtual string Title => "CupriFace App";
    public virtual int Width => 800;
    public virtual int Height => 600;
    public virtual SKColor Background => SKColors.White;

    /// <summary>Render with a transparent background so the UI composites over what's behind it — the
    /// desktop (a transparent GL window) or an HTML page (a transparent canvas overlay). The host
    /// clears to transparent instead of <see cref="Background"/>.</summary>
    public virtual bool Transparent => false;

    /// <summary>Borderless window — no title bar or chrome (for HUDs / overlays).</summary>
    public virtual bool Frameless => false;

    /// <summary>Keep the window above other windows (always-on-top).</summary>
    public virtual bool TopMost => false;

    /// <summary>Opt in to the commit-snapshot render-thread split (DESIGN §7.2): the UI thread builds
    /// the display list and a background thread rasterises it, so rasterisation never blocks input.
    /// Wired for the CPU/SDL software path; the GL path renders inline. Default off.</summary>
    public virtual bool ThreadedRender => false;

    /// <summary>Component library available to the markup (defaults to the built-ins).</summary>
    public virtual ComponentRegistry Components => ComponentRegistry.Default();

    /// <summary>Optional bound model for <c>{{...}}</c> interpolation and two-way controls.</summary>
    public virtual object? Model => null;

    /// <summary>Hook to register click handlers etc. after the document is built.</summary>
    public virtual void Configure(CupriDocument document) { }

    /// <summary>
    /// If &gt; 0, the host re-binds the model (<c>doc.Refresh()</c>) at this cadence in seconds,
    /// so computed values that drift over time — e.g. live diagnostics like RAM usage — update
    /// without user interaction. Default 0 = never (re-binds only on interaction).
    /// </summary>
    public virtual double RefreshIntervalSeconds => 0;

    /// <summary>
    /// Given the window size, return the logical viewport the document is laid out at and a
    /// scale factor the host applies. Default = responsive (lay out at the window, scale 1).
    /// Override for zoom/hybrid/fixed scaling.
    /// </summary>
    public virtual PresentInfo Present(float windowWidth, float windowHeight) =>
        new(windowWidth, windowHeight, 1f);

    /// <summary>Dev aid: outline every element box in the window (see <see cref="CupriDocument.DebugOverlay"/>).</summary>
    public virtual bool DebugOverlay => false;

    /// <summary>Build a ready-to-render document — identical on every host.</summary>
    public CupriDocument CreateDocument()
    {
        var doc = CupriDocument.Load(Html, Css).UseComponents(Components).UseImages(GetType().Assembly);
        if (Model is { } model) doc.Bind(model);
        doc.DebugOverlay = DebugOverlay;
        Configure(doc);
        return doc;
    }
}
