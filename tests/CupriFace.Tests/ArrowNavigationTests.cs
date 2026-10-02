using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>doc.ArrowNavigation</c> — the arrows as a keyboard D-pad, off by default.
///
/// <para>The default is the whole point of the flag. Arrow keys in a general-purpose application are
/// expected to move a caret, scroll a view and step through a radio group; silently repurposing them
/// would break every habit a user brought with them. A game wants the opposite, and wants it without
/// a controller plugged in. So the interesting assertions here are not "spatial navigation works" —
/// <see cref="DirectionalFocusTests"/> covers that — but <b>what the flag leaves alone</b>.</para>
/// </summary>
public class ArrowNavigationTests
{
    // Two columns, three rows. Document order crosses each ROW (a1, b1, a2, …), so "next in
    // document order" and "below" are different controls — which is what makes the mode visible.
    private const string Grid = """
        <body>
          <div class='row'><div class='b' role='button'>a1</div><div class='b' role='button'>b1</div></div>
          <div class='row'><div class='b' role='button'>a2</div><div class='b' role='button'>b2</div></div>
          <div class='row'><div class='b' role='button'>a3</div><div class='b' role='button'>b3</div></div>
        </body>
        """;
    private const string GridCss = """
        body { margin:0 }
        .row { display:flex; gap:20px; padding:10px }
        .b { width:160px; height:48px; background:#345; color:#fff; font-size:15px }
        """;

    private static TestDoc Open() => new(Grid, GridCss, width: 600, height: 400);

    /// <summary>OFF (the default): an arrow is Tab by another name. Down from a1 goes to b1 — the
    /// next control in the MARKUP, which is sideways on screen.</summary>
    [Fact]
    public void Off_by_default_an_arrow_steps_in_document_order()
    {
        using var t = Open();
        Assert.False(t.Doc.ArrowNavigation, "a general-purpose UI must not have its arrows repurposed");

        t.Key(EditKey.Tab);
        Assert.Equal("a1", t.FocusedName());
        t.Key(EditKey.Down);
        Assert.Equal("b1", t.FocusedName());     // document order: across the row
    }

    /// <summary>ON: the same keypress goes where the user is pointing. Down from a1 goes to a2.</summary>
    [Fact]
    public void On_an_arrow_moves_by_geometry()
    {
        using var t = Open();
        t.Doc.ArrowNavigation = true;

        t.Key(EditKey.Tab);
        Assert.Equal("a1", t.FocusedName());
        t.Key(EditKey.Down);
        Assert.Equal("a2", t.FocusedName());     // geometry: down the column
        t.Key(EditKey.Right);
        Assert.Equal("b2", t.FocusedName());
    }

    /// <summary>Up and Left are both "backwards" in document order — one sign, so a one-dimensional
    /// implementation cannot tell them apart. With the flag on they must do different things, which
    /// is why the axis is passed through rather than inferred from the sign.</summary>
    [Fact]
    public void Up_and_left_are_different_directions_not_one_sign()
    {
        using var t = Open();
        t.Doc.ArrowNavigation = true;
        t.Key(EditKey.Tab);                       // a1
        t.Key(EditKey.Right);                     // b1
        t.Key(EditKey.Down);                      // b2
        Assert.Equal("b2", t.FocusedName());

        t.Key(EditKey.Up);
        Assert.Equal("b1", t.FocusedName());      // up the column…
        t.Key(EditKey.Left);
        Assert.Equal("a1", t.FocusedName());      // …and left along the row. Not the same move.
    }

    /// <summary>The flag is live. An app can turn it on for a game board and off for the settings
    /// screen behind it, so it must not be latched at construction.</summary>
    [Fact]
    public void The_flag_can_be_flipped_at_runtime()
    {
        using var t = Open();
        t.Key(EditKey.Tab);
        t.Key(EditKey.Down);
        Assert.Equal("b1", t.FocusedName());      // off: document order

        t.Doc.ArrowNavigation = true;
        t.Key(EditKey.Left);
        Assert.Equal("a1", t.FocusedName());      // on: geometry, immediately

        t.Doc.ArrowNavigation = false;
        t.Key(EditKey.Down);
        Assert.Equal("b1", t.FocusedName());      // off again
    }

