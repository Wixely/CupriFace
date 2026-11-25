using CupriFace.DevTools;
using CupriFace.Interaction;
using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>
/// The dev tools panel popped out into a window of its own — the same <see cref="DevToolsPanel"/>,
/// drawn into a small SDL window beside the app instead of docked inside it. The window itself (and
/// how its events reach it) is <see cref="SdlChildWindow"/>'s; this class is what the panel does
/// with them.
///
/// <para>A development tool: only created when <see cref="CupriDevTools.IsSupported"/>.</para>
/// </summary>
internal sealed class DevToolsWindow : SdlChildWindow
{
    private static readonly SKColor Backdrop = new(0x1e, 0x1f, 0x24);

    private readonly DevToolsPanel _panel;
    private readonly SdlChildWindows _host;
    private readonly Action _changed;
    private (int X, int Y, int W, int H)? _lastPlace;

    /// <param name="panel">The panel to show.</param>
    /// <param name="host">The child windows of this run, which route this window's events.</param>
    /// <param name="changed">Called when input here changed something the APP's window shows too —
    /// the element highlighted under a hovered tree row, say — so the host draws a frame.</param>
    public DevToolsWindow(DevToolsPanel panel, SdlChildWindows host, Action changed) : base("devtools")
    {
        _panel = panel;
        _host = host;
        _changed = changed;
    }

    /// <summary>Raised when the user closes the window with its own close button.</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Open beside the app. <paramref name="appX"/>/<paramref name="appY"/> are the app window's
    /// client position and <paramref name="appRight"/> its right edge, in screen pixels; the panel is
    /// placed to the right of it, or where it was last time it was open. <paramref name="appLogicalHeight"/>
    /// is the app's height in LOGICAL units — the panel matches it, and the monitor's scale is applied
    /// once, when the window is created. False when SDL refused.
    /// </summary>
    public bool Open(int appX, int appY, int appRight, int appLogicalHeight)
    {
        if (IsOpen) return true;
        if (!_host.EnsureVideo()) return false;
        var (x, y, w, h) = _lastPlace ?? (appRight + 8, appY, (int)DevToolsPanel.DefaultWidth, Math.Max(600, appLogicalHeight));
        if (!Create("CupriFace DevTools", x, y, w, h, resizable: true, applyScale: _lastPlace is null)) return false;
        _host.Add(this);
        return true;
    }

    /// <summary>Close the window, remembering where it was.</summary>
    public void Close()
    {
        if (Placement is { W: > 0, H: > 0 } place) _lastPlace = place;
        _host.Remove(this);
        DestroyWindow();
    }

    protected override void Paint(SKCanvas canvas, int pixelWidth, int pixelHeight, float deviceScale)
    {
        canvas.Clear(Backdrop);
        canvas.Scale(deviceScale);
        _panel.Render(canvas, pixelWidth / deviceScale, pixelHeight / deviceScale);
    }

    // Its own close button closes the dev tools, as a browser's undocked ones do.
    protected override void OnCloseButton() => CloseRequested?.Invoke();

    protected override bool OnPointerMove(float x, float y) => _panel.PanelPointerMove(x, y);

    protected override bool OnPointerDown(float x, float y, int clicks) => _panel.PanelPointerDown(x, y, clicks);

    protected override bool OnPointerUp(float x, float y) => _panel.PanelPointerUp(x, y);

    protected override bool OnPointerLeave()
    {
        _panel.PanelPointerLeave();
        return true;
    }

    protected override bool OnWheel(float x, float y, float notches, KeyMods mods) => _panel.PanelWheel(x, y, -notches * 50f);

    protected override Style.CursorType CursorAt(float x, float y) => _panel.PanelCursor(x, y);

    // The keyboard follows the focused window: this one's keys are the panel's.
    protected override void OnFocusChanged(bool focused) => _panel.PanelHasFocus = focused;

    protected override bool OnKey(EditKey key, KeyMods mods)
    {
        _panel.PanelHasFocus = true;
        return _panel.HandleKey(key, mods);
    }

    protected override bool OnShortcut(char ch, KeyMods mods)
    {
        _panel.PanelHasFocus = true;
        return _panel.PanelShortcut(ch, mods, () => ClipboardText, v => ClipboardText = v);
    }

    protected override bool OnText(string text) => _panel.PanelText(text);

    protected override void OnChanged() => _changed();

    public override void Dispose()
    {
        _host.Remove(this);
        base.Dispose();
    }
}
