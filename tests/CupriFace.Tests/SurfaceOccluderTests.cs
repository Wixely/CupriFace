using CupriFace.Components;
using CupriFace.Interaction;
using CupriFace.Paint;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Issue #305. A host-composited surface is presented OVER everything the engine paints, so any
/// popup that can open on top of one has to declare itself with <c>data-surface-occluder</c> or it
/// is drawn underneath the surface and is simply not there.
/// </summary>
public class SurfaceOccluderTests
{
    [Fact]
    public void A_videos_track_menu_hides_the_surface_while_it_is_open_and_restores_it_after()
    {
        using var t = new TestDoc(
            "<body><cupri-video src='clip.mkv' controls tracks style='width:320px;height:180px'></cupri-video></body>",
            "", components: true);
        t.Layout();
        var video = t.Find(n => n.Element?.HasAttribute("data-cupri-video") == true)!;
        var (vx, vy, vw, vh) = HitTesting.ScreenBox(video);

        Assert.False(HostSurfaceGeometry.IsOccluded(t.Doc.Root, vx, vy, vw, vh));   // closed: nothing over it

        var (cx, cy) = (vx + vw / 2, vy + vh / 2);
        t.Doc.DispatchContextMenu(cx, cy);
        t.Layout();
        Assert.True(HostSurfaceGeometry.IsOccluded(t.Doc.Root, vx, vy, vw, vh),
                    "the open track menu must make the video stand down");

        // Dismissed with a click OUTSIDE it — clicking where it was opened lands on the menu
        // itself and chooses an item instead.
        t.Doc.DispatchClick(2, 2);
        t.Layout();
        Assert.False(HostSurfaceGeometry.IsOccluded(t.Doc.Root, vx, vy, vw, vh),
                     "the video must come back once the menu is gone");
    }

    [Fact]
    public void Every_context_menu_the_toolbox_builds_declares_itself_an_occluder()
    {
        // The class of bug, not just the instance: VideoComponent hand-rolled its own menu markup
        // and missed the marker that PopupControls sets. Anything that paints a .cupri-ctx-menu
        // over a host-composited surface has the same obligation.
        const string html = """
            <body>
              <cupri-video src='clip.mkv' controls tracks style='width:200px;height:120px'></cupri-video>
              <div data-cupri-ctx-host>
                <cupri-menu-item>Copy</cupri-menu-item>
              </div>
            </body>
            """;
        using var t = new TestDoc(html, "", components: true);
        t.Layout();

        var menus = new List<Dom.RenderNode>();
        Collect(t.Doc.Root);
        void Collect(Dom.RenderNode n)
        {
            if (n.Element?.ClassList.Contains("cupri-ctx-menu") == true) menus.Add(n);
            foreach (var c in n.Children) Collect(c);
        }

        Assert.NotEmpty(menus);
        foreach (var menu in menus)
            Assert.True(menu.Element!.HasAttribute("data-surface-occluder"),
                        $"a .cupri-ctx-menu inside <{menu.Parent?.Tag}> is missing data-surface-occluder");
    }
}
