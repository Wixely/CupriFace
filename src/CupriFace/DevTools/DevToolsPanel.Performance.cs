using System.Diagnostics;
using System.Globalization;

namespace CupriFace.DevTools;

/// <summary>
/// The Performance tab: what each frame of the app cost, and what each rebuild behind it cost.
///
/// <para><b>What it measures.</b> A frame is layout + building the paint list + (on the
/// incremental path) diffing it against the last + rasterising; a rebuild is the binding, cascade
/// and tree build that a click, a keystroke or a model change causes, broken down by stage. Both
/// are per-document (<see cref="PerformanceLog"/>), recorded only while this tab is showing.</para>
///
/// <para><b>What it does not.</b> The time the host spends presenting (the GPU swap, the software
/// window's texture upload) is the host's and is not here; nor is anything an app does in its own
/// handlers. An idle app draws nothing at all — the engine renders on demand — so an empty chart
/// means "nothing changed", not "nothing measured".</para>
/// </summary>
public sealed partial class DevToolsPanel
{
    private const double FrameBudgetMs = 1000.0 / 60;
    private const int ChartBars = 100;
    private const double ChartHeight = 80;
    private const double WindowSeconds = 2;

    private bool _perfTab;
    private long _perfSeen = -1;
    private long _perfBuiltAt;

    /// <summary>Whether the Performance tab is the one showing (otherwise Elements).</summary>
    public bool ShowingPerformance
    {
        get => _perfTab;
        set
        {
            if (_perfTab == value) return;
            _perfTab = value;
            UpdateRecording();
            MarkStale();
        }
    }

    /// <summary>Whether the app's document is being recorded for the Performance tab right now.</summary>
    public bool RecordingPerformance => _target.Performance is not null;

    private void WirePerformance()
    {
        _ui.OnClick(".dt-tab-elements", _ => ShowingPerformance = false);
        _ui.OnClick(".dt-tab-perf", _ => ShowingPerformance = true);
        _ui.OnClick(".dt-perf-clear", _ => { _target.Performance?.Clear(); MarkStale(); });
    }

    /// <summary>Record only while the tab is showing and the panel is open: the log is the app
    /// document's, and an app being developed should not pay for a view nobody is looking at.</summary>
    private void UpdateRecording()
    {
        if (_open && _perfTab) _target.Performance ??= new PerformanceLog();
        else _target.Performance = null;
    }

    /// <summary>New frames to show, at most four times a second — the chart is for reading, and
    /// redrawing the panel at the app's frame rate would put the panel in its own measurements'
    /// way.</summary>
    private bool PerformanceChanged()
    {
        if (!_open || !_perfTab || _target.Performance is not { } log || log.Version == _perfSeen) return false;
        return Stopwatch.GetElapsedTime(_perfBuiltAt).TotalMilliseconds >= 250;
    }

    private void BuildPerformance()
    {
        _m.ElementsDisplay = _perfTab ? "none" : "flex";
        _m.PerfDisplay = _perfTab ? "flex" : "none";
        _m.ElementsTabCss = _perfTab ? "" : "on";
        _m.PerfTabCss = _perfTab ? "on" : "";
        if (!_perfTab || _target.Performance is not { } log) return;
        _perfSeen = log.Version;
        _perfBuiltAt = Stopwatch.GetTimestamp();

        var frames = log.Frames;
        var now = log.Now;

        // The chart: the last ChartBars frames, scaled so the 60 fps budget always fits and a slow
        // frame is unmistakable without one spike flattening everything else.
        var shown = frames.Skip(Math.Max(0, frames.Count - ChartBars)).ToList();
        var scale = Math.Max(FrameBudgetMs * 2, shown.Count == 0 ? 0 : shown.Max(f => f.Timings.TotalMs));
        _m.Bars = shown.Select(f => new PerfBar
        {
            Height = Math.Max(1, (int)Math.Round(f.Timings.TotalMs / scale * ChartHeight)),
            BarCss = f.Timings.FastPath ? "fast" : f.Timings.TotalMs > FrameBudgetMs ? "slow" : "",
        }).ToList();
        _m.BudgetBottom = (int)Math.Round(FrameBudgetMs / scale * ChartHeight);
        _m.ChartScale = $"{Ms(scale)} at the top · line = 16.7 ms (60 fps)";

        var recent = frames.Where(f => now - f.AtSeconds <= WindowSeconds).Select(f => f.Timings).ToList();
        var rebuildsRecent = log.Rebuilds.Count(r => now - r.AtSeconds <= WindowSeconds);
        var summary = new List<NameValue>();
        if (recent.Count == 0)
        {
            _m.PerfNote = "No frames in the last 2 s — the app is idle. It only draws when something changes: interact with it.";
        }
        else
        {
            _m.PerfNote = "";
            var totals = recent.Select(t => t.TotalMs).OrderBy(x => x).ToList();
            summary.Add(new() { Name = "frames", Value = $"{recent.Count} in 2 s ({recent.Count / WindowSeconds:0.#}/s)" });
            summary.Add(new() { Name = "frame time", Value = $"avg {Ms(totals.Average())} · p95 {Ms(totals[(int)Math.Min(totals.Count - 1, Math.Ceiling(totals.Count * 0.95) - 1)])} · max {Ms(totals[^1])}" });
            summary.Add(new() { Name = "over budget", Value = $"{totals.Count(x => x > FrameBudgetMs)} of {totals.Count} (> 16.7 ms)" });
            summary.Add(new() { Name = "breakdown", Value = $"layout {Ms(recent.Average(t => t.LayoutMs))} · paint {Ms(recent.Average(t => t.PaintListMs))} · diff {Ms(recent.Average(t => t.DiffMs))} · raster {Ms(recent.Average(t => t.RasterMs))} (avg)" });
            var fast = recent.Count(t => t.FastPath);
            if (fast > 0) summary.Add(new() { Name = "fast path", Value = $"{fast} frames only re-rastered live surfaces" });
        }
        summary.Add(new() { Name = "rebuilds", Value = $"{rebuildsRecent} in 2 s" });
        _m.PerfSummary = summary;

        if (log.Rebuilds.Count > 0)
        {
            var last = log.Rebuilds[^1];
            _m.LastRebuild = $"Last rebuild: {Ms(last.TotalMs)}";
            // Slowest first: the question is always "where did it go".
            _m.RebuildPhases = last.Phases.OrderByDescending(p => p.Ms).Take(8)
                .Select(p => new NameValue { Name = p.Phase, Value = Ms(p.Ms) }).ToList();
        }
        else
        {
            _m.LastRebuild = "No rebuild recorded yet.";
            _m.RebuildPhases = [];
        }
    }

    private static string Ms(double ms) =>
        (ms < 10 ? ms.ToString("0.00", CultureInfo.InvariantCulture) : ms.ToString("0.0", CultureInfo.InvariantCulture)) + " ms";
}
