using AngleSharp.Dom;
using CupriFace.Dom;
using CupriFace.Interaction;

namespace CupriFace;

/// <summary>
/// Touch adjustment: a tap from a finger that lands near, but not on, something interactive is
/// moved onto it.
///
/// <para><b>Why.</b> A fingertip is a 7–10 mm contact patch and the OS reports one point from it,
/// so a layout that is fine for a 1 px mouse pointer makes fingers miss — the first Steam Deck
/// session with working touch reported it as "a bit too accurate". Browsers and mobile toolkits
/// all correct for this the same way: search a small radius around the point for the nearest
/// interactive element and dispatch there. Only fingers get it; a mouse means what it points at.</para>
///
/// <para><b>What "onto it" means.</b> The nearest point INSIDE the target's on-screen box, not its
/// centre: a tap beside a slider must land on the slider's edge, not jump to its midpoint. The
/// chosen point is then hit-tested for real, and the snap is abandoned if that point does not
/// resolve to the candidate — an overlay's backdrop, a clipped scroller, a transformed tile —
/// so a covered control can never attract a tap through whatever is covering it.</para>
/// </summary>
public sealed partial class CupriDocument
{
    /// <summary>
    /// How far, in host-logical pixels, a finger may land from an interactive element and still be
    /// taken to have meant it. 12 is roughly a third of a fingertip. Zero disables adjustment, for
    /// an app that draws its own precise targets and wants taps exactly where they fall.
    /// </summary>
    public float TouchAdjustRadius { get; set; } = 12f;

    /// <summary>
    /// A click from a FINGER: <see cref="DispatchClick"/> with touch adjustment. Hosts call this for
    /// taps and <see cref="DispatchClick"/> for mouse buttons; the engine cannot tell the two apart
    /// from coordinates alone, and must not guess, because a mouse that snapped would be a bug.
    /// </summary>
    public bool DispatchTap(float x, float y, int clickCount = 1) =>
        Bump(DispatchClickCore(Zc(x), Zc(y), clickCount, Zc(TouchAdjustRadius)));

    /// <summary>Move a tap that hit nothing interactive onto the nearest interactive element within
    /// <paramref name="radius"/> (document units). Returns the original triple when the tap already
    /// meant something, or nothing qualifies.</summary>
    private (RenderNode? Hit, float X, float Y) AdjustForTouch(RenderNode? hit, float x, float y, float radius)
    {
        // The IsOnTapTarget check is an EARLY-OUT, not a correctness rule: a tap already on a
        // control gives that control a clamped distance of zero, so it wins the nearest-first
        // search below regardless (mutation-tested: removing this changes no outcome). What it
        // saves is walking the whole tree on every ordinary tap, which is nearly all of them.
        if (radius <= 0 || IsOnTapTarget(hit)) return (hit, x, y);

        // Every interactive box within reach, nearest first; on a tie the SMALLER box, so a big
        // panel that happens to be clickable does not win over the button sitting inside it.
        var candidates = new List<(RenderNode Node, float Distance, float Area, float Px, float Py)>();
        void Walk(RenderNode n)
        {
            // ScreenBox before the size check, not after: an inline link has NO box of its own —
            // it lives in text fragments, and ScreenBox answers with the union of the text it
            // occupies. Filtering on Width/Height first would exclude every link in a paragraph,
            // which is about the most common thing a finger reaches for.
            if (n.Element is { } el && IsTapTarget(el)
                && HitTesting.ScreenBox(n) is var (bx, by, bw, bh) && bw > 0.5f && bh > 0.5f)
            {
                // Nearest point inside the box — half a pixel in from the edge so it is
                // unambiguously inside for the hit test that follows.
                var px = Math.Clamp(x, bx + 0.5f, bx + bw - 0.5f);
                var py = Math.Clamp(y, by + 0.5f, by + bh - 0.5f);
                var dx = px - x; var dy = py - y;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                if (d <= radius) candidates.Add((n, d, bw * bh, px, py));
            }
            foreach (var c in n.Children) Walk(c);
        }
        Walk(_root);
        if (candidates.Count == 0) return (hit, x, y);

        candidates.Sort((a, b) =>
        {
            var byDistance = a.Distance.CompareTo(b.Distance);
            return byDistance != 0 ? byDistance : a.Area.CompareTo(b.Area);
        });

        foreach (var (node, _, _, px, py) in candidates)
        {
            // The snap is real only if the adjusted point actually resolves to this node. Anything
            // painted over it — a dialog backdrop, a dropdown, a sibling with a higher z-index —
            // or clipping by a scroller makes the hit test answer something else, and that is the
            // correct answer: what the finger would have reached had it landed exactly there.
            var verified = HitTesting.HitTest(_root, px, py);
            for (var n = verified; n is not null; n = n.Parent)
                if (ReferenceEquals(n, node)) return (verified, px, py);
        }
        return (hit, x, y);
    }

    /// <summary>Did the tap already land on (or inside) something that acts on a press? Then it
    /// meant that, and nothing nearer-by-centre may steal it.</summary>
    private bool IsOnTapTarget(RenderNode? hit)
    {
        for (var n = hit; n is not null; n = n.Parent)
            if (n.Element is { } el && IsTapTarget(el)) return true;
        return false;
    }

    /// <summary>The element itself is a control — not merely inside one — and is not disabled. A
    /// disabled button must not pull a tap away from a live one beside it.</summary>
    private bool IsTapTarget(IElement el) =>
        !IsDisabled(el) && (ActsOnClick(el) || IsFocusableRole(el));
}
