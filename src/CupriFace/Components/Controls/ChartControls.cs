using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AngleSharp.Dom;

namespace CupriFace.Components.Controls;

/// <summary>A single chart datum: a value with an optional label and per-item colour.</summary>
internal readonly record struct ChartPoint(double Value, string Label, string? Color);

/// <summary>
/// Shared data reading for the chart components. Supports BOTH APIs: child elements
/// (<c>&lt;cupri-bar value label color&gt;</c>) when present, else <c>values="1,2,3"</c> (+ optional
/// <c>labels="a,b,c"</c>) attributes — both bindable to a model string.
/// </summary>
internal static class ChartData
{
    // A small palette for multi-colour cases; single-series charts use one accent by default.
    internal const string Accent = "#B87333";
    internal const string Line = "#4682B4";

    internal static List<ChartPoint> Read(IElement el, string childTag)
    {
        var pts = new List<ChartPoint>();
        var children = el.Children.Where(c => c.LocalName == childTag).ToList();
        if (children.Count > 0)
        {
            foreach (var c in children)
                pts.Add(new ChartPoint(
                    Parse(c.GetAttribute("value")),
                    c.GetAttribute("label") ?? c.TextContent.Trim(),
                    c.GetAttribute("color")));
        }
        else
        {
            var values = Split(el.GetAttribute("values"));
            var labels = Split(el.GetAttribute("labels"));
            for (var i = 0; i < values.Length; i++)
                pts.Add(new ChartPoint(Parse(values[i]), i < labels.Length ? labels[i].Trim() : "", null));
        }
        return pts;
    }

