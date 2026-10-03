using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>doc.WasdNavigation</c> — W A S D as a D-pad, off by default.
///
/// <para>This exists because of Steam rather than because of keyboards. Steam Input maps a thumbstick
/// to WASD for games with no controller support, so through the Steam overlay a stick push arrives as
/// the letter "w" and nothing else: no gamepad, no axis, just text. Reported from a real integration
/// where the pad worked in one context, produced WASD in another, and nothing at all in a third.</para>
/// </summary>
public class WasdNavigationTests
{
    // Two columns, three rows: document order crosses each row, so "next in the markup" and "below"
    // are different controls and the mode is visible in where focus lands.
    private const string Grid = """
        <body>
          <div class='row'><div class='b' role='button'>a1</div><div class='b' role='button'>b1</div></div>
          <div class='row'><div class='b' role='button'>a2</div><div class='b' role='button'>b2</div></div>
          <div class='row'><div class='b' role='button'>a3</div><div class='b' role='button'>b3</div></div>
        </body>
        """;
    private const string Css = """
        body { margin:0 }
        .row { display:flex; gap:20px; padding:10px }
        .b { width:160px; height:48px; background:#345; color:#fff; font-size:15px }
        """;

    private static TestDoc Open(bool wasd = true)
    {
        var t = new TestDoc(Grid, Css, width: 600, height: 400);
        t.Doc.WasdNavigation = wasd;
        return t;
    }

    [Fact]
    public void Off_by_default()
    {
        using var t = new TestDoc(Grid, Css, width: 600, height: 400);
        Assert.False(t.Doc.WasdNavigation);
    }

    /// <summary>The letters move by GEOMETRY, not document order — the only reason to turn this on is
    /// that something is pretending to be a keyboard on a controller's behalf.</summary>
    [Fact]
    public void The_letters_navigate_by_geometry()
    {
        using var t = Open();
        t.Key(EditKey.Tab);
        Assert.Equal("a1", t.FocusedName());

        t.Type("s");
        Assert.Equal("a2", t.FocusedName());     // down the column, not across the row
        t.Type("d");
        Assert.Equal("b2", t.FocusedName());
        t.Type("w");
        Assert.Equal("b1", t.FocusedName());
        t.Type("a");
        Assert.Equal("a1", t.FocusedName());
    }

    /// <summary>Capitals too — a stick mapped through Steam with shift held, or caps lock on, must
    /// not quietly stop steering.</summary>
    [Fact]
    public void Capitals_navigate_as_well()
    {
        using var t = Open();
        t.Key(EditKey.Tab);
        t.Type("S");
        Assert.Equal("a2", t.FocusedName());
    }

    /// <summary>
    /// A FOCUSED TEXT FIELD STILL RECEIVES THE LETTERS. This is the assertion that makes the feature
    /// usable at all: an app with a search box cannot have four letters silently stolen, and it is
    /// why this is opt-in rather than on.
    /// </summary>
    [Fact]
    public void Typing_into_a_field_is_not_stolen()
    {
        var model = new Model();
        using var t = new TestDoc(
            "<body><cupri-textfield value='{{Name}}'></cupri-textfield>"
            + "<div class='b' role='button'>elsewhere</div></body>",
            ".b{width:120px;height:40px}", model: model, width: 400, height: 240, components: true);
        t.Doc.WasdNavigation = true;

        t.Key(EditKey.Tab);                      // into the field
        t.Type("sword");
        t.Key(EditKey.Tab);                      // blur commits
        Assert.Equal("sword", model.Name);       // every letter arrived, including s, w and d
    }

    /// <summary>With the flag off the letters are just letters, and focus does not move.</summary>
    [Fact]
    public void Off_the_letters_do_nothing_to_focus()
    {
        using var t = Open(wasd: false);
        t.Key(EditKey.Tab);
        Assert.Equal("a1", t.FocusedName());
        t.Type("s");
        Assert.Equal("a1", t.FocusedName());
    }

    /// <summary>Other letters are untouched — only the four are claimed.</summary>
    [Fact]
    public void Only_those_four_letters_are_claimed()
    {
        using var t = Open();
        t.Key(EditKey.Tab);
        foreach (var c in "qerty") { t.Type(c.ToString()); Assert.Equal("a1", t.FocusedName()); }
    }

    /// <summary>Arrows and WASD coexist: turning one on does not take the other away, and an app may
    /// want both (a Steam-mapped stick and a real keyboard at the same desk).</summary>
    [Fact]
    public void Arrows_still_work_alongside()
    {
        using var t = Open();
        t.Doc.ArrowNavigation = true;
        t.Key(EditKey.Tab);

        t.Type("s");
        Assert.Equal("a2", t.FocusedName());
        t.Key(EditKey.Right);
        Assert.Equal("b2", t.FocusedName());
    }

    private sealed class Model { public string Name { get; set; } = ""; }
}
