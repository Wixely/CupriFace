using AngleSharp.Dom;
using CupriFace.Dom;

namespace CupriFace.Interaction;

/// <summary>A pointer event delivered to a click handler.</summary>
public readonly record struct CupriPointerEvent(float X, float Y, RenderNode Target, IElement Element);
