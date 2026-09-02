using CupriFace;
using CupriFace.Components;
using CupriFace.Lottie;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The optional Lottie package, end to end.
///
/// The claim worth testing is not "Skottie parses JSON" — that is Skia's job and Skia's tests. It is
/// that an animation reaches the ENGINE's paint output: that <c>&lt;cupri-lottie&gt;</c> becomes a live
/// surface, that a player is opened for it and retired with it, and that pixels actually land. A
/// component that expands correctly and paints nothing is the failure this package could plausibly
/// have, and nothing about the markup would show it.
/// </summary>
public class LottieTests(ITestOutputHelper output)
{
    private static byte[] Spinner()
    {
        using var s = typeof(LottieTests).Assembly.GetManifestResourceStream("fixtures.cupri-spinner.json")!;
        var buf = new byte[s.Length];
        s.ReadExactly(buf);
        return buf;
    }

    private const string Html =
        "<body style='margin:0;background:#ffffff'>" +
        "<cupri-lottie src=\"fixtures.cupri-spinner.json\" width=\"120\" height=\"120\"></cupri-lottie>" +
        "</body>";

    private static CupriDocument Doc() =>
        CupriDocument.Load(Html, "")
                     .UseComponents(ComponentRegistry.Default().UseLottie())
                     .UseLottie(typeof(LottieTests).Assembly);

    // ---- the player -------------------------------------------------------

    [Fact]
    public void The_authored_spinner_parses_and_reports_its_own_shape()
    {
        using var p = LottiePlayer.TryCreate(Spinner());
        Assert.NotNull(p);
        output.WriteLine($"duration={p!.Duration}s natural={p.NaturalSize}");
        Assert.Equal(1.5, p.Duration, 2);
        Assert.Equal((120, 120), p.NaturalSize);
    }

    /// <summary>A bad asset must not take the document down with it — one element's authoring mistake
    /// is not grounds for losing the page.</summary>
    [Fact]
    public void Rubbish_json_yields_no_player_rather_than_an_exception()
    {
        Assert.Null(LottiePlayer.TryCreate("not a lottie"u8.ToArray()));
    }

    /// <summary>A paused animation must stop ticking, or a render-on-demand host never goes idle.</summary>
    [Fact]
    public void Only_a_playing_animation_keeps_the_host_awake()
    {
        using var p = LottiePlayer.TryCreate(Spinner(), autoplay: true)!;
        Assert.True(p.Ticking);
        p.Playing = false;
        Assert.False(p.Ticking);
    }

    [Fact]
    public void Advancing_moves_through_the_animation_and_loops()
    {
        using var p = LottiePlayer.TryCreate(Spinner(), loop: true)!;
        p.Advance(0.5);
        Assert.Equal(0.5, p.Position, 2);
        p.Advance(1.4);                       // past the 1.5s end
        output.WriteLine($"after 1.9s of a 1.5s loop: {p.Position:0.##}");
        Assert.True(p.Position < 0.5, $"a looping animation should have wrapped, sat at {p.Position}");
    }

    // ---- the engine seam --------------------------------------------------

    [Fact]
    public void The_element_becomes_a_live_surface_and_a_player_is_opened_for_it()
    {
        using var doc = Doc();
        using (doc.RenderToImage(200, 200)) { }

        var node = TestDoc.Find(doc.Root, n => n.Element?.LocalName == "cupri-lottie");
        Assert.NotNull(node);
        var key = node!.Element!.GetAttribute("data-cupri-surface");
        Assert.Equal("lottie:fixtures.cupri-spinner.json", key);
        Assert.NotNull(doc.Surfaces.Get(key));          // …and UseLottie opened one for it
    }

