using System.Collections.Generic;
using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>Keyboard operation for the controls that lacked it: a reorder list's grip (↑/↓ move the row)
/// and a tree item (→/← expand/collapse). The rest of the keyboard model (Tab, Enter/Space, Escape, radio
/// /slider/grid arrows, focus ring, overlay trapping) already existed.</summary>
public class KeyboardNavTests
{
    private sealed class ListM { public List<string> Items { get; set; } = new() { "Alpha", "Beta", "Gamma" }; }
    private sealed class TreeM { public bool Open { get; set; } }

    [Fact]
    public void A_reorder_grip_moves_its_row_with_the_arrow_keys()
    {
        var m = new ListM();
        const string html = "<body><cupri-reorder><cupri-reorder-item data-repeat=\"Items\">{{.}}</cupri-reorder-item></cupri-reorder></body>";
        using var t = new TestDoc(html, "", m, width: 300, height: 400, components: true);
        t.Doc.OnReorder(e => { var it = m.Items[e.From]; m.Items.RemoveAt(e.From); m.Items.Insert(e.To, it); });

        t.Key(EditKey.Tab);                 // focus the first grip ("Alpha")
        t.Key(EditKey.Down);                // move it down a slot
        Assert.Equal(new[] { "Beta", "Alpha", "Gamma" }, m.Items);

        t.Key(EditKey.Down);                // focus followed the row → moving again works
        Assert.Equal(new[] { "Beta", "Gamma", "Alpha" }, m.Items);

        t.Key(EditKey.Down);                // at the bottom edge → consumed, no change
        Assert.Equal(new[] { "Beta", "Gamma", "Alpha" }, m.Items);

        t.Key(EditKey.Up);                  // back up one
        Assert.Equal(new[] { "Beta", "Alpha", "Gamma" }, m.Items);
    }

    [Fact]
    public void A_tree_item_expands_and_collapses_with_left_and_right()
    {
        var m = new TreeM();
        const string html = "<body><cupri-tree><cupri-tree-item label=\"src\" open=\"{{Open}}\">" +
            "<cupri-tree-item label=\"child\"></cupri-tree-item></cupri-tree-item></cupri-tree></body>";
        using var t = new TestDoc(html, "", m, width: 300, height: 400, components: true);

        t.Key(EditKey.Tab);                 // focus the twist
        Assert.False(m.Open);

        t.Key(EditKey.Right);               // → expands a closed item
        Assert.True(m.Open);
        t.Key(EditKey.Right);               // already open → consumed, no toggle
        Assert.True(m.Open);
        t.Key(EditKey.Left);                // ← collapses an open item
        Assert.False(m.Open);
        t.Key(EditKey.Left);               // already closed → consumed
        Assert.False(m.Open);
    }
}
