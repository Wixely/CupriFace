using System.Diagnostics;

namespace CupriFace.DevTools;

/// <summary>One rendered frame of a document, as <see cref="CupriDocument.FrameTimings"/> reports it,
/// with when it happened.</summary>
internal readonly record struct FrameSample(double AtSeconds, CupriDocument.FrameTimings Timings);

/// <summary>One rebuild — the binding, cascade and tree build a click or a model change causes —
/// with its total and the time each stage took.</summary>
internal sealed record RebuildSample(double AtSeconds, double TotalMs, IReadOnlyList<(string Phase, double Ms)> Phases);

/// <summary>
/// What the dev tools' Performance view reads: the last few hundred frames a document rendered and
/// the rebuilds behind them.
///
/// <para>Per DOCUMENT, unlike <see cref="CupriDocument.ProfileHook"/>, which is one static hook for
/// the whole process — the panel is a document too, and a view of "the app" that included the
/// panel drawing itself would measure the wrong thing. Recording costs a null check while nothing
/// is listening; the panel attaches one only while its Performance tab is showing.</para>
/// </summary>
internal sealed class PerformanceLog
{
    public const int Capacity = 240;

    private readonly FrameSample[] _frames = new FrameSample[Capacity];
    private int _next, _count;
    private readonly List<RebuildSample> _rebuilds = [];
    private List<(string, double)>? _openPhases;
    private double _openStart;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Bumped on every frame and rebuild, so a reader can tell whether anything is new.</summary>
    public long Version { get; private set; }

    public double Now => _clock.Elapsed.TotalSeconds;

    public void AddFrame(CupriDocument.FrameTimings t)
    {
        CloseRebuild();
        _frames[_next] = new FrameSample(Now, t);
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
        Version++;
    }

    public void BeginRebuild()
    {
        CloseRebuild();
        _openPhases = [];
        _openStart = Now;
    }

    public void AddPhase(string phase, double ms) => _openPhases?.Add((phase, ms));

    /// <summary>A rebuild is complete once the next frame (or the next rebuild) starts — the tree
    /// it built is what that frame lays out.</summary>
    private void CloseRebuild()
    {
        if (_openPhases is null) return;
        var total = 0.0;
        foreach (var (_, ms) in _openPhases) total += ms;
        _rebuilds.Add(new RebuildSample(_openStart, total, _openPhases));
        if (_rebuilds.Count > Capacity) _rebuilds.RemoveAt(0);
        _openPhases = null;
        Version++;
    }

    /// <summary>The frames recorded, oldest first.</summary>
    public IReadOnlyList<FrameSample> Frames
    {
        get
        {
            var list = new List<FrameSample>(_count);
            var start = (_next - _count + Capacity) % Capacity;
            for (var i = 0; i < _count; i++) list.Add(_frames[(start + i) % Capacity]);
            return list;
        }
    }

    public IReadOnlyList<RebuildSample> Rebuilds => _rebuilds;

    public void Clear()
    {
        _next = _count = 0;
        _rebuilds.Clear();
        _openPhases = null;
        Version++;
    }
}
