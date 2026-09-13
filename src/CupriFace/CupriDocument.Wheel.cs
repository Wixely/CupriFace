using CupriFace.Interaction;

namespace CupriFace;

public sealed partial class CupriDocument
{
    private readonly List<(string Attribute, Func<CupriWheelEvent, bool> Handler)> _wheelHandlers = new();

    /// <summary>Offer wheel input to elements carrying <paramref name="dataAttribute"/> before
    /// ordinary document scrolling. Returning true consumes the wheel and refreshes model binding.</summary>
    public CupriDocument OnWheel(string dataAttribute, Func<CupriWheelEvent, bool> handler)
    {
        _wheelHandlers.Add((dataAttribute, handler));
        return this;
    }

    private bool DispatchAuthoredWheel(float x, float y, float deltaY, float deltaX)
    {
        if (_wheelHandlers.Count == 0) return false;
        for (var node = HitTesting.HitTest(_root, x, y); node is not null; node = node.Parent)
        {
            if (node.Element is not { } element) continue;
            foreach (var (attribute, handler) in _wheelHandlers)
            {
                if (!element.HasAttribute(attribute)) continue;
                if (!handler(new CupriWheelEvent(
                        x, y, deltaY, deltaX, element,
                        element.GetAttribute(attribute) ?? "", _model)))
                    continue;
                Refresh();
                return true;
            }
        }
        return false;
    }
}
