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
/// </summary>
public enum NavigationDirection
{
    Up,
    Down,
    Left,
    Right,
}
