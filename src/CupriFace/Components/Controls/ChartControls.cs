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
    private static readonly string[] _palette = { "#4682B4", "#e0245e", "#10ac84", "#f5b301", "#9b59b6", "#B87333", "#576574" };
    internal static string Palette(int i) => _palette[System.Math.Abs(i) % _palette.Length];

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

    // Normalised polyline points "x,y x,y …" in 0..1 (y=0 top), with a vertical inset so the line never
    // hugs the edges. fullWidth spans 0..1 (sparkline/rolling); otherwise the points sit at flex-slot
    // centres so they line up with the x-axis labels (line chart). By default the Y axis auto-scales to
    // the data's min..max; pass fixedMin/fixedMax for a stable range (a rolling monitor), which also
    // clamps out-of-range samples into the box.
    internal static string Points(IReadOnlyList<ChartPoint> s, bool fullWidth, double inset = 0.12,
        double? fixedMin = null, double? fixedMax = null)
    {
        if (s.Count == 0) return "";
        var min = fixedMin ?? s.Min(p => p.Value);
        var max = fixedMax ?? s.Max(p => p.Value);
        var range = max - min;
        var sb = new StringBuilder();
        for (var i = 0; i < s.Count; i++)
        {
            var x = fullWidth ? (s.Count == 1 ? 0.5 : (double)i / (s.Count - 1)) : (i + 0.5) / s.Count;
            var norm = range > 1e-9 ? System.Math.Clamp((s[i].Value - min) / range, 0, 1) : 0.5;
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
/// <c>&lt;cupri-line-chart values="…" labels="…" area dots curve&gt;</c> — a trend line auto-scaled to its
/// data. <c>area</c> fills under it, <c>dots</c> marks points, <c>curve</c> smooths it. For MULTIPLE
/// lines, give it <c>&lt;cupri-line values="…" color="…" label="…"&gt;</c> children (they share one Y axis
/// and get a legend); a single series can use <c>values="…"</c> or <c>&lt;cupri-point&gt;</c> children.
/// </summary>
public sealed class LineChartComponent : ComponentBase
{
    public override string Tag => "cupri-line-chart";
    public override string DefaultCss => """
        .cupri-line-chart { display:block; }
        .cupri-lc-plot { position:relative; display:block; height:150px; }
        .cupri-lc-line { position:absolute; top:0; left:0; width:100%; height:100%; color:#4682B4; }
        .cupri-lc-labels { display:flex; margin-top:7px; }
        .cupri-lc-labels > span { flex:1; text-align:center; font-size:12px; color:var(--cupri-muted, #98a2b3); }
        .cupri-lc-legend { display:flex; gap:16px; margin-top:9px; }
        .cupri-lc-key { display:inline-flex; align-items:center; gap:6px; font-size:12px; color:var(--cupri-muted, #98a2b3); }
        .cupri-lc-swatch { width:11px; height:11px; border-radius:3px; }
        """;

    public override void Expand(IElement el)
    {
        el.SetAttribute("role", "img");
        el.ClassList.Add("cupri-line-chart");

        // Multiple series (<cupri-line …> children) or a single series (values / <cupri-point> children).
        var lineEls = el.Children.Where(c => c.LocalName == "cupri-line").ToList();
        var series = lineEls.Count > 0
            ? lineEls.Select((c, i) => (Pts: ChartData.Read(c, "cupri-point"),
                Color: c.GetAttribute("color") is { Length: > 0 } col ? col : ChartData.Palette(i),
                Label: c.GetAttribute("label") ?? "")).ToList()
            : new() { (ChartData.Read(el, "cupri-point"), Str(el, "color", ChartData.Line), "") };

        // One shared Y range across every series so the lines are directly comparable.
        var all = series.SelectMany(s => s.Pts).ToList();
        double min = all.Count > 0 ? all.Min(p => p.Value) : 0, max = all.Count > 0 ? all.Max(p => p.Value) : 1;

        var flags = (Flag(el, "area") ? " data-cupri-area" : "")
                  + (Flag(el, "dots") ? " data-cupri-dots" : "")
                  + (Flag(el, "curve") ? " data-cupri-curve" : "");

        var sb = new StringBuilder("<div class='cupri-lc-plot'>"); // each series overlays as an absolute line
        foreach (var s in series)
            sb.Append($"<div class='cupri-lc-line' data-cupri-line='{ChartData.Points(s.Pts, fullWidth: false, fixedMin: min, fixedMax: max)}'{flags} style='color:{s.Color}'></div>");
        sb.Append("</div>");

        // x-axis labels: the `labels` attr, else the first series' point labels.
        var labels = Str(el, "labels") is { Length: > 0 } lab
            ? lab.Split(',').Select(l => l.Trim()).ToList()
            : series[0].Pts.Select(p => p.Label).ToList();
        if (labels.Any(l => l.Length > 0))
        {
            sb.Append("<div class='cupri-lc-labels'>");
            foreach (var l in labels) sb.Append($"<span>{ChartData.Esc(l)}</span>");
            sb.Append("</div>");
        }

        // Legend for multiple named series.
        if (series.Count > 1 && series.Any(s => s.Label.Length > 0))
        {
            sb.Append("<div class='cupri-lc-legend'>");
            foreach (var s in series)
                sb.Append($"<span class='cupri-lc-key'><span class='cupri-lc-swatch' style='background:{s.Color}'></span>{ChartData.Esc(s.Label)}</span>");
            sb.Append("</div>");
        }
        el.InnerHtml = sb.ToString();
    }
}

/// <summary>A point inside a line chart / sparkline (value / label); consumed by the parent.</summary>
public sealed class PointComponent : ComponentBase
{
    public override string Tag => "cupri-point";
    public override string DefaultCss => "";
    public override void Expand(IElement el) { }
}

/// <summary>A series inside <c>&lt;cupri-line-chart&gt;</c> (values / color / label) for multi-line
/// charts; consumed by the parent.</summary>
public sealed class LineSeriesComponent : ComponentBase
{
    public override string Tag => "cupri-line";
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
        if (Flag(el, "curve")) el.SetAttribute("data-cupri-curve", "");
        el.InnerHtml = ""; // the polyline is painted directly on this element's box
    }
}

/// <summary>
/// <c>&lt;cupri-rolling-chart values="{{Hist}}" max="200"&gt;</c> — a time-series monitor (Task-Manager
/// style): a full-width area line over a FIXED 0..<c>max</c> range, so new samples scroll in from the
/// right without the baseline jumping. Bind <c>values</c> to a rolling window your model maintains and
/// let the refresh cadence drive it (no dots/labels; the plot is tinted, bordered and clips its line).
/// </summary>
public sealed class RollingChartComponent : ComponentBase
{
    public override string Tag => "cupri-rolling-chart";
    public override string DefaultCss => """
        .cupri-rolling { display:block; }
        .cupri-rl-plot { display:block; height:130px; overflow:hidden; color:#4682B4;
                         background:var(--cupri-hover, #eef1f5); border:1px solid var(--cupri-border, #cbd2dc);
                         border-radius:8px; }
        """;

    public override void Expand(IElement el)
    {
        var series = ChartData.Read(el, "cupri-point");
        var max = ChartData.Max(el, series); // fixed ceiling: the `max` attr, else the window's largest
        el.SetAttribute("role", "img");
        el.ClassList.Add("cupri-rolling");

        var pts = ChartData.Points(series, fullWidth: true, inset: 0.06, fixedMin: 0, fixedMax: max);
        var curve = Flag(el, "curve") ? " data-cupri-curve" : "";
        var color = Str(el, "color");
        var style = color.Length > 0 ? $" style='color:{color}'" : "";
        el.InnerHtml = $"<div class='cupri-rl-plot' data-cupri-line='{pts}' data-cupri-area{curve}{style}></div>";
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
