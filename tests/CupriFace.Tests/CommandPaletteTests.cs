using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>&lt;cupri-command-palette&gt;</c>: opens with the search auto-focused, filters commands as you type,
/// runs the highlighted (↓ then Enter) or clicked command — navigating via its data-set-path and closing
/// the palette — and dismisses on Escape.
/// </summary>
public class CommandPaletteTests
{
    private sealed class Model
    {
        public bool PaletteOpen { get; set; } = true;
        public string PaletteQuery { get; set; } = "";
        public string Section { get; set; } = "home";
    }

    private const string Html =
        "<body><cupri-command-palette open=\"{{PaletteOpen}}\" value=\"{{PaletteQuery}}\">" +
          "<cupri-command data-set-path=\"Section\" data-set-value=\"charts\" icon=\"bar-chart\">Go to Charts</cupri-command>" +
          "<cupri-command data-set-path=\"Section\" data-set-value=\"images\" icon=\"image\">Go to Images</cupri-command>" +
          "<cupri-command data-set-path=\"Section\" data-set-value=\"settings\">Open Settings</cupri-command>" +
        "</cupri-command-palette></body>";

    private static int Rows(TestDoc t)
    {
        var c = 0;
        void W(RenderNode n) { if (n.Element?.ClassList.Contains("cupri-cmdp-row") == true) c++; foreach (var k in n.Children) W(k); }
        W(t.Root);
        return c;
    }
    private static bool PanelShown(TestDoc t) => t.Find(n => n.Element?.ClassList.Contains("cupri-cmdp-panel") == true) is not null;

    [Fact]
    public void Opens_with_the_search_autofocused_and_all_commands_listed()
    {
        using var t = new TestDoc(Html, "", new Model(), width: 640, height: 480, components: true);
        Assert.True(PanelShown(t));
        Assert.True(t.FindClass("cupri-cmdp-input").Element!.HasAttribute("data-focus")); // caret is in the search box
        Assert.Equal(3, Rows(t));
    }

    [Fact]
    public void A_ctrl_chord_shortcut_opens_the_palette_immediately()
    {
        var m = new Model { PaletteOpen = false };
        using var t = new TestDoc(Html, "", m, width: 640, height: 480, components: true);
        t.Doc.OnShortcut(KeyMods.Ctrl, "k", () => { m.PaletteQuery = ""; m.PaletteOpen = true; });
        Assert.False(PanelShown(t));

        // The chord alone must surface the palette. The old code fired the handler but skipped the
        // rebuild, so the panel only appeared on the NEXT event's ReconcileScope — desktop's constant
        // mouse-moves hid that; the web host (chord → nothing else) showed a dead Ctrl+K.
        Assert.True(t.Doc.DispatchKey("k", EditKey.None, KeyMods.Ctrl));
        t.Layout();
        Assert.True(PanelShown(t));

        // And the search box was focused by the same dispatch — typing filters with no click first.
        t.Type("sett");
        Assert.Equal("sett", m.PaletteQuery);
        Assert.Equal(1, Rows(t));
    }

    [Fact]
    public void Typing_filters_the_commands_live()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, width: 640, height: 480, components: true);
        t.Type("sett");
        Assert.Equal("sett", m.PaletteQuery);   // committed live
        Assert.Equal(1, Rows(t));               // only "Open Settings" matches
    }

    [Fact]
    public void Down_then_Enter_runs_the_highlighted_command_and_closes()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, width: 640, height: 480, components: true);
        t.Key(EditKey.Down);                     // highlight the first command
        t.Key(EditKey.Enter);                    // run it
        Assert.Equal("charts", m.Section);       // navigated via its data-set-path
        Assert.False(m.PaletteOpen);             // and the palette closed
    }

    [Fact]
    public void Clicking_a_command_runs_it_and_closes()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, width: 640, height: 480, components: true);
        var images = t.Find(n => n.Element?.ClassList.Contains("cupri-cmdp-row") == true
                                 && n.Element.TextContent.Contains("Images"))!;
        t.ClickNode(images);
        Assert.Equal("images", m.Section);
        Assert.False(m.PaletteOpen);
    }

    [Fact]
    public void Escape_dismisses_the_palette()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, width: 640, height: 480, components: true);
        t.Key(EditKey.Escape);
        Assert.False(PanelShown(t));
    }
}
