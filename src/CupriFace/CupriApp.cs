using CupriFace.Components;
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
    public abstract string Html { get; }
    public abstract string Css { get; }

    public virtual string Title => "CupriFace App";
    public virtual int Width => 800;
    public virtual int Height => 600;
    public virtual SKColor Background => SKColors.White;

    /// <summary>Component library available to the markup (defaults to the built-ins).</summary>
    public virtual ComponentRegistry Components => ComponentRegistry.Default();

    /// <summary>Optional bound model for <c>{{...}}</c> interpolation and two-way controls.</summary>
    public virtual object? Model => null;

    /// <summary>Hook to register click handlers etc. after the document is built.</summary>
    public virtual void Configure(CupriDocument document) { }

    /// <summary>
    /// Given the window size, return the logical viewport the document is laid out at and a
    /// scale factor the host applies. Default = responsive (lay out at the window, scale 1).
    /// Override for zoom/hybrid/fixed scaling.
    /// </summary>
    public virtual PresentInfo Present(float windowWidth, float windowHeight) =>
        new(windowWidth, windowHeight, 1f);

    /// <summary>Build a ready-to-render document — identical on every host.</summary>
    public CupriDocument CreateDocument()
    {
        var doc = CupriDocument.Load(Html, Css).UseComponents(Components);
        if (Model is { } model) doc.Bind(model);
        Configure(doc);
        return doc;
    }
}
