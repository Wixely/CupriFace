using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>doc.OnShortcut</c>: a Ctrl/Cmd chord fires its handler anywhere (even mid-edit) and consumes the
/// key; a plain-key shortcut fires only when no field is focused, so it never eats normal typing.
/// </summary>
public class ShortcutTests
{
    private sealed class Model { public string Name { get; set; } = ""; }
    private const string Html = "<body><div style='padding:20px'><cupri-textfield value=\"{{Name}}\"></cupri-textfield></div></body>";

    [Fact]
    public void A_ctrl_shortcut_fires_and_consumes_the_key_even_while_editing()
    {
        var m = new Model();
        var fired = 0;
        using var t = new TestDoc(Html, "", m, width: 400, height: 200, components: true);
        t.Doc.OnShortcut(KeyMods.Ctrl, "k", () => fired++);

        var (fx, fy) = TestDoc.Center(t.FindRole("textbox"));
        t.Click(fx, fy);                                     // focus the field

        Assert.True(t.Doc.DispatchKey("k", EditKey.None, KeyMods.Ctrl)); // Ctrl+K
        Assert.Equal(1, fired);
        Assert.Equal("", m.Name);                            // the "k" was NOT typed into the field
    }

    [Fact]
    public void A_plain_shortcut_fires_only_when_no_field_is_focused()
    {
        var fired = 0;
        using var t = new TestDoc(Html, "", new Model(), width: 400, height: 200, components: true);
        t.Doc.OnShortcut(KeyMods.None, "/", () => fired++);

        Assert.True(t.Doc.DispatchKey("/", EditKey.None));   // nothing focused → fires
        Assert.Equal(1, fired);
        t.Layout();  // a handled shortcut rebuilds the tree — lay out before hit-testing, as hosts do each frame

        var (fx, fy) = TestDoc.Center(t.FindRole("textbox"));
        t.Click(fx, fy);                                     // focus the field
        t.Doc.DispatchKey("/", EditKey.None);                // now "/" is normal input
        Assert.Equal(1, fired);                              // did not fire again
    }
}
