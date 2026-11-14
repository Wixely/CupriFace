namespace CupriFace;

/// <summary>Whether a host draws a document's pixels on the GPU or the CPU.</summary>
public enum RendererKind
{
    /// <summary>The host has not said — a custom host, a test, or a host that predates this.</summary>
    Unknown,
    Gpu,
    Cpu,
}

/// <summary>
/// What a host is drawing a document with, and why — <see cref="CupriDocument.Renderer"/>.
///
/// <para>Every host tries the GPU first and falls back to the CPU on its own, which is the right
/// behaviour and an invisible one: the page looks the same and runs slower. This record is how the
/// fallback stops being invisible. <see cref="Reason"/> says why a host is on the renderer it is on
/// when that is not simply "the GPU worked".</para>
/// </summary>
/// <param name="Kind">GPU or CPU.</param>
/// <param name="Name">What is drawing, as specifically as the host knows it: the GL renderer string
/// on the GPU, the presentation path on the CPU.</param>
/// <param name="Reason">Why the host is here rather than on the GPU, or what just happened to it (a
/// lost context, a restore). Null for the ordinary case.</param>
public sealed record RendererInfo(RendererKind Kind, string Name, string? Reason = null)
{
    /// <summary>Nothing reported.</summary>
    public static readonly RendererInfo Unknown = new(RendererKind.Unknown, "not reported by this host");

    /// <summary>When the host reported it.</summary>
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public override string ToString() =>
        Reason is null ? $"{Kind}: {Name}" : $"{Kind}: {Name} ({Reason})";
}

public sealed partial class CupriDocument
{
    private RendererInfo _renderer = RendererInfo.Unknown;
    private readonly List<RendererInfo> _rendererHistory = [];
    private const int RendererHistoryLimit = 32;

    /// <summary>What the host is drawing this document with right now. Hosts report it; an app reads
    /// it, typically on a diagnostics page. <see cref="RendererInfo.Unknown"/> until a host says.</summary>
    public RendererInfo Renderer => _renderer;

    /// <summary>Every renderer this document has been drawn with, oldest first — a GPU start, a lost
    /// context, the fallback to the CPU. The last 32.</summary>
    public IReadOnlyList<RendererInfo> RendererHistory => _rendererHistory;

    /// <summary>The host changed renderer, or something happened to the one it has.</summary>
    public event Action<RendererInfo>? RendererChanged;

    /// <summary>
    /// For hosts: say what this document is being drawn with. A report identical to the current one
    /// (same kind, name and reason) is ignored, so a host may report on every frame it likes.
    /// </summary>
    public void ReportRenderer(RendererInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Kind == _renderer.Kind && info.Name == _renderer.Name && info.Reason == _renderer.Reason) return;
        _renderer = info;
        _rendererHistory.Add(info);
        if (_rendererHistory.Count > RendererHistoryLimit) _rendererHistory.RemoveAt(0);
        RendererChanged?.Invoke(info);
    }
}
