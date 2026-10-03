using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// A binding inside a <c>data-repeat</c> can see the model the list came from.
///
/// <para>It could not, and the failure was silent: the item was the only context, so a name the item
/// did not have produced an empty string with no diagnostic. An author wanting a model-level flag in
/// a list — a theme, a mode, an "is editing" switch — had to copy it onto every item or find an
/// element outside the list to hang it on.</para>
///
/// <para>It is also not what the syntax promises. <c>{{…}}</c> is Mustache, and this engine already
/// borrows <c>{{.}}</c> from it; Mustache resolves a name against a STACK of contexts, innermost
/// first. The expectation arrives with the syntax.</para>
/// </summary>
public class BindingScopeTests
{
    private sealed class Row { public string Name { get; set; } = ""; public string? Note { get; set; } }

    private sealed class Model
    {
        public string Theme { get; set; } = "dark";
        public List<Row> Rows { get; set; } = [new() { Name = "a" }, new() { Name = "b" }];
    }

    private static string TextOf(TestDoc t) =>
        string.Join("|", Collect(t.Root).Where(s => s.Length > 0));

    private static IEnumerable<string> Collect(Dom.RenderNode n)
    {
        if (n.IsText && n.Lines is { Count: > 0 })
            foreach (var l in n.Lines) yield return l.Text.Trim();
        foreach (var c in n.Children)
            foreach (var s in Collect(c)) yield return s;
    }

    /// <summary>THE FIX. A row can name something only the model has.</summary>
    [Fact]
    public void A_row_can_reach_the_model_the_list_came_from()
    {
        using var t = new TestDoc(
            "<body><div data-repeat='Rows'><span>{{Name}}</span><span>{{Theme}}</span></div></body>",
            "body{margin:0}", model: new Model(), width: 400, height: 300);

        Assert.Equal("a|dark|b|dark", TextOf(t));
    }

    /// <summary>It works in an ATTRIBUTE too, which is where it bit first — a class driven by a
    /// model-level flag is the obvious thing to want and produced an empty string.</summary>
    [Fact]
    public void It_works_in_an_attribute_as_well_as_in_text()
    {
        using var t = new TestDoc(
            "<body><div class='row {{Theme}}' data-repeat='Rows'>{{Name}}</div></body>",
            "body{margin:0} .row{height:10px} .row.dark{background:#345}",
            model: new Model(), width: 400, height: 300);

        var row = t.Find(n => n.Element?.ClassList.Contains("row") == true)!;
        Assert.Contains("dark", row.Element!.GetAttribute("class"));
    }

    /// <summary>THE ITEM WINS. A name the item has is answered by the item, never by the model —
    /// otherwise a list would show the same value in every row.</summary>
    [Fact]
    public void The_item_shadows_the_model()
    {
        using var t = new TestDoc(
            "<body><div data-repeat='Rows'>{{Name}}</div></body>",
            "body{margin:0}", model: new Shadowed(), width: 400, height: 300);

        Assert.Equal("a|b", TextOf(t));      // the rows' own names, not the model's
    }

    private sealed class Shadowed
    {
        public string Name { get; set; } = "THE MODEL";
        public List<Row> Rows { get; set; } = [new() { Name = "a" }, new() { Name = "b" }];
    }

    /// <summary>Nested repeats see every enclosing scope, innermost first — a row inside a group can
    /// still reach the model, which is the case that makes a chain rather than one fallback.</summary>
    [Fact]
    public void A_nested_repeat_sees_every_scope_outwards()
    {
        using var t = new TestDoc(
            "<body><div data-repeat='Groups'><span data-repeat='Items'>{{Label}}/{{GroupName}}/{{Theme}}</span></div></body>",
            "body{margin:0}", model: new Nested(), width: 600, height: 300);

        Assert.Equal("x/one/dark|y/one/dark", TextOf(t));
    }

    private sealed class Nested
    {
        public string Theme { get; set; } = "dark";
        public List<Group> Groups { get; set; } = [new()];
    }
    private sealed class Group
    {
        public string GroupName { get; set; } = "one";
        public List<Item> Items { get; set; } = [new() { Label = "x" }, new() { Label = "y" }];
    }
    private sealed class Item { public string Label { get; set; } = ""; }

    /// <summary>A name nothing has is still empty, and still silent — the engine does not invent a
    /// value. That is what <c>CupriDoctor</c> is for, and this pins that the fallback did not turn a
    /// typo into a plausible-looking wrong answer.</summary>
    [Fact]
    public void A_name_nobody_has_is_still_empty()
    {
        using var t = new TestDoc(
            "<body><div data-repeat='Rows'>[{{Nonexistent}}]</div></body>",
            "body{margin:0}", model: new Model(), width: 400, height: 300);

        Assert.Equal("[]|[]", TextOf(t));
    }

    /// <summary>The documented imprecision, pinned so it is a decision rather than a surprise: an item
    /// property that EXISTS and is null does not stop the search, so an outer name answers instead.
    /// Telling "no such name" from "that name is null" is impossible for a model using the generated
    /// accessor, and a rule that differed by how a model opted into binding would be the worse trap.
    /// </summary>
    [Fact]
    public void A_null_item_property_falls_through_to_the_outer_scope()
    {
        using var t = new TestDoc(
            "<body><div data-repeat='Rows'>[{{Note}}]</div></body>",
            "body{margin:0}", model: new NullNote(), width: 400, height: 300);

        Assert.Equal("[outer]|[outer]", TextOf(t));
    }

    private sealed class NullNote
    {
        public string Note { get; set; } = "outer";
        public List<Row> Rows { get; set; } = [new() { Name = "a", Note = null }, new() { Name = "b" }];
    }
}
