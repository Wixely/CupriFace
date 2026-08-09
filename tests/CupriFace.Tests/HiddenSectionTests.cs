using Xunit;

namespace CupriFace.Tests;

/// <summary>Components inside an inline <c>display:none</c> subtree (a switched-off section) are not
/// expanded — they can't render, and skipping them keeps the per-keystroke rebuild cheap. The rebuild
/// that reveals the subtree expands them.</summary>
public class HiddenSectionTests
{
    private sealed class Model { public string Show { get; set; } = "none"; }

    private const string Html =
        "<body><div style=\"display:{{Show}}\">" +
        "<cupri-switch checked=\"true\"></cupri-switch>" +
        "<cupri-bar-chart values=\"1,2,3\"></cupri-bar-chart>" +
        "</div></body>";

    [Fact]
    public void Hidden_components_do_not_expand_but_do_when_revealed()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 400, height: 300);

        // Hidden: the subtree is pruned from the render tree (no expanded internals anywhere).
        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-switch") == true));
        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-bc-bar") == true));

        // Reveal → the rebuild expands the section's components and they render.
        m.Show = "block";
        t.Doc.Refresh();
        t.Layout();
        var sw = t.Find(n => n.Element?.ClassList.Contains("cupri-switch") == true);
        Assert.NotNull(sw);
        Assert.True(sw!.Width > 0);                                       // laid out, not just present
        Assert.NotNull(t.Find(n => n.Element?.ClassList.Contains("cupri-bc-bar") == true));

        // And hiding again prunes it once more.
        m.Show = "none";
        t.Doc.Refresh();
        t.Layout();
        Assert.Null(t.Find(n => n.Element?.ClassList.Contains("cupri-switch") == true));
    }
}
