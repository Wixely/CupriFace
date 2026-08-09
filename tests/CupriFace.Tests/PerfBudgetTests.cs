using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using CupriFace;
using CupriFace.Components;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// Performance budgets that fail the build (DESIGN risk #0: sustained fluidity is a requirement, not a
/// demo target). CI runs these on a shared runner whose absolute speed is unknown and variable, so the
/// gates that matter are <b>ratios measured inside one process</b> — each compares a document against a
/// variant of itself, so machine speed cancels out. Each one encodes an invariant a real regression
/// would break; the comment on each says which.
///
/// A wall-clock backstop is included too, but deliberately loose: it only catches something
/// catastrophic, because a tight absolute number on a noisy runner is a flaky test, and a flaky gate
/// gets disabled — at which point it protects nothing.
/// </summary>
public class PerfBudgetTests
{
    private readonly ITestOutputHelper _out;
    public PerfBudgetTests(ITestOutputHelper o) => _out = o;

    // ---- measurement -------------------------------------------------------

    /// <summary>Median ms per call. Median (not mean) so one GC pause or a busy neighbouring core
    /// doesn't decide the result.</summary>
    private static double Median(Action action, int iters)
    {
        var samples = new double[iters];
        for (var i = 0; i < iters; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples[iters / 2];
    }

    /// <summary>Time two workloads <b>interleaved</b> (a, b, a, b, …) and return their medians. Sampling
    /// them alternately means CPU contention, thermal drift and JIT warm-up hit both sides alike, so the
    /// ratio between them stays meaningful on hardware we don't control.</summary>
    private static (double A, double B) Race(Action a, Action b, int iters = 21)
    {
        for (var i = 0; i < 3; i++) { a(); b(); }           // warm up both (JIT, caches)
        var sa = new List<double>(iters);
        var sb = new List<double>(iters);
        for (var i = 0; i < iters; i++)
        {
            var t = Stopwatch.StartNew(); a(); sa.Add(t.Elapsed.TotalMilliseconds);
            t = Stopwatch.StartNew(); b(); sb.Add(t.Elapsed.TotalMilliseconds);
        }
        sa.Sort(); sb.Sort();
        return (sa[iters / 2], sb[iters / 2]);
    }

    // ---- documents ---------------------------------------------------------

    // A page with enough real controls to be representative of an app screen.
    private static string Panel(int controls)
    {
        var sb = new StringBuilder("<div class='panel'>");
        for (var i = 0; i < controls; i++)
            sb.Append($"<div class='row r{i}'><span class='lbl'>Field {i}</span>")
              .Append($"<cupri-textfield value='v{i}'></cupri-textfield>")
              .Append("<cupri-switch checked='true'></cupri-switch>")
              .Append($"<cupri-badge>#{i}</cupri-badge></div>");
        return sb.Append("</div>").ToString();
    }

    private const string BaseCss = """
        .panel { display:block; padding:12px; }
        .row { display:flex; align-items:center; gap:10px; margin-bottom:8px; }
        .lbl { width:90px; color:#4a5262; }
        """;

    // Rules for components that aren't on this page — the shape a component library's CSS actually has
    // (class-keyed: `.cupri-button`, `.cupri-table .cupri-row`, …), which is the case bucketing exists
    // to make free. NOTE: a selector ending in a *tag* (`… > span`) buckets under that tag and is
    // legitimately tested against every span on the page; that is correct, so it isn't asserted here.
    private static string UnusedRules(int n)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < n; i++)
            sb.Append($".absent-{i} .nested-{i} > .leaf-{i} {{ color:#123456; padding:{i % 7}px; }}\n")
              .Append($".widget-{i}[data-hover] .deep-{i} {{ background:#abcdef; }}\n");
        return sb.ToString();
    }

    private static CupriDocument Doc(string html, string css, object? model = null)
    {
        var d = CupriDocument.Load($"<body>{html}</body>", css).UseComponents(ComponentRegistry.Default());
        if (model is not null) d.Bind(model);
        using var _ = d.RenderToImage(900, 700);   // lay out once, like a host's first frame
        return d;
    }

    // ---- gates -------------------------------------------------------------

    [Fact]
    public void Styling_does_not_scale_with_the_size_of_the_stylesheet()
    {
        // INVARIANT: style resolution costs per *element*, not per *rule*. Selectors are compiled once
        // and bucketed by their rightmost compound, so rules that cannot match the page are never tested.
        // Regression this catches: going back to matching every rule against the whole document (which
        // was the single biggest cost in the engine — 38ms of a 68ms rebuild).
        var html = Panel(20);
        using var small = Doc(html, BaseCss);
        using var huge = Doc(html, BaseCss + UnusedRules(400));   // +800 rules, none of them matching

        var (a, b) = Race(() => small.Refresh(), () => huge.Refresh());
        _out.WriteLine($"rebuild: {a:F2} ms with a small sheet, {b:F2} ms with +800 unused rules (x{b / a:F2})");

        Assert.True(b < a * 1.5, $"800 unused CSS rules must not meaningfully slow a rebuild (x{b / a:F2})");
    }

    [Fact]
    public void Rebuilding_does_not_scale_with_hidden_content()
    {
        // INVARIANT: components inside a display:none subtree are not expanded — a switched-off section
        // costs (almost) nothing. Regression this catches: expanding every component in the document on
        // every keystroke, which is what a tabbed app spends its time on (9 of 10 sections are hidden).
        var visible = Panel(20);
        var hidden = string.Concat(Enumerable.Range(0, 6).Select(i => $"<div style='display:none'>{Panel(20)}</div>"));

        using var lean = Doc(visible, BaseCss);
        using var withHidden = Doc(visible + hidden, BaseCss);     // 6x more components, all hidden

        var (a, b) = Race(() => lean.Refresh(), () => withHidden.Refresh());
        _out.WriteLine($"rebuild: {a:F2} ms visible-only, {b:F2} ms with 6 hidden panels (x{b / a:F2})");

        Assert.True(b < a * 3.0, $"hidden sections must stay nearly free to rebuild (x{b / a:F2})");
    }

    [Fact]
    public void Hovering_does_not_scale_with_the_size_of_the_stylesheet()
    {
        // INVARIANT: the selector index is built once per stylesheet, not per restyle. A pointer move
        // restyles, and re-bucketing every rule there made the cost of moving the mouse grow with the
        // size of the app's CSS — 2000 rules tripled it. This is the hottest path in the engine.
        const string rows = "<div class='panel'><div class='row a'><span class='lbl'>A</span></div>" +
                            "<div class='row b'><span class='lbl'>B</span></div></div>";
        var hoverCss = BaseCss + ".row[data-hover] { background:#eef1f5; }";

        using var small = Doc(rows, hoverCss);
        using var huge = Doc(rows, hoverCss + UnusedRules(1000));   // +2000 rules, none matching

        static Action Hoverer(CupriDocument d)
        {
            var pts = new List<(float X, float Y)>();
            void W(CupriFace.Dom.RenderNode n)
            {
                if (n.Element?.ClassList.Contains("row") == true) pts.Add(TestDoc.Center(n));
                foreach (var c in n.Children) W(c);
            }
            W(d.Root);
            var i = 0;
            return () => { var p = pts[i++ % pts.Count]; d.DispatchPointerMove(p.X, p.Y); };
        }

        var (a, b) = Race(Hoverer(small), Hoverer(huge), iters: 41);
        _out.WriteLine($"hover: {a:F3} ms with a small sheet, {b:F3} ms with +2000 unused rules (x{b / a:F2})");

        Assert.True(b < a * 2.0, $"stylesheet size must not drive the cost of a mouse move (x{b / a:F2})");
    }

    [Fact]
    public void A_hover_restyle_is_far_cheaper_than_a_full_rebuild()
    {
        // INVARIANT: pointer movement re-resolves styles only — it must never pay for parse + bind +
        // component expansion. This is the difference between a UI that tracks the mouse and one that
        // stutters, and it runs on literally every mouse move.
        using var doc = Doc(Panel(24), BaseCss + ".row[data-hover] { background:#eef1f5; }");

        var rows = new List<(float X, float Y)>();
        void Collect(CupriFace.Dom.RenderNode n)
        {
            if (n.Element?.ClassList.Contains("row") == true) rows.Add(TestDoc.Center(n));
            foreach (var c in n.Children) Collect(c);
        }
        Collect(doc.Root);
        Assert.True(rows.Count >= 4, "need a few rows to hover between");

        var i = 0;
        var (rebuild, hover) = Race(
            () => doc.Refresh(),
            () => { var p = rows[i++ % rows.Count]; doc.DispatchPointerMove(p.X, p.Y); });
        _out.WriteLine($"rebuild {rebuild:F2} ms vs hover move {hover:F2} ms (hover is {hover / rebuild:P0} of a rebuild)");

        Assert.True(hover < rebuild, $"a hover ({hover:F2} ms) must cost less than a full rebuild ({rebuild:F2} ms)");
    }

    [Fact]
    public void An_unchanged_frame_costs_nothing_to_present()
    {
        // INVARIANT: with a retained canvas, an identical frame is detected and skipped entirely — the
        // basis of render-on-demand in both hosts (an idle window must not burn a core).
        using var doc = Doc(Panel(20), BaseCss);
        using var bmp = new SKBitmap(new SKImageInfo(900, 700, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);

        Assert.NotNull(doc.RenderIncremental(canvas, 900, 700, SKColors.White));   // first frame: full
        for (var i = 0; i < 5; i++)
            Assert.Null(doc.RenderIncremental(canvas, 900, 700, SKColors.White));  // then: nothing to do
    }

    [Fact]
    public void A_pointer_move_within_one_element_does_no_work()
    {
        // INVARIANT: hover is diffed against the current chain, so sliding the pointer across a single
        // control doesn't restyle. Regression this catches: reporting "changed" on every mouse move,
        // which repaints the whole window at pointer frequency.
        using var doc = Doc(Panel(6), BaseCss);
        var target = TestDoc.Find(doc.Root, n => n.Element?.ClassList.Contains("lbl") == true)!;
        var (x, y) = TestDoc.Center(target);

        doc.DispatchPointerMove(x, y);                       // settle onto it (this one may restyle)
        for (var d = 1; d <= 3; d++)
            Assert.False(doc.DispatchPointerMove(x + d, y), "moving within the same element must be a no-op");
    }

    [Fact]
    public void An_interaction_stays_within_a_loose_wall_clock_budget()
    {
        // Coarse backstop for a catastrophic regression (something turning linear work quadratic).
        // Deliberately generous: CI hardware is unknown and shared, and a flaky gate is worse than a
        // loose one. On a 2026 dev machine this is ~15 ms; the budget is over an order of magnitude up.
        using var doc = Doc(Panel(24), BaseCss + UnusedRules(120));
        using var bmp = new SKBitmap(new SKImageInfo(900, 700, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);

        for (var i = 0; i < 3; i++) { doc.Refresh(); doc.Render(canvas, 900, 700); }   // warm up

        var interaction = Median(() => { doc.Refresh(); doc.Render(canvas, 900, 700); }, 15);
        _out.WriteLine($"rebuild + full render: {interaction:F2} ms");

        Assert.True(interaction < 400, $"a full interaction took {interaction:F1} ms (budget 400 ms)");
    }
}
