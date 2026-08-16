using CupriFace.Accessibility;
using CupriFace.Demo;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The phone sample has to contain something to PRESS for each feature that CI cannot drive.
/// Multi-touch, the IME contract and autofill are all proven by a person with a device, and a
/// checklist is worthless if the app has no gesture tile, no autofilled field and no sideways row.
/// These tests keep the demo surface honest — they are the reason the manual pass is possible.
/// </summary>
public class MobileDemoSurfaceTests
{
    private const int W = 393, H = 771;

    private static RenderNode? Find(RenderNode n, Func<RenderNode, bool> pred)
    {
        if (pred(n)) return n;
        foreach (var c in n.Children) if (Find(c, pred) is { } f) return f;
        return null;
    }

    private static AccessibilityNode? Named(AccessibilityNode n, string name)
    {
        if (n.Name == name) return n;
        foreach (var c in n.Children) if (Named(c, name) is { } f) return f;
        return null;
    }

    private static void Nav(CupriDocument doc, string page)
    {
        var tree = doc.BuildAccessibilityTree(W, H);
        Assert.True(doc.AccessibilityActivate(Named(tree, page)!.Path));
        using var _ = doc.RenderToImage(W, H);
    }

    [Fact]
    public void Two_fingers_on_the_tile_scale_and_rotate_it()
    {
        var app = new MobileApp();
        using var doc = app.CreateDocument();
        using (doc.RenderToImage(W, H)) { }

        var tile = Find(doc.Root, n => n.Element?.HasAttribute("data-gesture") == true);
        Assert.NotNull(tile);
        var (x, y, w, h) = HitTesting.ScreenBox(tile!);
        var model = (MobileModel)app.Model!;

        // Two fingers 40px apart horizontally; the second swings down and out, which both spreads
        // the span (40 -> ~63, so ~1.6x) and twists the line between them (0° -> ~72°).
        Assert.True(doc.DispatchPointer(1, PointerPhase.Down, x + w / 2 - 20, y + h / 2));
        Assert.True(doc.DispatchPointer(2, PointerPhase.Down, x + w / 2 + 20, y + h / 2));
        doc.DispatchPointer(2, PointerPhase.Move, x + w / 2, y + h / 2 + 60);

        Assert.True(model.TileScale > 1.2, $"pinch did not scale it ({model.TileScale:0.00})");
        Assert.True(Math.Abs(model.TileRotation) > 20, $"twist did not rotate it ({model.TileRotation:0}°)");
        Assert.Contains("scale(", model.TileTransform);

        doc.DispatchPointer(1, PointerPhase.Up, x, y);
        doc.DispatchPointer(2, PointerPhase.Up, x, y);
        Assert.False(doc.HasCapturedPointers);
    }

    [Fact]
    public void The_sideways_row_actually_overflows_and_scrolls()
    {
        var app = new MobileApp();
        using var doc = app.CreateDocument();
        using (doc.RenderToImage(W, H)) { }

        var strip = Find(doc.Root, n => n.Element?.ClassList.Contains("strip") == true);
        Assert.NotNull(strip);
        Assert.True(strip!.IsScrollableX, "the demo row fits the screen, so there is nothing to drag");

        var (x, y, w, h) = HitTesting.ScreenBox(strip);
        Assert.True(doc.DispatchWheel(x + w / 2, y + h / 2, 0, 120));
        Assert.True(strip.ScrollX > 50);
    }

    [Fact]
    public void The_form_offers_fields_a_password_manager_can_fill()
    {
        var app = new MobileApp();
        using var doc = app.CreateDocument();
        using (doc.RenderToImage(W, H)) { }
        Nav(doc, "Form");

        var hints = new List<string>();
        void Walk(AccessibilityNode n)
        {
            if (n.AutofillHint is { Length: > 0 } h) hints.Add(h);
            foreach (var c in n.Children) Walk(c);
        }
        Walk(doc.BuildAccessibilityTree(W, H));

        Assert.Contains("username", hints);
        Assert.Contains("current-password", hints);
    }

    [Fact]
    public void The_email_field_asks_for_the_right_keyboard()
    {
        var app = new MobileApp();
        using var doc = app.CreateDocument();
        using (doc.RenderToImage(W, H)) { }
        Nav(doc, "Form");

        var email = Find(doc.Root, n => n.Element?.GetAttribute("inputmode") == "email");
        Assert.NotNull(email);

        // Focus it the way a finger would, then read what the host would hand the IME.
        var target = Find(doc.Root, n => n.Element?.GetAttribute("aria-label") == "Email"
                                         && n.Element?.GetAttribute("role") == "textbox")
                     ?? email!;
        var (x, y, w, h) = HitTesting.ScreenBox(target);
        doc.DispatchClick(x + w / 2, y + h / 2);
        using (doc.RenderToImage(W, H)) { }

        var state = doc.GetTextInputState();
        Assert.True(state.Focused);
        Assert.Equal("email", state.InputMode);
        Assert.Equal("next", state.EnterKeyHint);
        Assert.Equal("you@example.com", state.Placeholder);
    }

    [Fact]
    public void The_capability_hint_swaps_with_the_input_profile()
    {
        // Touch and mouse get different copy from the same markup, through the cascade alone.
        var app = new MobileApp();
        using var doc = app.CreateDocument();
        using (doc.RenderToImage(W, H)) { }

        static bool Shown(CupriDocument d, string cls) =>
            Find(d.Root, n => n.Element?.ClassList.Contains(cls) == true) is { } n
            && n.Style.Display != CupriFace.Style.DisplayType.None;

        Assert.True(Shown(doc, "mouse-only"));
        Assert.False(Shown(doc, "touch-only"));

        doc.InputProfile = InputProfile.Touch;
        using (doc.RenderToImage(W, H)) { }

        Assert.True(Shown(doc, "touch-only"));
        Assert.False(Shown(doc, "mouse-only"));
    }
}
