using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

/// <summary>@media (max-width/min-width) re-resolves on viewport change — the mechanism behind the
/// showcase sidebar auto-collapsing to its icon rail on a narrow window.</summary>
public class MediaQueryTests
{
    private static RenderNode Find(RenderNode n, string cls)
    {
        if (n.Element?.ClassList.Contains(cls) == true) return n;
        foreach (var c in n.Children) { var f = Find(c, cls); if (f is not null) return f; }
        return null!;
    }

    [Fact]
    public void An_element_collapses_below_a_max_width_breakpoint_and_restores_above_it()
    {
        const string css = "body{margin:0} .bar{width:190px;height:50px} @media (max-width:600px){ .bar{width:64px} }";
        using var doc = CupriDocument.Load("<body><div class='bar'>x</div></body>", css);

        doc.BuildFrame(900, 400);
        Assert.Equal(190f, Find(doc.Root, "bar").Width, 1);   // wide window → full width

        doc.BuildFrame(500, 400);                             // cross the breakpoint → styles re-resolve
        Assert.Equal(64f, Find(doc.Root, "bar").Width, 1);    // narrow → collapsed

        doc.BuildFrame(900, 400);                             // …and back when it widens again
        Assert.Equal(190f, Find(doc.Root, "bar").Width, 1);
    }

    [Fact]
    public void A_height_qualified_query_tells_phone_landscape_from_a_desktop_window()
    {
        // Same wide viewport, different heights: only the tall (desktop-shaped) one may match.
        const string css = "body{margin:0} .page{width:800px;height:50px}"
                         + " @media (min-width:700px) and (min-height:600px){ .page{width:560px} }";
        using var doc = CupriDocument.Load("<body><div class='page'>x</div></body>", css);

        doc.BuildFrame(850, 312);                             // phone landscape: wide but short
        Assert.Equal(800f, Find(doc.Root, "page").Width, 1);  // → the cap must NOT fire

        doc.BuildFrame(850, 700);                             // desktop window: wide AND tall
        Assert.Equal(560f, Find(doc.Root, "page").Width, 1);  // → the cap fires

        doc.BuildFrame(850, 312);                             // rotate back → re-resolves on height alone
        Assert.Equal(800f, Find(doc.Root, "page").Width, 1);
    }

    // ---- the size an app can ASK about, so it agrees with the cascade (#155) ---------------------

    /// <summary>
    /// A responsive control with three states — auto, forced open, forced closed — has to know what
    /// is on SCREEN, not what its flag says: below a breakpoint the sidebar may already be a rail
    /// while the flag still reads expanded. Before this existed the only way to find out was to
    /// record the width in <c>Present()</c> every frame, which the engine's own Showcase did.
    /// </summary>
    [Fact]
    public void A_document_reports_the_viewport_it_was_laid_out_in()
    {
        using var doc = CupriDocument.Load("<body><div class='bar'>x</div></body>", "body{margin:0}");
        // Nothing measured yet: zero, rather than a default an app could not tell from a measurement.
        Assert.Equal(0f, doc.ViewportWidth);

        doc.BuildFrame(900, 400);
        Assert.Equal(900f, doc.ViewportWidth, 1);
        Assert.Equal(400f, doc.ViewportHeight, 1);

        doc.BuildFrame(500, 300);
        Assert.Equal(500f, doc.ViewportWidth, 1);
        Assert.Equal(300f, doc.ViewportHeight, 1);
    }

    /// <summary>And it is the POST-ZOOM size — the one the stylesheet was resolved against. An app
    /// comparing the host's logical width to its own breakpoints disagrees with the cascade the
    /// moment zoom is not 1, and the disagreement is invisible until someone zooms.</summary>
    [Fact]
    public void The_reported_viewport_is_the_one_the_breakpoints_were_tested_against()
    {
        const string css = "body{margin:0} .bar{width:190px;height:50px} @media (max-width:600px){ .bar{width:64px} }";
        using var doc = CupriDocument.Load("<body><div class='bar'>x</div></body>", css);

        doc.Zoom = 2f;                       // layout happens at viewport/Zoom, like a browser's page zoom
        doc.BuildFrame(900, 400);

        Assert.Equal(450f, doc.ViewportWidth, 1);              // 900 / 2, not 900
        // …which is below the breakpoint, so the rule fired — the two answers cannot disagree.
        Assert.Equal(64f, Find(doc.Root, "bar").Width, 1);
        Assert.True(doc.ViewportWidth <= 600f);
    }
}
