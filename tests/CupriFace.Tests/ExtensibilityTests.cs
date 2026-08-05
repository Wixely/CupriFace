using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

public class ExtensibilityTests
{
    private sealed class Model { public int Rating { get; set; } }

    [Fact]
    public void Custom_action_fires_on_click_with_element_value_and_model()
    {
        var m = new Model();
        using var t = new TestDoc("<body><div class='star' data-rate='4' role='button'>rate</div></body>", "", m);
        string? seen = null;
        t.Doc.OnAction("data-rate", e => { seen = e.Value; ((Model)e.Model!).Rating = int.Parse(e.Value); return true; });

        t.ClickNode(t.FindClass("star"));
        Assert.Equal("4", seen);
        Assert.Equal(4, m.Rating);
    }

    [Fact]
    public void Custom_action_also_fires_on_keyboard_activation()
    {
        var m = new Model();
        using var t = new TestDoc("<body><div class='star' data-rate='3' role='button'>rate</div></body>", "", m);
        t.Doc.OnAction("data-rate", e => { ((Model)e.Model!).Rating = int.Parse(e.Value); return true; });

        t.Key(EditKey.Tab);     // focus the button
        t.Key(EditKey.Enter);   // activate → ActivateFrom → custom action
        Assert.Equal(3, m.Rating);
    }

    [Fact]
    public void Returning_false_lets_the_event_fall_through()
    {
        var m = new Model();
        using var t = new TestDoc("<body><div class='star' data-rate='9' role='button'>rate</div></body>", "", m);
        var called = false;
        t.Doc.OnAction("data-rate", _ => { called = true; return false; }); // observe but don't consume
        t.ClickNode(t.FindClass("star"));
        Assert.True(called);
        Assert.Equal(0, m.Rating);                 // handler chose not to act
    }
}