    // The value that maps to the top of the plot: the `max` attribute, else the largest datum.
    internal static double Max(IElement el, IReadOnlyList<ChartPoint> s)
    {
        if (double.TryParse(el.GetAttribute("max"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m > 0) return m;
        return s.Count == 0 ? 1 : System.Math.Max(1e-6, s.Max(p => p.Value));
    }

    // Normalised polyline points "x,y x,y …" in 0..1 (y=0 top), auto-scaled to the data's min..max with a
    // vertical inset so the line never hugs the edges. fullWidth spans 0..1 (sparkline); otherwise the
    // points sit at flex-slot centres so they line up with the x-axis labels (line chart).
    internal static string Points(IReadOnlyList<ChartPoint> s, bool fullWidth, double inset = 0.12)
    {
        if (s.Count == 0) return "";
        double min = s.Min(p => p.Value), max = s.Max(p => p.Value), range = max - min;
        var sb = new StringBuilder();
        for (var i = 0; i < s.Count; i++)
        {
            var x = fullWidth ? (s.Count == 1 ? 0.5 : (double)i / (s.Count - 1)) : (i + 0.5) / s.Count;
            var norm = range > 1e-9 ? (s[i].Value - min) / range : 0.5;
            var y = inset + (1 - norm) * (1 - 2 * inset);
            if (i > 0) sb.Append(' ');
            sb.Append(Fmt(x)).Append(',').Append(Fmt(y));
        }
        return sb.ToString();
    }

    internal static string Fmt(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    internal static double Parse(string? v) => double.TryParse((v ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    internal static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string[] Split(string? v) => (v ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// <c>&lt;cupri-bar-chart values="12,19,7" labels="Mon,Tue,Wed"&gt;</c> (or <c>&lt;cupri-bar value label
/// color&gt;</c> children) — a vertical bar chart. Bars scale to <c>max</c> (or the largest datum).
/// </summary>
public sealed class BarChartComponent : ComponentBase
{
    public override string Tag => "cupri-bar-chart";
    public override string DefaultCss => """
        .cupri-bar-chart { display:block; }
        .cupri-bc-plot { display:flex; align-items:flex-end; gap:8px; height:150px;
                         border-bottom:2px solid var(--cupri-border, #cbd2dc); }
        .cupri-bc-bar { flex:1; min-height:2px; border-radius:5px 5px 0 0; }
        .cupri-bc-labels { display:flex; gap:8px; margin-top:7px; }
        .cupri-bc-labels > span { flex:1; text-align:center; font-size:12px; color:var(--cupri-muted, #98a2b3); }
        """;

    public override void Expand(IElement el)
    {
        var series = ChartData.Read(el, "cupri-bar");
        var max = ChartData.Max(el, series);
        el.SetAttribute("role", "img");
        el.ClassList.Add("cupri-bar-chart");

        var sb = new StringBuilder("<div class='cupri-bc-plot'>");
        foreach (var p in series)
        {
            var pct = System.Math.Clamp(p.Value / max * 100.0, 0, 100);
            var color = p.Color ?? ChartData.Accent;
            sb.Append($"<div class='cupri-bc-bar' style='height:{ChartData.Fmt(pct)}%;background:{color}'></div>");
        }
        sb.Append("</div>");
        if (series.Any(p => p.Label.Length > 0))
        {
            sb.Append("<div class='cupri-bc-labels'>");
            foreach (var p in series) sb.Append($"<span>{ChartData.Esc(p.Label)}</span>");
            sb.Append("</div>");
        }
        el.InnerHtml = sb.ToString();
    }
}

/// <summary>A bar inside <c>&lt;cupri-bar-chart&gt;</c> (value / label / color); consumed by the parent.</summary>
public sealed class BarComponent : ComponentBase
{
    public override string Tag => "cupri-bar";
    public override string DefaultCss => "";
    public override void Expand(IElement el) { }
}

/// <summary>
/// <c>&lt;cupri-line-chart values="…" labels="…" area dots&gt;</c> (or <c>&lt;cupri-point value label&gt;</c>
/// children) — a trend line auto-scaled to its data. <c>area</c> fills under it; <c>dots</c> marks points.
/// </summary>
public sealed class LineChartComponent : ComponentBase
{
    public override string Tag => "cupri-line-chart";
    public override string DefaultCss => """
        .cupri-line-chart { display:block; }
        .cupri-lc-plot { display:block; height:150px; color:#4682B4; }
        .cupri-lc-labels { display:flex; margin-top:7px; }
        .cupri-lc-labels > span { flex:1; text-align:center; font-size:12px; color:var(--cupri-muted, #98a2b3); }
        """;

    public override void Expand(IElement el)
    {
        var series = ChartData.Read(el, "cupri-point");
        el.SetAttribute("role", "img");
        el.ClassList.Add("cupri-line-chart");

        var attrs = new StringBuilder($"data-cupri-line='{ChartData.Points(series, fullWidth: false)}'");
        if (Flag(el, "area")) attrs.Append(" data-cupri-area");
        if (Flag(el, "dots")) attrs.Append(" data-cupri-dots");
        var color = Str(el, "color");
        var style = color.Length > 0 ? $" style='color:{color}'" : "";

        var sb = new StringBuilder($"<div class='cupri-lc-plot' {attrs}{style}></div>");
        if (series.Any(p => p.Label.Length > 0))
        {
            sb.Append("<div class='cupri-lc-labels'>");
            foreach (var p in series) sb.Append($"<span>{ChartData.Esc(p.Label)}</span>");
            sb.Append("</div>");
        }
        el.InnerHtml = sb.ToString();
    }
}

/// <summary>A point inside <c>&lt;cupri-line-chart&gt;</c> (value / label); consumed by the parent.</summary>
public sealed class PointComponent : ComponentBase
{
    public override string Tag => "cupri-point";
    public override string DefaultCss => "";
    public override void Expand(IElement el) { }
}

/// <summary>
/// <c>&lt;cupri-sparkline values="…" area&gt;</c> — a compact, axis-less trend line that sizes to its box
/// (defaults inline), for stat cards or inline text. Spans the full width; <c>area</c>/<c>dots</c> optional.
/// </summary>
public sealed class SparklineComponent : ComponentBase
{
    public override string Tag => "cupri-sparkline";
    public override string DefaultCss => """
        .cupri-sparkline { display:inline-block; width:120px; height:34px; color:#4682B4; vertical-align:middle; }
        """;

    public override void Expand(IElement el)
    {
        var series = ChartData.Read(el, "cupri-point");
        el.SetAttribute("role", "img");
        el.ClassList.Add("cupri-sparkline");
        el.SetAttribute("data-cupri-line", ChartData.Points(series, fullWidth: true));
        if (Flag(el, "area")) el.SetAttribute("data-cupri-area", "");
        if (Flag(el, "dots")) el.SetAttribute("data-cupri-dots", "");
        el.InnerHtml = ""; // the polyline is painted directly on this element's box
    }
}

/// <summary>
/// <c>&lt;cupri-heatmap values="…" columns="7"&gt;</c> (or <c>&lt;cupri-heat value&gt;</c> children) — a grid
/// of cells tinted by intensity (contribution-graph style); darker = higher, relative to <c>max</c>.
/// </summary>
public sealed class HeatmapComponent : ComponentBase
{
    public override string Tag => "cupri-heatmap";
    public override string DefaultCss => """
        .cupri-heatmap { display:grid; gap:4px; }
        .cupri-hm-cell { height:22px; border-radius:4px; }
        """;

    public override void Expand(IElement el)
    {
        var series = ChartData.Read(el, "cupri-heat");
        var max = ChartData.Max(el, series);
        var cols = System.Math.Max(1, (int)Num(el, "columns", 7));
        el.SetAttribute("role", "img");
        el.ClassList.Add("cupri-heatmap");
        el.SetAttribute("style", $"grid-template-columns:repeat({cols},1fr)");

        var sb = new StringBuilder();
        foreach (var p in series)
        {
            var t = System.Math.Clamp(p.Value / max, 0, 1);
            var alpha = ChartData.Fmt(0.12 + 0.88 * t); // faint → solid accent
            sb.Append($"<div class='cupri-hm-cell' style='background:rgba(184,115,51,{alpha})'></div>");
        }
        el.InnerHtml = sb.ToString();
    }
}

/// <summary>A cell inside <c>&lt;cupri-heatmap&gt;</c> (value); consumed by the parent.</summary>
public sealed class HeatCellComponent : ComponentBase
{
    public override string Tag => "cupri-heat";
    public override string DefaultCss => "";
    public override void Expand(IElement el) { }
}
