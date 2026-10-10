using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Paint;
using CupriFace.Style;

namespace CupriFace.Shell;

/// <summary>The Win32 parent-window feature offered to optional desktop surface adapters.</summary>
public sealed class Win32HostWindow
{
    private readonly Func<nint?> _handle;

    internal Win32HostWindow(Func<nint?> handle) => _handle = handle;

    public nint Handle => _handle() ?? 0;
}

/// <summary>
/// What this host can offer a composited surface, as a set of features keyed by type.
///
/// <para>A feature is present when the host actually has the thing — a Win32 window only where
/// there are HWNDs. An adapter asks for the ONE type it understands and gets null everywhere else,
/// so <c>CupriFace.Media.Windows</c> needs no platform test to know it cannot run here, and neither
/// will the Linux or Android package beside it. Supporting a new platform is a new adapter package
/// plus one registration below; it is never a branch inside an adapter, and never a branch in an
/// application.</para>
/// </summary>
internal sealed class DesktopHostSurfaceContext : IHostSurfaceContext
{
    private readonly Dictionary<Type, object> _features = [];
    private readonly Action _requestFrame;

    internal DesktopHostSurfaceContext(Func<nint?> hwnd, Action requestFrame)
    {
        _requestFrame = requestFrame;
        if (OperatingSystem.IsWindows()) _features[typeof(Win32HostWindow)] = new Win32HostWindow(hwnd);
        // Linux (X11/Wayland) and Android register their own handle types here when they land.
    }

    public object? GetFeature(Type featureType) =>
        featureType is not null && _features.TryGetValue(featureType, out var feature) ? feature : null;

    public void RequestFrame() => _requestFrame();
}

/// <summary>Maps engine layout to host-composited native surfaces after each rendered frame.</summary>
internal sealed class HostSurfaceCoordinator(
    CupriDocument document,
    IHostSurfaceContext context) : IDisposable
{
    private readonly HashSet<IHostCompositedSurfaceSource> _attached =
        new(ReferenceEqualityComparer.Instance);

    internal void Sync(float scale)
    {
        var seen = new HashSet<IHostCompositedSurfaceSource>(ReferenceEqualityComparer.Instance);
        Walk(document.Root, Math.Max(scale, 0.01f), seen);

        if (_attached.Count == seen.Count && _attached.SetEquals(seen)) return;
        foreach (var source in _attached.ToArray())
        {
            if (seen.Contains(source)) continue;
            Safe(source.Detach);
            _attached.Remove(source);
        }
    }

    private void Walk(RenderNode node, float scale, HashSet<IHostCompositedSurfaceSource> seen)
    {
        if (node.SurfaceKey is { Length: > 0 } key
            && document.Surfaces.Get(key) is IHostCompositedSurfaceSource source)
        {
            seen.Add(source);
            if (!_attached.Contains(source))
            {
                try
                {
                    source.Attach(context);
                    _attached.Add(source);
                }
                catch
                {
                    // A transient host-resource failure may recover on the next rendered frame.
                }
            }
            if (_attached.Contains(source)) Safe(() => source.Arrange(Placement(node, scale)));
        }

        foreach (var child in node.Children) Walk(child, scale, seen);
    }

    private HostSurfacePlacement Placement(RenderNode node, float scale)
    {
        if (!node.LaidOut)
            return new(0, 0, 0, 0, 0, 0, 0, 0, false, "contain", HostSurfaceTransform.Identity, scale);

        var (x, y, w, h) = HitTesting.ScreenBox(node);
        float visL = x, visT = y, visR = x + w, visB = y + h;
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Style.Overflow == OverflowMode.Visible) continue;
            var (ax, ay, aw, ah) = HitTesting.ScreenBox(ancestor);
            visL = MathF.Max(visL, ax);
            visT = MathF.Max(visT, ay);
            visR = MathF.Min(visR, ax + aw);
            visB = MathF.Min(visB, ay + ah);
        }

        // Engine-painted chrome the host's surface would otherwise cover (see the overlay note below).
        var surfaceH = MathF.Max(0, h - HostSurfaceGeometry.OverlayBottomInset(node, y, h));

        var visible = w > 0 && surfaceH > 0 && visR > visL && visB > visT
                      && !HostSurfaceGeometry.IsOccluded(document.Root, x, y, w, surfaceH);
        var matrix = HitTesting.ScreenTransform(node);
        var transform = matrix.IsIdentity
            ? HostSurfaceTransform.Identity
            : new HostSurfaceTransform(
                matrix.ScaleX,
                matrix.SkewY,
                matrix.SkewX,
                matrix.ScaleY,
                (matrix.MapPoint(x, y).X - x) * scale,
                (matrix.MapPoint(x, y).Y - y) * scale);

        return new(
            x * scale,
            y * scale,
            w * scale,
            surfaceH * scale,
            MathF.Max(0, visT - y) * scale,
            MathF.Max(0, x + w - visR) * scale,
            MathF.Max(0, y + surfaceH - visB) * scale,
            MathF.Max(0, visL - x) * scale,
            visible,
            node.Element?.GetAttribute("data-object-fit") ?? "contain",
            transform,
            scale);
    }

    private static void Safe(Action action)
    {
        try { action(); }
        catch { /* one optional native surface must not take down the host */ }
    }

    public void Dispose()
    {
        foreach (var source in _attached) Safe(source.Detach);
        _attached.Clear();
    }
}
