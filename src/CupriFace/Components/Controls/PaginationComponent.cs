using System;
using System.Collections.Generic;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>
/// <c>&lt;cupri-pagination page="{{Page}}" pages="10"&gt;</c> — a 1-based page navigator bound to the
/// current page. Renders ‹ prev, the first/last pages, a small window around the current page (with
/// … gaps), and next ›. Clicking a page or arrow writes the new page number.
/// </summary>
public sealed class PaginationComponent : ComponentBase
{
    public override string Tag => "cupri-pagination";
    public override string DefaultCss => """
        .cupri-pagination { display:inline-flex; gap:4px; align-items:center; }
        .cupri-page, .cupri-page-nav { min-width:32px; height:32px; padding:0 6px; border-radius:6px; font-size:14px;
                                       display:inline-flex; align-items:center; justify-content:center; color:var(--cupri-text, #1e2430); }
        .cupri-page[data-hover], .cupri-page-nav[data-hover] { background:var(--cupri-hover, #eef1f5); }
        .cupri-page.active { background:#B87333; color:white; font-weight:bold; }
        .cupri-page-ell { min-width:20px; text-align:center; color:var(--cupri-muted, #98a2b3); }
        .cupri-page-nav.disabled { color:var(--cupri-border, #cbd2dc); }
        """;

    public override void Expand(IElement el)
    {
        var path = el.GetAttribute("data-bind-page") ?? "";
        var pages = Math.Max(1, (int)Num(el, "pages", 1));
        var page = Math.Clamp((int)Num(el, "page", 1), 1, pages);

        el.SetAttribute("role", "navigation");
        el.ClassList.Add("cupri-pagination");

        // Pages to show: first, last, and a ±1 window around the current.
        var show = new SortedSet<int> { 1, pages, page, page - 1, page + 1 };
        show.RemoveWhere(p => p < 1 || p > pages);

        var sb = new StringBuilder();
        Nav(sb, "chevron-left", page > 1, path, page - 1);
        var prev = 0;
        foreach (var p in show)
        {
            if (p - prev > 1) sb.Append("<div class='cupri-page-ell'>…</div>");
            var active = p == page;
            sb.Append($"<div class='cupri-page{(active ? " active" : "")}' role='button' aria-label='page {p}'")
              .Append(path.Length > 0 && !active ? $" data-set-path='{path}' data-set-value='{p}'" : "")
              .Append($">{p}</div>");
            prev = p;
        }
        Nav(sb, "chevron-right", page < pages, path, page + 1);
        el.InnerHtml = sb.ToString();
    }

    private static void Nav(StringBuilder sb, string icon, bool enabled, string path, int target)
    {
        sb.Append($"<div class='cupri-page-nav{(enabled ? "" : " disabled")}' role='button'")
          .Append(enabled && path.Length > 0 ? $" data-set-path='{path}' data-set-value='{target}'" : "")
          .Append($">{IconMarkup(icon, 16)}</div>");
    }
}
