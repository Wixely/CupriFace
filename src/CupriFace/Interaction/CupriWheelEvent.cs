using AngleSharp.Dom;

namespace CupriFace.Interaction;

/// <summary>A wheel event offered to an author-owned element before ordinary scrolling.</summary>
public sealed record CupriWheelEvent(
    float X,
    float Y,
    float DeltaY,
    float DeltaX,
    IElement Element,
    string Value,
    object? Model);
