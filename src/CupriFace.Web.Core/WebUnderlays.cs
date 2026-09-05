using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Paint;
using CupriFace.Style;

namespace CupriFace.Web;

/// <summary>
/// Keeps every host-composited surface's DOM element glued to the box the engine laid out for it.
///
/// <para>This was <c>WebVideoBackend.SyncRects</c>, and it moved because none of it was ever about
/// video: it maps an engine box to a page element through the clip chain and the transform chain,
/// which is the same job whether the element under the hole is a <c>&lt;video&gt;</c> the browser
/// decodes into or a <c>&lt;canvas&gt;</c> an app renders WebGL into. Generalising it is what lets a
/// 3D viewport reuse work that already survives scrolling, <c>overflow</c> ancestors, hover-lift
/// transforms and <c>object-fit</c> — none of which a fresh implementation gets right first time.</para>
///
/// <para>Two kinds of participant, and the difference is only WHO CREATES the element. A video owns
/// its own (its lifetime is loading and playback, not layout) and reports
/// <see cref="ISurfaceSource.UnderlayElement"/> as null. A surface that returns <c>"canvas"</c> gets
/// one made here on first sight and removed when it goes. Both are then synced identically.</para>
/// </summary>
internal sealed class WebUnderlays(IWebBridge js)
{
    private readonly IWebBridge _js = js;

    // Elements this class created, by surface key. Video's own ids come from WebVideoBackend and are
    // deliberately NOT in here — it allocates from 1 upwards, so these start high enough that the two
    // ranges cannot meet. A shared allocator would be tidier and would have meant touching video's
    // id handling, which has a browser gate over it and no reason to change.
    private const int IdBase = 1_000_000;
    private readonly Dictionary<string, int> _created = new(StringComparer.Ordinal);
    private int _next = IdBase;

    /// <summary>True when anything is underlaid and live, which is what makes the present path
    /// convert to straight alpha — without that, a punched hole never reaches the page as
    /// transparent and the underlay stays invisible behind an opaque frame.</summary>
    internal bool Any => _created.Count > 0;

    /// <summary>Called after every painted frame. Walks the tree once, finds every element carrying a
    /// surface key whose source is host-composited, creates any element that needs creating, and
    /// sends each one its rect.</summary>
    /// <param name="existingId">Resolves a surface key to an element the host ALREADY owns — video's
    /// player id. Surfaces that create their own element report no <c>UnderlayElement</c>, so without
    /// this they would be walked past and never synced, and every video would sit at its first
    /// laid-out box for ever.</param>
    internal void Sync(CupriDocument doc, float scale, Func<string, int?> existingId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(doc.Root, doc, scale, seen, existingId);

        // Retire elements whose node has gone — a section switched away, a row removed. Without this
        // a canvas outlives its element and floats over the page at its last known box.
        if (_created.Count == 0) return;
        List<string>? gone = null;
        foreach (var key in _created.Keys)
            if (!seen.Contains(key)) (gone ??= []).Add(key);
        if (gone is null) return;
        foreach (var key in gone)
        {
            _js.UnderlayClose(_created[key]);
            _created.Remove(key);
        }
    }

    private void Walk(RenderNode node, CupriDocument doc, float scale, HashSet<string> seen,
                      Func<string, int?> existingId)
    {
        // NOT gated on HostComposited, and a test caught that it must not be. HostComposited decides
        // whether the PAINTER punches a hole; whether an ELEMENT needs positioning is a different
        // question, and the answer is "as soon as it exists". A <video> reports HostComposited only
        // once it can show pixels (poster paints until then), so gating here left every video
        // unpositioned until it had loaded — and then positioned at wherever it first laid out.
        if (node.SurfaceKey is { Length: > 0 } key && doc.Surfaces.Get(key) is { } source)
        {
            if (source.UnderlayElement is { Length: > 0 } kind)
            {
                seen.Add(key);
                if (!_created.TryGetValue(key, out var id))
                {
                    id = ++_next;
                    _created[key] = id;
                    // Only "canvas" exists today. An unknown kind creates nothing rather than
                    // guessing, so a typo shows up as a missing underlay instead of a stray element.
                    if (kind == "canvas") _js.UnderlayOpenCanvas(id, key);
                }
                SendRect(id, node, scale);
            }
            else if (existingId(key) is { } ownId)
            {
                // The surface owns its element (video). We do not create or destroy it — only keep
                // it glued to the box, which is the half that was never video-specific.
                SendRect(ownId, node, scale);
            }
        }
        foreach (var child in node.Children) Walk(child, doc, scale, seen, existingId);
    }

    /// <summary>The geometry, moved verbatim from the video path: on-screen box in device pixels, the
    /// visible intersection with every clipping ancestor as an inset clip-path, and the engine's own
    /// transform chain as a CSS matrix. A DOM element ignores engine clips and engine transforms, so
    /// each has to be recreated in terms the browser applies itself.</summary>
    internal void SendRect(int id, RenderNode node, float scale)
    {
        if (!node.LaidOut) { Hide(id); return; }

        var (x, y, w, h) = HitTesting.ScreenBox(node);

        float visL = x, visT = y, visR = x + w, visB = y + h;
        for (var a = node.Parent; a is not null; a = a.Parent)
        {
            if (a.Style.Overflow == OverflowMode.Visible) continue;
            var (ax, ay, aw, ah) = HitTesting.ScreenBox(a);
            visL = MathF.Max(visL, ax);
            visT = MathF.Max(visT, ay);
            visR = MathF.Min(visR, ax + aw);
            visB = MathF.Min(visB, ay + ah);
        }
        if (visR <= visL || visB <= visT) { Hide(id); return; }

        var fit = node.Element?.GetAttribute("data-object-fit") ?? "contain";

        // A transformed ancestor (hover lift, transform transition) moves the painted HOLE — the
        // element must follow the identical mapping. CSS matrix(a,b,c,d,e,f) with transform-origin
        // 0 0 applies in the element's own frame at its laid-out position P: final = P + linear·local
        // + (e,f). The engine's mapping is final = M·(P + local), so e,f = M·P − P (+ M's own
        // translation), computed here in device pixels.
        var m = HitTesting.ScreenTransform(node);
        double ta = m.ScaleX, tb = m.SkewY, tc = m.SkewX, td = m.ScaleY, te = 0, tf = 0;
        if (!m.IsIdentity)
        {
            var mapped = m.MapPoint(x, y);
            te = (mapped.X - x) * scale;
            tf = (mapped.Y - y) * scale;
        }

        _js.UnderlayRect(id,
            x * scale, y * scale, w * scale, h * scale,
            (visT - y) * scale,            // clip-path inset: top
            (x + w - visR) * scale,        // right
            (y + h - visB) * scale,        // bottom
            (visL - x) * scale,            // left
            true, fit, ta, tb, tc, td, te, tf);
    }

    private void Hide(int id) => _js.UnderlayRect(id, 0, 0, 0, 0, 0, 0, 0, 0, false, "", 1, 0, 0, 1, 0, 0);
}
