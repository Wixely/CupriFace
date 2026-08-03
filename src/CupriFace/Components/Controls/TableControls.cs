using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-table&gt;&lt;cupri-row header&gt;&lt;cupri-cell&gt;…&lt;/cupri-cell&gt;…&lt;/cupri-row&gt;…&lt;/cupri-table&gt;</c>
/// — a simple data table. Rows are flex rows; cells share the width. For a scrolling body,
/// set <c>style="height:…;overflow:scroll"</c> on the table (the engine's overflow scroller
/// takes over). Pairs with collection binding to render rows from a model list.
/// </summary>
public sealed class TableComponent : ComponentBase
{
    public override string Tag => "cupri-table";
    public override string DefaultCss => """
        .cupri-table { display:block; border:1px var(--cupri-border, #e6e9f0); border-radius:10px;
                       color:var(--cupri-text, #1e2430); font-size:14px; }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "table");
        el.ClassList.Add("cupri-table");
    }
}

/// <summary><c>&lt;cupri-row [header]&gt;</c> — a table row; <c>header</c> styles it as the head row.</summary>
public sealed class TableRowComponent : ComponentBase
{
    public override string Tag => "cupri-row";
    public override string DefaultCss => """
        .cupri-row { display:flex; border-bottom:1px var(--cupri-border, #eef1f5); }
        .cupri-row:last-child { border-bottom:0px transparent; }
        .cupri-row.header { background:var(--cupri-hover, #f6f7f9); font-weight:bold; color:var(--cupri-muted, #4a5262); }
        .cupri-row[data-hover]:not(.header) { background:var(--cupri-hover, #f8f9fb); }
        """;

    public override void Expand(IElement el)
    {
        var header = Flag(el, "header");
        el.SetAttribute("role", "row");
        el.ClassList.Add("cupri-row");
        if (header) el.ClassList.Add("header");
    }
}

/// <summary><c>&lt;cupri-cell&gt;</c> — a table cell; shares row width evenly (flex:1).</summary>
public sealed class TableCellComponent : ComponentBase
{
    public override string Tag => "cupri-cell";
    public override string DefaultCss => """
        .cupri-cell { flex:1; padding:11px 14px; }
        """;

    public override void Expand(IElement el)
    {
        // A cell in a header row is a column header for a11y.
        var isHead = el.ParentElement?.ClassList.Contains("header") == true
                     || el.ParentElement?.HasAttribute("header") == true;
        el.SetAttribute("role", isHead ? "columnheader" : "cell");
        el.ClassList.Add("cupri-cell");
    }
}
