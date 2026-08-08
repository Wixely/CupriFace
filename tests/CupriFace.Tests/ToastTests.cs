using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The engine-owned toast stack (<c>doc.Toast(…)</c>): a toast is injected off-screen, the first Animate
/// flips it on-screen (the transition slides it in), it sits for a few seconds, then flips off-screen and
/// is removed. While any toast is live the document reports active animation so the host keeps ticking.
/// </summary>
public class ToastTests
{
    private static int Toasts(TestDoc t)
    {
        var c = 0;
        void W(RenderNode n) { if (n.Element?.ClassList.Contains("cupri-toast-item") == true) c++; foreach (var k in n.Children) W(k); }
        W(t.Root);
        return c;
    }
    private static string Style(TestDoc t) =>
        t.Find(n => n.Element?.ClassList.Contains("cupri-toast-item") == true)?.Element?.GetAttribute("style") ?? "";

    [Fact]
    public void A_toast_slides_in_waits_then_auto_dismisses()
    {
        using var t = new TestDoc("<body></body>", "", components: true, width: 320, height: 240);
        Assert.Equal(0, Toasts(t));

        t.Doc.Toast("Saved");
        t.Layout();
        Assert.Equal(1, Toasts(t));
        Assert.Contains("opacity:0", Style(t));           // injected off-screen (Entering)
        Assert.True(t.Doc.HasActiveAnimations);           // host keeps ticking

        t.Doc.Animate(0.05); t.Layout();
        Assert.Contains("opacity:1", Style(t));           // flipped on-screen → the transition slides it in

        t.Doc.Animate(4.0); t.Layout();
        Assert.Contains("opacity:0", Style(t));           // sat its time → flipped off-screen (Leaving)

        t.Doc.Animate(4.5); t.Layout();
        Assert.Equal(0, Toasts(t));                       // exit slide finished → removed
        Assert.False(t.Doc.HasActiveAnimations);          // nothing left animating
    }

    [Fact]
    public void Multiple_toasts_stack()
    {
        using var t = new TestDoc("<body></body>", "", components: true, width: 320, height: 300);
        t.Doc.Toast("First");
        t.Doc.Toast("Second", "success");
        t.Layout();
        Assert.Equal(2, Toasts(t));                        // both queued in the stack
    }
}