    /// <summary>Tab is untouched in BOTH modes. The flag adds a way to move; it does not take the
    /// ordinary one away, and an app that turns it on must not lose Tab for its users.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tab_still_follows_the_document(bool arrowNav)
    {
        using var t = Open();
        t.Doc.ArrowNavigation = arrowNav;
        t.Key(EditKey.Tab);
        Assert.Equal("a1", t.FocusedName());
        t.Key(EditKey.Tab);
        Assert.Equal("b1", t.FocusedName());     // across the row, spatially or not
    }

    // ---- what the flag must NOT take away -------------------------------------------------------

    /// <summary>A FOCUSED TEXT FIELD KEEPS ITS CARET. This is the one that would make the flag
    /// unusable if it were wrong: arrows inside a field must edit, not navigate away, or typing into
    /// a game's name box would throw focus across the screen mid-word.</summary>
    [Fact]
    public void A_focused_text_field_still_moves_its_caret()
    {
        var model = new Model();
        using var t = new TestDoc(
            "<body><cupri-textfield value='{{Name}}'></cupri-textfield>"
            + "<div class='b' role='button'>elsewhere</div></body>",
            ".b{width:120px;height:40px}", model: model, width: 400, height: 240, components: true);
        t.Doc.ArrowNavigation = true;

        t.Key(EditKey.Tab);                       // into the field
        t.Type("abc");
        t.Key(EditKey.Left);                      // a caret move, NOT a navigation
        Assert.True(t.FindClass("cupri-textfield").Element!.HasAttribute("data-focus"),
            "the arrow must not have navigated focus out of the field");

        t.Type("X");
        t.Key(EditKey.Tab);                       // blur commits the buffer to the model
        Assert.Equal("abXc", model.Name);         // the caret really was between b and c
    }

    /// <summary>A focused slider still nudges its value. Sliders are decided before the general
    /// arrow handling, and must stay that way.</summary>
    [Fact]
    public void A_focused_slider_still_nudges_its_value()
    {
        var model = new Model();
        using var t = new TestDoc(
            "<body><cupri-slider min='0' max='100' value='{{Vol}}' style='width:200px'></cupri-slider>"
            + "<div class='b' role='button'>elsewhere</div></body>",
            ".b{width:120px;height:40px}", model: model, width: 400, height: 240, components: true);
        t.Doc.ArrowNavigation = true;

        t.Key(EditKey.Tab);                       // onto the slider
        var before = model.Vol;
        t.Key(EditKey.Right);
        Assert.True(model.Vol > before, $"the slider should have nudged up from {before}, got {model.Vol}");
    }

    /// <summary>A radio group still follows the ARIA pattern — the arrow moves AND selects within the
    /// group, rather than navigating to whatever is geometrically nearest.</summary>
    [Fact]
    public void A_radio_group_still_follows_the_aria_pattern()
    {
        var model = new Model();
        using var t = new TestDoc(
            """
            <body>
              <cupri-radio group='{{Size}}' value='small'></cupri-radio>
              <cupri-radio group='{{Size}}' value='medium'></cupri-radio>
              <cupri-radio group='{{Size}}' value='large'></cupri-radio>
              <div class='b' role='button'>elsewhere</div>
            </body>
            """, ".b{width:120px;height:40px}", model: model, width: 400, height: 240, components: true);
        t.Doc.ArrowNavigation = true;

        t.Key(EditKey.Tab);                       // onto the checked radio ("small")
        t.Key(EditKey.Down);
        Assert.Equal("medium", model.Size);       // moved AND selected, not navigated
    }

    private sealed class Model
    {
        public string Name { get; set; } = "";
        public string Size { get; set; } = "small";
        public int Vol { get; set; } = 50;
    }
}
