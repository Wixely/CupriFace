using CupriFace.Dom;

namespace CupriFace.Interaction;

/// <summary>
/// Where a scrollbar's track and thumb are, as one answer both the painter and the hit-test ask.
///
/// <para>They used to work it out separately, three lines each, and the two copies had already
/// drifted apart: the hit-test padded its thumb by six pixels on the left and eight on the right to
/// make a five-pixel bar catchable, so the region you could grab was nowhere near the region you
/// could see. With a real track the two questions become the same question, and it is answered
/// once.</para>
///
/// <para>Public because the numbers are useful outside the engine too: an app reserving a gutter
/// beside a scrolling list needs to know how wide the column is, and guessing 12 is worse than
/// reading it.</para>
///
/// <para><b>The track is the whole hit surface.</b> It is wider than the thumb, invisible until the
/// pointer is over it, and it accepts a press anywhere along its length — on the thumb to drag, above
/// or below it to page. That is what makes a scrollbar feel reliable: the target is the column, not
/// the five pixels of bar inside it.</para>
/// </summary>
public static class Scrollbar
{
    /// <summary>The column the pointer has to be in. Wider than the thumb on purpose — a five-pixel
    /// target is a five-pixel target however carefully it is drawn.</summary>
    public const float TrackWidth = 12f;

    /// <summary>Resting thumb width, and the width it grows to while the pointer is over the track.
    /// The growth IS the feedback: it says the column will take a press, before one is made.</summary>
    public const float ThumbWidth = 5f;
    public const float ThumbWidthHot = 9f;

    /// <summary>A thumb never shrinks below this, however long the content — past a point it stops
    /// being something a pointer can catch.</summary>
    public const float MinThumbHeight = 28f;

    /// <summary>How far a press in the empty part of the track moves the content, as a fraction of
    /// the visible height. A page, less an overlap, the way every desktop scrollbar pages — the
    /// overlap is what keeps a line or two of context across the jump.</summary>
    public const float PageFraction = 0.9f;

    /// <summary>
    /// Does this node get a scrollbar at all?
    ///
    /// <para><see cref="RenderNode.IsScrollable"/> alone is not enough, because a box with no height
    /// but some padding has a NEGATIVE content height — so "content taller than the box" is
    /// arithmetically true of an element with no content in it, and every inline <c>&lt;code&gt;</c>
    /// chip on a page qualifies. Nothing ever showed it: the old thumb height divided by a zero
    /// content height, came out infinite, and was drawn somewhere off the world. The day that
    /// arithmetic was made safe, bars appeared beside paragraphs.</para>
    ///
    /// <para>The guard is HERE rather than on IsScrollable itself. Narrowing that changed how
    /// gradient-filled boxes elsewhere on the same page painted — measured, not guessed — and a
    /// scrollbar has no business redefining what the rest of the engine means by scrollable.</para>
    /// </summary>
    public static bool Applies(RenderNode n) => n.IsScrollable && n.ContentBoxHeight > 0.5f;

    /// <summary>The track's rectangle, in the same painted coordinates <paramref name="ax"/> and
    /// <paramref name="ay"/> are given in (the node's painted top-left).</summary>
    public static (float X, float Y, float W, float H) Track(RenderNode n, float ax, float ay)
    {
        var x = ax + n.Width - n.BorderRightW - TrackWidth;
        return (x, ay + n.ContentTopInset, TrackWidth, n.ContentBoxHeight);
    }

    /// <summary>The thumb's rectangle. <paramref name="hot"/> widens it to fill the track, which is
    /// the hover feedback.</summary>
    public static (float X, float Y, float W, float H) Thumb(RenderNode n, float ax, float ay, bool hot)
    {
        var track = Track(n, ax, ay);
        var h = ThumbHeight(n);
        var travel = MathF.Max(0f, track.H - h);
        var max = MathF.Max(1f, n.MaxScrollY);
        var y = track.Y + Math.Clamp(n.ScrollY, 0, n.MaxScrollY) / max * travel;
        var w = hot ? ThumbWidthHot : ThumbWidth;
        // Centred in the track, so growing on hover grows from the middle rather than drifting.
        return (track.X + (TrackWidth - w) / 2f, y, w, h);
    }

    public static float ThumbHeight(RenderNode n) =>
        MathF.Max(MinThumbHeight, n.ContentBoxHeight * n.ContentBoxHeight / MathF.Max(1f, n.ScrollContentHeight));

    /// <summary>Is the point inside the track column?</summary>
    public static bool InTrack(RenderNode n, float ax, float ay, float x, float y)
    {
        var t = Track(n, ax, ay);
        return x >= t.X && x <= t.X + t.W && y >= t.Y && y <= t.Y + t.H;
    }

    /// <summary>Is the point on the thumb itself (as opposed to the empty track around it)? Measured
    /// against the track's full width, not the thumb's: the thumb is what the column is FOR, and
    /// asking someone to hit five pixels inside a twelve-pixel column they are already in would be
    /// the original problem with extra steps.</summary>
    public static bool OnThumb(RenderNode n, float ax, float ay, float x, float y, bool hot)
    {
        if (!InTrack(n, ax, ay, x, y)) return false;
        var th = Thumb(n, ax, ay, hot);
        return y >= th.Y && y <= th.Y + th.H;
    }
}
