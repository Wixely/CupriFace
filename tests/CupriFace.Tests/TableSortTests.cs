using System.Collections.Generic;
using CupriFace.Dom;
using Xunit;

namespace CupriFace.Tests;

public class TableSortTests
{
    private sealed class Model { public string Sort { get; set; } = ""; }

    private const string Html = """
        <body><div style='padding:10px'>
          <cupri-table sort="{{Sort}}">
            <cupri-row header><cupri-cell>Item</cupri-cell><cupri-cell>Qty</cupri-cell></cupri-row>
            <cupri-row><cupri-cell>Pear</cupri-cell><cupri-cell>5</cupri-cell></cupri-row>
            <cupri-row><cupri-cell>Apple</cupri-cell><cupri-cell>3</cupri-cell></cupri-row>
            <cupri-row><cupri-cell>Cherry</cupri-cell><cupri-cell>8</cupri-cell></cupri-row>
          </cupri-table>
        </div></body>
        """;

    // First-column text of each body row, in display order.
    private static List<string> Order(TestDoc t)
    {
        var order = new List<string>();
        void Walk(RenderNode n)
        {
            if (n.Element?.GetAttribute("role") == "row" && n.Element.ClassList.Contains("header") != true)
            {
                var cell = TestDoc.Find(n, x => x.Element?.ClassList.Contains("cupri-cell") == true);
                var text = cell is null ? null : TestDoc.Find(cell, x => x.IsText);
                order.Add(text?.Text ?? "");
            }
            foreach (var c in n.Children) Walk(c);
        }
        Walk(t.Root);
        return order;
    }

    [Fact]
    public void Clicking_headers_sorts_ascending_descending_and_numeric()
    {
        var m = new Model();
        using var t = new TestDoc(Html, "", m, components: true, width: 360, height: 260);

        Assert.Equal(new[] { "Pear", "Apple", "Cherry" }, Order(t)); // markup order (unsorted)

        t.ClickMatch(n => n.Element?.GetAttribute("data-set-value") == "0:asc");
        Assert.Equal("0:asc", m.Sort);
        Assert.Equal(new[] { "Apple", "Cherry", "Pear" }, Order(t)); // by Item, A→Z

        t.ClickMatch(n => n.Element?.GetAttribute("data-set-value") == "0:desc");
        Assert.Equal(new[] { "Pear", "Cherry", "Apple" }, Order(t)); // toggled Z→A

        t.ClickMatch(n => n.Element?.GetAttribute("data-set-value") == "1:asc");
        Assert.Equal(new[] { "Apple", "Pear", "Cherry" }, Order(t)); // by Qty numeric: 3,5,8
    }

    [Fact]
    public void A_table_without_sort_is_not_clickable()
    {
        using var t = new TestDoc(
            "<body><cupri-table><cupri-row header><cupri-cell>H</cupri-cell></cupri-row><cupri-row><cupri-cell>x</cupri-cell></cupri-row></cupri-table></body>",
            "", null, components: true);
        Assert.Null(t.Find(n => n.Element?.HasAttribute("data-sortable") == true));
    }
}
