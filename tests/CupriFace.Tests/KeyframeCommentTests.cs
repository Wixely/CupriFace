using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Comments inside <c>@keyframes</c>, and the timing function an animation was given (#184).
///
/// <para>The keyframes parser worked on the RAW stylesheet while every other parser got a copy with
/// the comments stripped. So a <c>/* comment */</c> between two stops was swallowed into the next
/// stop's selector — <c>"/* one */ 60%"</c> parses as no percentage at all — and that stop was
/// silently dropped. What was left was then interpolated across, and extrapolated past: with two
/// comments, a bar declared to finish at 545px settled at 714px and held there, 31% past a stop it
/// could see.</para>
///
/// <para>Nothing caught it: no exception, no diagnostic, a clean doctor report, and an animation
/// that played smoothly to a wrong number. Comments are legal anywhere, and the stops of a keyframes
/// block are exactly where an author wants to write them.</para>
/// </summary>
public class KeyframeCommentTests(ITestOutputHelper output)
{
    private const string Stops = "0% { width:0; } 35% { width:250px; } 60% { width:300px; } 100% { width:545px; }";

    private static float WidthAt(string keyframesBody, double t, string timing = "linear")
    {
        using var doc = CupriDocument.Load(
            "<body><div class='a'></div></body>",
            "body{margin:0;font-family:sans-serif} .a{height:20px;width:0;background:#d9642a;"
            + $"animation: k 3.6s {timing} 0.35s both}}"
            + " @keyframes k { " + keyframesBody + " }");
        doc.Refresh();
        using (doc.RenderToImage(900, 200)) { }
        doc.Animate(t);
        using (doc.RenderToImage(900, 200)) { }

        RenderNode? Find(RenderNode n)
        {
            if (n.Element?.ClassList.Contains("a") == true) return n;
            foreach (var c in n.Children) { var f = Find(c); if (f is not null) return f; }
            return null;
        }
        return Find(doc.Root)!.Width;
    }

    /// <summary>The reported case: two comments, and the animation settles 31% past its own last
    /// stop and holds there. 714.3 is 250/0.35 — the first interval extrapolated all the way out,
    /// which is what you get when everything after 35% has been dropped.</summary>
    [Fact]
    public void Two_comments_do_not_change_where_the_animation_ends()
    {
        var commented = WidthAt("0% { width:0; } 35% { width:250px; } /* one */ 60% { width:300px; } /* two */ 100% { width:545px; }", 6.0);
        var plain = WidthAt(Stops, 6.0);

        output.WriteLine($"commented {commented:0.0}   plain {plain:0.0}");
        Assert.Equal(545f, plain, 1);
        Assert.Equal(545f, commented, 1);
    }

    /// <summary>One comment was enough to disturb the middle of the curve while still landing in the
    /// right place, which is the version that would survive a glance at the finished animation.</summary>
    [Fact]
    public void One_comment_does_not_change_the_middle_of_the_curve()
    {
        var commented = WidthAt("0% { width:0; } 35% { width:250px; } /* one */ 60% { width:300px; } 100% { width:545px; }", 2.5);
        var plain = WidthAt(Stops, 2.5);

        output.WriteLine($"at t=2.5: commented {commented:0.0}   plain {plain:0.0}");
        Assert.Equal(plain, commented, 1);
    }

    /// <summary>Comments in every position an author might put them, including before the first stop
    /// and after the last.</summary>
    [Theory]
    [InlineData("/* lead */ 0% { width:0; } 35% { width:250px; } 60% { width:300px; } 100% { width:545px; }")]
    [InlineData("0% { width:0; } /* a */ /* b */ 35% { width:250px; } 60% { width:300px; } 100% { width:545px; }")]
    [InlineData("0% { width:0; } 35% { width:250px; } 60% { width:300px; } 100% { width:545px; } /* trail */")]
    [InlineData("0% { /* inside a stop */ width:0; } 35% { width:250px; } 60% { width:300px; } 100% { width:545px; }")]
    [InlineData("0% { width:0; }\n  /* on its own line */\n  35% { width:250px; } 60% { width:300px; } 100% { width:545px; }")]
    public void A_comment_anywhere_leaves_the_animation_alone(string body)
    {
        Assert.Equal(WidthAt(Stops, 2.5), WidthAt(body, 2.5), 1);
        Assert.Equal(545f, WidthAt(body, 6.0), 1);
    }

    /// <summary>Nothing is ever animated to a value outside the stops. The extrapolation was only
    /// reachable because comments had dropped the stops in between, but going past the last
    /// keyframe is wrong however it is reached — so it is pinned directly, with a keyframes block
    /// that stops well before the end.</summary>
    [Fact]
    public void An_animation_never_overshoots_its_last_stop()
    {
        var w = WidthAt("0% { width:0; } 35% { width:250px; }", 6.0);
        output.WriteLine($"settled at {w:0.0}");
        Assert.Equal(250f, w, 1);
    }

    // ---- the timing function, noticed while isolating the above ----------------------------------

    /// <summary>`ease-out` and `linear` used to produce identical values at every sample, because
    /// every timing keyword was matched and discarded. An eased animation is ahead of a linear one
    /// in its first half — that is what "ease-out" means.</summary>
    [Fact]
    public void An_eased_animation_differs_from_a_linear_one()
    {
        var eased = WidthAt(Stops, 1.0, "ease-out");
        var linear = WidthAt(Stops, 1.0, "linear");

        output.WriteLine($"at t=1.0: ease-out {eased:0.0}   linear {linear:0.0}");
        Assert.True(eased > linear + 1f, $"ease-out should be ahead early: {eased:0.0} vs {linear:0.0}");
    }

    /// <summary>…and they still agree at both ends, which is what says the curve was applied rather
    /// than the values shifted.</summary>
    [Theory]
    [InlineData(0.35)]
    [InlineData(6.0)]
    public void Every_timing_function_agrees_at_the_ends(double t)
        => Assert.Equal(WidthAt(Stops, t, "linear"), WidthAt(Stops, t, "ease-in-out"), 1);

    /// <summary>The longhand works too, and an unreadable one is reported rather than ignored.</summary>
    [Fact]
    public void The_longhand_timing_function_is_honoured()
    {
        using var doc = CupriDocument.Load(
            "<body><div class='a'></div></body>",
            "body{margin:0} .a{height:20px;width:0;animation-name:k;animation-duration:3.6s;"
            + "animation-timing-function:ease-out;animation-fill-mode:both}"
            + " @keyframes k { 0% { width:0; } 100% { width:545px; } }");
        doc.Refresh();
        using (doc.RenderToImage(900, 200)) { }
        doc.Animate(0.9);
        using (doc.RenderToImage(900, 200)) { }

        RenderNode? Find(RenderNode n)
        {
            if (n.Element?.ClassList.Contains("a") == true) return n;
            foreach (var c in n.Children) { var f = Find(c); if (f is not null) return f; }
            return null;
        }
        var eased = Find(doc.Root)!.Width;
        var quarter = 545f * 0.25f;
        output.WriteLine($"a quarter of the way through: {eased:0.0} (linear would be {quarter:0.0})");
        Assert.True(eased > quarter + 1f);
    }
}
