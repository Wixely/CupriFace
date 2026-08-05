using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-table&gt;&lt;cupri-row header&gt;&lt;cupri-cell&gt;…&lt;/cupri-cell&gt;…&lt;/cupri-row&gt;…&lt;/cupri-table&gt;</c>
/// — a simple data table. Rows are flex rows; cells share the width. For a scrolling body,
/// set <c>style="height:…;overflow:scroll"</c> on the table (the engine's overflow scroller
/// takes over). Pairs with collection binding to render rows from a model list.
///
/// Add <c>sort="{{Sort}}"</c> to make it <b>sortable</b>: header cells become click triggers that set
/// the bound sort key (<c>"col:dir"</c>, e.g. <c>"1:desc"</c>, toggling asc↔desc), and the body rows
/// are reordered by that column (numeric when both cells parse as numbers, else case‑insensitive text).
/// The active column shows a ▲/▼ marker.
/// </summary>
public sealed class TableComponent : ComponentBase
{
    public override string Tag => "cupri-table";
    public override string DefaultCss => """
        .cupri-table { display:block; border:1px var(--cupri-border, #e6e9f0); border-radius:10px;
                       color:var(--cupri-text, #1e2430); font-size:14px; }
        .cupri-cell[data-sortable] { color:var(--cupri-muted, #4a5262); }
        .cupri-cell[data-sortable][data-hover] { background:var(--cupri-hover, #eef1f5); }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "table");
        el.ClassList.Add("cupri-table");

        var sortPath = el.GetAttribute("data-bind-sort");
        if (string.IsNullOrEmpty(sortPath)) return; // not sortable → plain table

        // Current sort key: "col:dir" (dir = asc|desc). Empty = unsorted.
        var sortCol = -1;
        var asc = true;
        var parts = Str(el, "sort").Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && int.TryParse(parts[0], out var c)) { sortCol = c; asc = parts.Length < 2 || parts[1] != "desc"; }

        var rows = el.Children.Where(r => r.LocalName == "cupri-row").ToList();
        var header = rows.FirstOrDefault(r => r.HasAttribute("header"));
        var body = rows.Where(r => r != header).ToList();

        // Header cells → sort triggers (click cycles this column asc → desc → asc).
        if (header is not null)
        {
            var headCells = header.Children.Where(x => x.LocalName == "cupri-cell").ToList();
            for (var i = 0; i < headCells.Count; i++)
            {
                var next = i == sortCol && asc ? $"{i}:desc" : $"{i}:asc";
                headCells[i].SetAttribute("data-set-path", sortPath);
                headCells[i].SetAttribute("data-set-value", next);
                headCells[i].SetAttribute("data-sortable", "");
                if (i == sortCol) headCells[i].InnerHtml += asc ? " &#9650;" : " &#9660;"; // ▲ / ▼
            }
        }

        // Reorder the body rows by the chosen column, then relocate them after the header.
        if (sortCol >= 0 && body.Count > 1)
        {
            static string Cell(IElement row, int col)
            {
                var cells = row.Children.Where(x => x.LocalName == "cupri-cell").ToList();
                return col < cells.Count ? cells[col].TextContent.Trim() : "";
            }
            body = body.OrderBy(r => Cell(r, sortCol), new SortComparer(asc)).ToList();
            foreach (var r in body) el.AppendChild(r); // AppendChild relocates an existing child to the end
        }
    }

    // Numeric when both sides parse as numbers, else case-insensitive text; direction applied here.
    private sealed class SortComparer(bool asc) : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            a ??= ""; b ??= "";
            var cmp = double.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out var na)
                   && double.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var nb)
                ? na.CompareTo(nb)
                : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            return asc ? cmp : -cmp;
        }
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
