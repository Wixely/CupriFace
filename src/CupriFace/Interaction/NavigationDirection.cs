namespace CupriFace.Interaction;

/// <summary>
/// A direction to move keyboard/controller focus in, for
/// <see cref="CupriDocument.MoveFocus(NavigationDirection)"/>.
///
/// <para>Separate from Tab order because they answer different questions. Tab asks "what is next in
/// the document" — one dimension, author-ordered, and the right model for a form. A D-pad or a
/// thumbstick asks "what is ABOVE this" — two dimensions, decided by where things ended up on screen,
/// and the only model that makes sense when the user is pointing a stick at a grid of buttons from
/// the sofa.</para>
///
/// <para><b>The four diagonals are scored differently from the four orthogonals</b>, and deliberately
/// so. An orthogonal direction accepts a 45° cone and then prefers anything whose span overlaps the
/// control you are leaving, which is what keeps a column a column. A diagonal has no column to stay
/// in — "down-right" names a corner, not a lane — so it accepts the whole quadrant and simply takes
/// the nearest thing in it. Sharing the orthogonal rule would make diagonals nearly unusable: a 45°
/// cone centred on the diagonal rejects anything more sideways-than-45°, which on a staggered layout
/// is most of what the user is actually aiming at.</para>
/// </summary>
public enum NavigationDirection
{
    Up,
    Down,
    Left,
    Right,

    UpLeft,
    UpRight,
    DownLeft,
    DownRight,
}

/// <summary>Helpers for composing and decomposing <see cref="NavigationDirection"/>.</summary>
public static class NavigationDirections
{
    /// <summary>True for the four corner directions.</summary>
    public static bool IsDiagonal(this NavigationDirection d) => d >= NavigationDirection.UpLeft;

    /// <summary>True when the two directions lie on opposite axes (one vertical, one horizontal), so
    /// they combine into a diagonal rather than fighting each other. Both must be orthogonal.</summary>
    public static bool CombinesWith(this NavigationDirection a, NavigationDirection b) =>
        !a.IsDiagonal() && !b.IsDiagonal() && IsVertical(a) != IsVertical(b);

    /// <summary>The corner the two orthogonal directions name together, in either order. Returns null
    /// when they do not combine (same axis, or either already a diagonal).</summary>
    public static NavigationDirection? Combine(NavigationDirection a, NavigationDirection b)
    {
        if (!a.CombinesWith(b)) return null;
        var (vertical, horizontal) = IsVertical(a) ? (a, b) : (b, a);
        return (vertical, horizontal) switch
        {
            (NavigationDirection.Up, NavigationDirection.Left) => NavigationDirection.UpLeft,
            (NavigationDirection.Up, _) => NavigationDirection.UpRight,
            (_, NavigationDirection.Left) => NavigationDirection.DownLeft,
            _ => NavigationDirection.DownRight,
        };
    }

    private static bool IsVertical(NavigationDirection d) =>
        d is NavigationDirection.Up or NavigationDirection.Down;
}