    /// <summary>The claim that matters: the animation reaches the engine's pixels, AND moves.
    ///
    /// <para>Counting non-background pixels on the ring was the first version of this, and it was
    /// worthless: the spinner has a faint track circle that is present at every instant, so all 120
    /// samples came back covered whether or not a single frame had advanced. Comparing two DIFFERENT
    /// times is what separates "it painted" from "it animated" — and only the second is the feature.</para></summary>
    [Fact]
    public void The_animation_paints_and_the_frame_changes_over_time()
    {
        using var doc = Doc();
        using (doc.RenderToImage(200, 200)) { }           // first frame wires the player
        var player = (LottiePlayer)doc.Surfaces.Get("lottie:fixtures.cupri-spinner.json")!;

        byte[] Frame(double at)
        {
            player.Rewind();
            player.Advance(at);
            using var img = doc.RenderToImage(200, 200, SKColors.White);
            return SKBitmap.FromImage(img).Bytes;
        }

        var early = Frame(0.10);
        var later = Frame(0.55);

        var differing = early.Zip(later).Count(p => Math.Abs(p.First - p.Second) > 8);
        output.WriteLine($"bytes differing between t=0.10 and t=0.55: {differing:n0}");

        // The arc sweeps and grows between those instants, so a large slice of the ring must change.
        // A static image — or a player that never advanced — differs in nothing.
        Assert.True(differing > 500,
            $"the frame should change as the animation runs; only {differing} bytes differed");
    }

    /// <summary>A player is retired when its element goes, or a page that has switched away keeps
    /// burning frames behind a hidden panel.</summary>
    [Fact]
    public void A_player_is_retired_when_its_element_disappears()
    {
        var model = new Holder { Show = "block" };
        using var doc = CupriDocument.Load(
            "<body><cupri-lottie src=\"fixtures.cupri-spinner.json\" " +
            "style=\"display:{{Show}}\" width=\"60\" height=\"60\"></cupri-lottie></body>", "")
            .UseComponents(ComponentRegistry.Default().UseLottie())
            .UseLottie(typeof(LottieTests).Assembly);
        doc.Bind(model);
        using (doc.RenderToImage(200, 200)) { }
        Assert.NotNull(doc.Surfaces.Get("lottie:fixtures.cupri-spinner.json"));

        model.Show = "none";
        doc.Refresh();
        using (doc.RenderToImage(200, 200)) { }

        Assert.Null(doc.Surfaces.Get("lottie:fixtures.cupri-spinner.json"));
    }

    /// <summary>Pausing from the MARKUP has to reach the player. A Pause button binds
    /// autoplay="{{Playing}}", and the player is deliberately kept across rebuilds so an animation does
    /// not restart on every keystroke — so unless the attribute is re-read, the button rewrites the DOM
    /// and nothing else. The bound value is a real bool, as a sample would write it.</summary>
    [Fact]
    public void A_pause_in_the_markup_reaches_the_open_player()
    {
        var model = new Toggle();
        using var doc = CupriDocument.Load(
            "<body><cupri-lottie src=\"fixtures.cupri-spinner.json\" " +
            "autoplay=\"{{Playing}}\" width=\"60\" height=\"60\"></cupri-lottie></body>", "")
            .UseComponents(ComponentRegistry.Default().UseLottie())
            .UseLottie(typeof(LottieTests).Assembly);
        doc.Bind(model);
        using (doc.RenderToImage(200, 200)) { }

        var player = (LottiePlayer)doc.Surfaces.Get("lottie:fixtures.cupri-spinner.json")!;
        Assert.True(player.Playing, "autoplay defaults on");

        // Pin what a bound bool actually RENDERS as, because it is the reason the attribute is read
        // case-insensitively: .NET writes "False", and an ordinal compare against "false" would read
        // a pause as a play. Asserted rather than commented, so a binder that started lower-casing
        // would show up here instead of silently making the check moot.
        string? rendered = null;
        doc.OnRebuilt(dom => rendered = dom.QuerySelector("[data-cupri-surface]")?.GetAttribute("autoplay"));

        model.Playing = false;
        doc.Refresh();
        using (doc.RenderToImage(200, 200)) { }

        output.WriteLine($"a bound bool reaches the attribute as: \"{rendered}\"");
        Assert.Equal("False", rendered);
        Assert.False(player.Playing, "the pause in the markup never reached the player");
        Assert.False(player.Ticking, "a paused player must stop keeping the host awake");
    }

    private sealed class Toggle { public bool Playing { get; set; } = true; }

    private sealed class Holder { public string Show { get; set; } = "block"; }
}
