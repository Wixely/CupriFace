using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

public class ContextMenuTests
{
    private sealed class Model { public string Name { get; set; } = "Ada Lovelace"; }

    private const string Html = "<body><div style='padding:20px'><cupri-textfield value=\"{{Name}}\"></cupri-textfield></div></body>";

    private static TestDoc Make(out System.Collections.Generic.List<ContextCommand> actions)
    {
        var t = new TestDoc(Html, "", new Model(), width: 500, height: 300, components: true);
        var a = new System.Collections.Generic.List<ContextCommand>();
        t.Doc.ContextRequested += a.Add;
        actions = a;
        return t;
    }

    private RenderNode? Menu(TestDoc t) => t.Find(n => n.Element?.HasAttribute("data-ctx-menu") == true);
    private RenderNode? Row(TestDoc t, string cmd) => t.Find(n => n.Element?.GetAttribute("data-cupri-context") == cmd);

    private void OpenMenu(TestDoc t) { var (x, y) = TestDoc.Center(t.FindRole("textbox")); t.Doc.DispatchContextMenu(x, y); t.Layout(); }

    [Fact]
    public void Right_click_opens_menu_with_enable_states()
    {
        using var t = Make(out _);
        OpenMenu(t);
        Assert.NotNull(Menu(t));
        Assert.NotNull(Row(t, "paste"));                 // paste always enabled
        Assert.NotNull(Row(t, "selectall"));             // field has text
        Assert.Null(Row(t, "cut"));                      // no selection on a fresh focus
        Assert.Null(Row(t, "copy"));
    }

    [Fact]
    public void Outside_click_dismisses_the_menu()
    {
        using var t = Make(out _);
        OpenMenu(t);
        t.Click(5, 5);
        Assert.Null(Menu(t));
    }

    [Fact]
    public void Copy_enables_with_a_selection_and_raises_the_command()
    {
        using var t = Make(out var actions);
        var (fx, fy) = TestDoc.Center(t.FindRole("textbox"));
        t.Click(fx, fy);
        t.Key(EditKey.SelectAll);
        t.Doc.DispatchContextMenu(fx, fy); t.Layout();

        Assert.NotNull(Row(t, "copy"));                  // selection → copy enabled
        t.ClickNode(Row(t, "copy")!);
        Assert.Equal(new[] { ContextCommand.Copy }, actions);
        Assert.Null(Menu(t));                            // menu closed after choosing
        Assert.Equal("Ada Lovelace", t.Doc.CopySelection()); // field not blurred → still copyable
    }

    [Fact]
    public void Right_click_on_empty_space_opens_nothing()
    {
        using var t = Make(out _);
        Assert.False(t.Doc.DispatchContextMenu(2, 2));
        Assert.Null(Menu(t));
    }
}
