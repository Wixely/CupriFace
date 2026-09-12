using CupriFace.Binding;
using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// What the animation clock means for an element that was not there when the clock started.
///
/// <para><b>Why this is pinned.</b> <c>Animate(t)</c> being a parameter rather than a wall clock is
/// what makes frame-by-frame rendering possible at all (experiments/CUPRICUT.md). A renderer puts an
/// element on stage at its start time and expects its entry animation to play — so the question of
/// whether that animation runs from the composition's zero or the element's own is load-bearing for
/// anything built on top, and it is not obvious from the code either way.</para>
///
/// <para><b>The answer: the clock is absolute.</b> An element created at t = 2 with a 1s animation
/// is already past the end of it, so it renders its final frame and never plays. That is correct CSS
/// — the animation's timeline is the document's — and it means a timeline layer must say when each
/// element's animation begins. <c>animation-delay</c> is exactly that statement, and it needs no
/// engine change: stamp an element placed at t = 2 with <c>animation-delay: 2s</c> and it animates
/// from its own zero.</para>
///
/// <para>Measured on layout rather than pixels: the animation drives width, so the node's width is
/// the animation's progress as a number. <c>forwards</c> is on the keyframes for the same reason —
/// without it a finished animation reverts to <c>width:auto</c>, which reads identically to "the
/// animation never applied", and the two cases have to be told apart.</para>
/// </summary>
public class AnimationClockTests
{
    private const string Css = """
        body { margin: 0 }
        @keyframes grow { from { width: 0px } to { width: 100px } }
        .item { height: 10px; background: #000; animation: grow 1s forwards; }
        """;

    private const string Html = """
        <body><div data-repeat="Rows"><div class="item">x</div></div></body>
        """;

    [CupriBindable]
    public sealed partial class Model
    {
        public List<string> Rows { get; set; } = [];
    }

    private static float? Width(CupriDocument doc)
    {
        RenderNode? found = null;
        void Walk(RenderNode n)
        {
            if (n.Element?.ClassList.Contains("item") == true) found ??= n;
            foreach (var c in n.Children) Walk(c);
        }
        Walk(doc.Root);
        return found?.Width;
    }

    private static float? At(CupriDocument doc, double t)
    {
        doc.Animate(t);
        using (doc.RenderToImage(400, 200)) { }
        return Width(doc);
    }

    /// <summary>The baseline: an element present from the start animates across the clock.</summary>
    [Fact]
    public void AnElementPresentFromTheStartAnimatesAcrossTheClock()
    {
        var model = new Model { Rows = { "a" } };
        using var doc = CupriDocument.Load(Html, Css);
        doc.Bind(model);

        Assert.Equal(0f, At(doc, 0.0)!.Value, 1);
        Assert.Equal(50f, At(doc, 0.5)!.Value, 1);
        Assert.Equal(100f, At(doc, 1.5)!.Value, 1);      // finished, held by `forwards`
    }

    /// <summary>
    /// The one that decides a renderer's design: an element created LATE gets no entry animation,
    /// because the clock it animates against is the document's and that clock is already past the
    /// end. Not a defect — it is what CSS says — but it is the thing a timeline layer must handle.
    /// </summary>
    [Fact]
    public void AnElementCreatedLateHasAlreadyMissedItsAnimation()
    {
        var model = new Model();
        using var doc = CupriDocument.Load(Html, Css);
        doc.Bind(model);

        Assert.Null(At(doc, 0.0));                        // nothing on stage yet

        model.Rows.Add("a");
        doc.Refresh();

        // Appears at its END state and stays there — the 1s animation elapsed between t=0 and t=2.
        Assert.Equal(100f, At(doc, 2.0)!.Value, 1);
        Assert.Equal(100f, At(doc, 2.5)!.Value, 1);
    }

    /// <summary>
    /// And the fix, from outside the engine: an <c>animation-delay</c> equal to the element's start
    /// time restores its own zero exactly. This is why the absolute clock needs no engine change —
    /// a timeline layer stamps one inline style as it places each element.
    /// </summary>
    [Fact]
    public void ADelayEqualToTheStartTimeRestoresTheElementsOwnZero()
    {
        const string html = """
            <body><div data-repeat="Rows">
              <div class="item" style="animation-delay: 2s">x</div>
            </div></body>
            """;
        var model = new Model();
        using var doc = CupriDocument.Load(html, Css);
        doc.Bind(model);

        At(doc, 0.0);
        model.Rows.Add("a");
        doc.Refresh();

        Assert.Equal(0f, At(doc, 2.00)!.Value, 1);        // its own zero, at the composition's t=2
        Assert.Equal(25f, At(doc, 2.25)!.Value, 1);
        Assert.Equal(50f, At(doc, 2.50)!.Value, 1);
        Assert.Equal(75f, At(doc, 2.75)!.Value, 1);
        Assert.Equal(100f, At(doc, 3.20)!.Value, 1);      // finished, one second after it appeared
    }

    /// <summary>Seeking is order-independent, which is the property the whole frame-renderer idea
    /// rests on: the frame at a given t is the same whether you arrived forwards, backwards, or by
    /// jumping. Asserted on the delayed element, since that is the arrangement a timeline produces.</summary>
    [Fact]
    public void SeekingBackwardsGivesTheSameFrameAsSeekingForwards()
    {
        const string html = """
            <body><div data-repeat="Rows">
              <div class="item" style="animation-delay: 2s">x</div>
            </div></body>
            """;
        var model = new Model();
        using var doc = CupriDocument.Load(html, Css);
        doc.Bind(model);
        model.Rows.Add("a");
        doc.Refresh();

        var forwards = At(doc, 2.5);
        At(doc, 3.9);                                     // seek away, past the end
        var backwards = At(doc, 2.5);                     // and back

        Assert.Equal(forwards!.Value, backwards!.Value, 3);
    }
}
