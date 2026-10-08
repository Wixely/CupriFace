using CupriFace.Dom;
using CupriFace.Style;

namespace CupriFace.Paint;

/// <summary>
/// What order a node's children paint in: document order, with <c>z-index</c> honoured among
/// siblings.
///
/// <para><b>Why this exists.</b> <c>z-index</c> was read for exactly one thing — ordering the top
/// layer, where a dialog has to sit above a dropdown — and ignored everywhere else, so two
/// overlapping positioned siblings always stacked in document order whatever either declared, and
/// even <c>z-index: -1</c> did not put an element behind. 71 of 165 corpus compositions declare the
/// property, 258 declarations in all; it is how a designed layout puts a scrim over a photo, a
/// caption above a gradient, a card above its own shadow. Document order is frequently the opposite
/// of what the author wanted, which is exactly why they reached for the property (#290).</para>
///
/// <para>It was also the silent class at its worst: every element present, every one the right size
/// and colour, the wrong one in front, and nothing reported — which reads as a design that was
/// always like that.</para>
///
/// <para><b>What this is not.</b> Real CSS sorts within a STACKING CONTEXT, and which elements
/// establish one (opacity &lt; 1, a transform, a filter, <c>isolation</c>, a positioned element with
/// a z-index of its own) decides whose z-indices are even comparable. None of that is modelled
/// here: the sort is per parent, among siblings. Two consequences worth knowing. A child's z-index
/// cannot lift it past its parent's siblings — a `z-index: 999` inside a card does not escape the
/// card — which is what a stacking context would do anyway for the common case of a positioned
/// parent, and is the conservative answer when it would not. And a negative z-index puts a child
/// behind its siblings but still in front of its parent's own background, where CSS would paint it
/// behind that background (and so, under an opaque parent, hide it completely). Behind the siblings
/// is what an author reaching for <c>-1</c> on a decorative layer actually wants, and hiding the
/// element would be a worse answer to arrive at by approximation.</para>
/// </summary>
internal static class PaintOrder
{
    /// <summary>The children of <paramref name="parent"/>, in paint order: lowest layer first,
    /// document order within a layer. Hit-testing walks the same order (and keeps the LAST hit), so
    /// the topmost-painted element is the one a click lands on.
    ///
    /// <para>Returns the node's own list whenever no child declares a z-index that applies, which is
    /// nearly every node in a document — so the ordinary case costs one pass over the children and
    /// allocates nothing. A frame paints every node, so this is on the hot path.</para></summary>
    internal static IReadOnlyList<RenderNode> Children(RenderNode parent)
    {
        var kids = parent.Children;
        var sort = false;
        for (var i = 0; i < kids.Count; i++)
            if (Layer(parent, kids[i]) != 0) { sort = true; break; }
        if (!sort) return kids;

        // OrderBy, not List.Sort: the sort must be STABLE, or siblings sharing a z-index would
        // reorder arbitrarily between frames and a layout would flicker.
        return kids.OrderBy(k => Layer(parent, k)).ToList();
    }

    /// <summary>The layer a child sorts into: its <c>z-index</c> where the property applies, else 0.
    /// <c>auto</c> and <c>0</c> are the same answer here — they differ only in whether a stacking
    /// context is established, which this model does not have.</summary>
    private static int Layer(RenderNode parent, RenderNode child) =>
        Applies(parent, child) ? child.Style.ZIndex : 0;

    /// <summary>Whether <c>z-index</c> applies to this child at all. CSS says: positioned elements,
    /// and flex/grid items whatever their position. On anything else the declaration is ignored —
    /// so a <c>z-index</c> on a static block in normal flow must not quietly start reordering a
    /// page that renders correctly today.</summary>
    private static bool Applies(RenderNode parent, RenderNode child) =>
        child.Style.Position != PositionType.Static
        || parent.Style.Display is DisplayType.Flex or DisplayType.Grid;
}
