using AngleSharp.Dom;
using CupriFace.Dom;

namespace CupriFace.Interaction;

/// <summary>A pointer event delivered to a click handler.</summary>
public readonly record struct CupriPointerEvent(float X, float Y, RenderNode Target, IElement Element);

/// <summary>An activation of a custom interaction primitive (registered via
/// <c>CupriDocument.OnAction</c>): the element carrying the registered <c>data-*</c> attribute, its
/// value, the bound model, and the pointer position. Fired by click or keyboard activation.</summary>
public readonly record struct CupriActionEvent(RenderNode Node, IElement Element, string Value, object? Model, float X, float Y);
