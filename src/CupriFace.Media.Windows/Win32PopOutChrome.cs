using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CupriFace.Media.Windows;

internal static partial class Win32PopOutChrome
{
    private const string ClassName = "CupriFace.Media.Windows.PopOutChrome";
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpFrameChanged = 0x0020;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int GwlStyle = -16;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const uint WsPopupVisible = WsPopup | WsVisible;
    private const uint MfString = 0x00000000;
    private const uint MfPopup = 0x00000010;
    private const uint MfChecked = 0x00000008;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const int Transparent = 1;
    private const uint DtSingleLine = 0x20;
    private const uint DtVCenter = 0x04;
    private const uint DtCenter = 0x01;
    private const uint DtLeft = 0;
    private const int PsSolid = 0;
    private const int TimerId = 41;

    private static readonly object Gate = new();
    private static readonly Dictionary<nint, State> ByParent = [];
    private static readonly Dictionary<nint, State> ByOverlay = [];
    private static readonly Dictionary<nint, State> ByTooltip = [];
    private static bool _registered;
    private static nint _instance;

    private sealed class State
    {
        internal required nint Parent;
        internal required PopOutPlayerControls Controls;
        internal nint Overlay;
        internal nint Tooltip;
        internal string TooltipText = "";
        internal bool Fullscreen;
        internal Rect RestoreBounds;
        internal nuint RestoreStyle;
        internal bool RestoreMaximized;
        internal bool Scrubbing;
        internal long LastInteraction = Environment.TickCount64;
        internal Point LastCursor;
    }

    internal static void Attach(nint parent, PopOutPlayerControls controls, bool restoreFullscreen)
    {
        EnsureClass();
        var state = new State { Parent = parent, Controls = controls };
        state.Overlay = CreateWindowExW(0, ClassName, "", WsChild | WsVisible, 0, 0, 1, 1, parent, 0, _instance, 0);
        state.Tooltip = CreateWindowExW(0, ClassName, "", WsChild, 0, 0, 1, 1, parent, 0, _instance, 0);
        lock (Gate)
        {
            ByParent[parent] = state;
            ByOverlay[state.Overlay] = state;
            ByTooltip[state.Tooltip] = state;
        }
        SetTimer(parent, TimerId, 250, 0);
        Resize(parent);
        if (restoreFullscreen) ToggleFullscreen(state);
    }

    internal static void Detach(nint parent)
    {
        State? state;
        lock (Gate)
        {
            if (!ByParent.Remove(parent, out state)) return;
            ByOverlay.Remove(state.Overlay);
            ByTooltip.Remove(state.Tooltip);
        }
        KillTimer(parent, TimerId);
        if (state.Overlay != 0) DestroyWindow(state.Overlay);
        if (state.Tooltip != 0) DestroyWindow(state.Tooltip);
    }

    internal static bool HandleParentMessage(nint parent, uint message, nuint wordParameter, nint longParameter)
    {
        State? state;
        lock (Gate) ByParent.TryGetValue(parent, out state);
        if (state is null) return false;
        switch (message)
        {
            case 0x0005: // WM_SIZE
                Resize(parent);
                return false;
            case 0x0113: // WM_TIMER
                if ((int)wordParameter == TimerId) Tick(state);
                return false;
            case 0x007B: // WM_CONTEXTMENU
                ShowTrackMenu(state, SignedLow(longParameter), SignedHigh(longParameter));
                return true;
            case 0x0100 when (int)wordParameter == 0x1B && state.Fullscreen: // Escape
                ToggleFullscreen(state);
                return true;
        }
        return false;
    }

    internal static PopOutBounds? CaptureBounds(nint parent)
    {
        State? state;
        lock (Gate) ByParent.TryGetValue(parent, out state);
        if (state is not null && state.Fullscreen)
            return new PopOutBounds(
                state.RestoreBounds.Left,
                state.RestoreBounds.Top,
                state.RestoreBounds.Right - state.RestoreBounds.Left,
                state.RestoreBounds.Bottom - state.RestoreBounds.Top,
                state.RestoreMaximized,
                true);
        if (!GetWindowRect(parent, out var rect)) return null;
        return new PopOutBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, IsZoomed(parent));
    }

    private static void Tick(State state)
    {
        if (GetCursorPos(out var cursor) && GetWindowRect(state.Parent, out var windowBounds))
        {
            var inside = cursor.X >= windowBounds.Left && cursor.X < windowBounds.Right
                && cursor.Y >= windowBounds.Top && cursor.Y < windowBounds.Bottom;
            if (inside && (cursor.X != state.LastCursor.X || cursor.Y != state.LastCursor.Y))
                state.LastInteraction = Environment.TickCount64;
            if (!inside) ShowWindow(state.Tooltip, SwHide);
            state.LastCursor = cursor;
        }
        var show = state.Scrubbing || !state.Fullscreen || !state.Controls.IsPlaying()
            || Environment.TickCount64 - state.LastInteraction < 3000;
        ShowWindow(state.Overlay, show ? SwShowNoActivate : SwHide);
        if (!show) ShowWindow(state.Tooltip, SwHide);
        InvalidateRect(state.Overlay, 0, false);
    }

    private static void Resize(nint parent)
    {
        State? state;
        lock (Gate) ByParent.TryGetValue(parent, out state);
        if (state is null || !GetClientRect(parent, out var rect)) return;
        var scale = Math.Max(1f, GetDpiForWindow(parent) / 96f);
        var height = (int)MathF.Round(48 * scale);
        SetWindowPos(state.Overlay, 0, 0, Math.Max(0, rect.Bottom - height), rect.Right, height,
            SwpNoActivate | SwpShowWindow);
    }

    private static unsafe void EnsureClass()
    {
        if (_registered) return;
        _instance = GetModuleHandleW(0);
        var className = ClassName;
        fixed (char* name = className)
        {
            var windowClass = new WindowClass
            {
                WindowProcedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure,
                Instance = _instance,
                Background = GetStockObject(4),
                ClassName = (nint)name,
            };
            _ = RegisterClassW(ref windowClass);
        }
        _registered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint window, uint message, nuint wordParameter, nint longParameter)
    {
        State? state;
        bool tooltip;
        lock (Gate)
        {
            tooltip = ByTooltip.TryGetValue(window, out state);
            if (!tooltip) ByOverlay.TryGetValue(window, out state);
        }
        if (state is null) return DefWindowProcW(window, message, wordParameter, longParameter);
        if (message == 0x000F) // WM_PAINT
        {
            if (tooltip) PaintTooltip(window, state); else PaintBar(window, state);
            return 0;
        }
        if (!tooltip && message == 0x0200) // WM_MOUSEMOVE
        {
            state.LastInteraction = Environment.TickCount64;
            if (state.Scrubbing)
                SeekFromProgressX(state, SignedLow(longParameter));
            HandleProgressHover(state, SignedLow(longParameter), SignedHigh(longParameter));
            return 0;
        }
        if (!tooltip && message == 0x02A3) // WM_MOUSELEAVE
        {
            ShowWindow(state.Tooltip, SwHide);
            return 0;
        }
        if (!tooltip && message == 0x0201) // WM_LBUTTONDOWN
        {
            SetFocus(state.Parent);
            state.LastInteraction = Environment.TickCount64;
            var x = SignedLow(longParameter);
            if (IsOverProgress(state, x))
            {
                state.Scrubbing = true;
                SetCapture(window);
                SeekFromProgressX(state, x);
            }
            else HandleClick(state, x);
            return 0;
        }
        if (!tooltip && message == 0x0202) // WM_LBUTTONUP
        {
            if (state.Scrubbing)
            {
                SeekFromProgressX(state, SignedLow(longParameter));
                state.Scrubbing = false;
                ReleaseCapture();
            }
            return 0;
        }
        if (!tooltip && message == 0x0215) // WM_CAPTURECHANGED
        {
            state.Scrubbing = false;
            return 0;
        }
        if (!tooltip && message == 0x0100 && (int)wordParameter == 0x1B && state.Fullscreen)
        {
            ToggleFullscreen(state);
            return 0;
        }
        if (!tooltip && message == 0x007B) // WM_CONTEXTMENU
        {
            ShowTrackMenu(state, SignedLow(longParameter), SignedHigh(longParameter));
            return 0;
        }
        return DefWindowProcW(window, message, wordParameter, longParameter);
    }

    private static void HandleClick(State state, int x)
    {
        if (!GetClientRect(state.Overlay, out var rect)) return;
        var scale = Math.Max(1f, GetDpiForWindow(state.Parent) / 96f);
        var playEnd = (int)(44 * scale);
        var muteEnd = (int)(86 * scale);
        var fullscreenStart = rect.Right - (int)(48 * scale);
        if (x < playEnd) state.Controls.TogglePlayback();
        else if (x < muteEnd) state.Controls.ToggleMute();
        else if (x >= fullscreenStart) ToggleFullscreen(state);
        InvalidateRect(state.Overlay, 0, false);
    }

    private static bool IsOverProgress(State state, int x)
    {
        if (!TryGetProgressBounds(state, out var start, out var end)) return false;
        return x >= start && x <= end && state.Controls.Duration() > 0;
    }

    private static void SeekFromProgressX(State state, int x)
    {
        if (!TryGetProgressBounds(state, out var start, out var end)) return;
        var duration = state.Controls.Duration();
        if (duration <= 0) return;
        state.Controls.Seek(Math.Clamp((x - start) / (double)Math.Max(1, end - start), 0, 1) * duration);
        InvalidateRect(state.Overlay, 0, false);
    }

    private static bool TryGetProgressBounds(State state, out int start, out int end)
    {
        start = end = 0;
        if (!GetClientRect(state.Overlay, out var rect)) return false;
        var scale = Math.Max(1f, GetDpiForWindow(state.Parent) / 96f);
        start = (int)(142 * scale);
        end = rect.Right - (int)(124 * scale);
        return end > start;
    }

    private static void HandleProgressHover(State state, int x, int y)
    {
        if (!GetClientRect(state.Overlay, out var rect)) return;
        var scale = Math.Max(1f, GetDpiForWindow(state.Parent) / 96f);
        var start = (int)(142 * scale);
        var end = rect.Right - (int)(124 * scale);
        if (x < start || x > end)
        {
            ShowWindow(state.Tooltip, SwHide);
            return;
        }
        var duration = state.Controls.Duration();
        var seconds = duration * Math.Clamp((x - start) / (double)Math.Max(1, end - start), 0, 1);
        var chapter = state.Controls.Chapters().LastOrDefault(item => item.StartSeconds <= seconds);
        state.TooltipText = chapter?.Label ?? FormatTime(seconds);
        var width = Math.Clamp(24 + state.TooltipText.Length * (int)(7 * scale), (int)(72 * scale), (int)(420 * scale));
        GetClientRect(state.Parent, out var parentRect);
        var left = Math.Clamp(x - width / 2, 4, Math.Max(4, parentRect.Right - width - 4));
        var height = (int)(30 * scale);
        var barHeight = rect.Bottom;
        SetWindowPos(state.Tooltip, 0, left, Math.Max(4, parentRect.Bottom - barHeight - height - 5), width, height,
            SwpNoActivate | SwpShowWindow);
        InvalidateRect(state.Tooltip, 0, false);
    }

    private static void PaintBar(nint window, State state)
    {
        var paint = new PaintStruct();
        var dc = BeginPaint(window, ref paint);
        GetClientRect(window, out var rect);
        using var background = new GdiObject(CreateSolidBrush(ToColorRef(0x0B111B)));
        FillRect(dc, ref rect, background.Handle);
        var scale = Math.Max(1f, GetDpiForWindow(state.Parent) / 96f);
        var cy = rect.Bottom / 2;
        DrawPlayPause(dc, (int)(22 * scale), cy, state.Controls.IsPlaying(), scale);
        DrawMute(dc, (int)(64 * scale), cy, state.Controls.IsMuted(), scale);

        SetBkMode(dc, Transparent);
        SetTextColor(dc, ToColorRef(0xF3F6FB));
        var font = CreateFontW(-(int)(12 * scale), 0, 0, 0, 500, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var oldFont = SelectObject(dc, font);
        var timeRect = new Rect { Left = (int)(88 * scale), Top = 0, Right = (int)(138 * scale), Bottom = rect.Bottom };
        DrawTextW(dc, FormatTime(state.Controls.Position()), -1, ref timeRect, DtSingleLine | DtVCenter | DtLeft);

        var fullStart = rect.Right - (int)(48 * scale);
        var durationRect = new Rect { Left = fullStart - (int)(72 * scale), Top = 0, Right = fullStart - 4, Bottom = rect.Bottom };
        DrawTextW(dc, FormatTime(state.Controls.Duration()), -1, ref durationRect, DtSingleLine | DtVCenter | DtCenter);
        SelectObject(dc, oldFont);
        DeleteObject(font);

        var progressStart = (int)(142 * scale);
        var progressEnd = fullStart - (int)(76 * scale);
        DrawProgress(dc, state, progressStart, progressEnd, cy, scale);
        DrawFullscreen(dc, rect.Right - (int)(24 * scale), cy, state.Fullscreen, scale);
        EndPaint(window, ref paint);
    }

    private static void PaintTooltip(nint window, State state)
    {
        var paint = new PaintStruct();
        var dc = BeginPaint(window, ref paint);
        GetClientRect(window, out var rect);
        using var background = new GdiObject(CreateSolidBrush(ToColorRef(0x111925)));
        FillRect(dc, ref rect, background.Handle);
        SetBkMode(dc, Transparent);
        SetTextColor(dc, ToColorRef(0xF3F6FB));
        var font = CreateFontW(-13, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        var old = SelectObject(dc, font);
        DrawTextW(dc, state.TooltipText, -1, ref rect, DtSingleLine | DtVCenter | DtCenter);
        SelectObject(dc, old);
        DeleteObject(font);
        EndPaint(window, ref paint);
    }

    private static void DrawProgress(nint dc, State state, int start, int end, int y, float scale)
    {
        if (end <= start) return;
        using var basePen = new GdiObject(CreatePen(PsSolid, Math.Max(2, (int)(3 * scale)), ToColorRef(0x5B6472)));
        var old = SelectObject(dc, basePen.Handle);
        MoveToEx(dc, start, y, 0); LineTo(dc, end, y);
        var duration = state.Controls.Duration();
        var ratio = duration <= 0 ? 0 : Math.Clamp(state.Controls.Position() / duration, 0, 1);
        using var activePen = new GdiObject(CreatePen(PsSolid, Math.Max(2, (int)(3 * scale)), ToColorRef(0xD8FF59)));
        SelectObject(dc, activePen.Handle);
        MoveToEx(dc, start, y, 0); LineTo(dc, start + (int)((end - start) * ratio), y);
        using var markerPen = new GdiObject(CreatePen(PsSolid, 1, ToColorRef(0x0B111B)));
        SelectObject(dc, markerPen.Handle);
        if (duration > 0)
            foreach (var chapter in state.Controls.Chapters())
            {
                var x = start + (int)((end - start) * Math.Clamp(chapter.StartSeconds / duration, 0, 1));
                MoveToEx(dc, x, y - (int)(5 * scale), 0); LineTo(dc, x, y + (int)(5 * scale));
            }
        using var knob = new GdiObject(CreateSolidBrush(ToColorRef(0xF3F6FB)));
        var kx = start + (int)((end - start) * ratio);
        var radius = Math.Max(4, (int)(5 * scale));
        Ellipse(dc, kx - radius, y - radius, kx + radius, y + radius);
        SelectObject(dc, old);
    }

    private static void DrawPlayPause(nint dc, int x, int y, bool playing, float scale)
    {
        using var brush = new GdiObject(CreateSolidBrush(ToColorRef(0xF3F6FB)));
        if (playing)
        {
            var w = Math.Max(3, (int)(4 * scale)); var h = (int)(12 * scale);
            var a = new Rect { Left = x - (int)(6 * scale), Top = y - h / 2, Right = x - (int)(2 * scale), Bottom = y + h / 2 };
            var b = new Rect { Left = x + (int)(2 * scale), Top = y - h / 2, Right = x + (int)(6 * scale), Bottom = y + h / 2 };
            FillRect(dc, ref a, brush.Handle); FillRect(dc, ref b, brush.Handle);
        }
        else
        {
            var points = new[] { new Point(x - (int)(5 * scale), y - (int)(8 * scale)), new Point(x + (int)(8 * scale), y), new Point(x - (int)(5 * scale), y + (int)(8 * scale)) };
            Polygon(dc, points, points.Length);
        }
    }

    private static void DrawMute(nint dc, int x, int y, bool muted, float scale)
    {
        using var pen = new GdiObject(CreatePen(PsSolid, Math.Max(1, (int)(2 * scale)), ToColorRef(0xF3F6FB)));
        var old = SelectObject(dc, pen.Handle);
        var box = new Rect { Left = x - (int)(8 * scale), Top = y - (int)(4 * scale), Right = x - (int)(3 * scale), Bottom = y + (int)(4 * scale) };
        Rectangle(dc, box.Left, box.Top, box.Right, box.Bottom);
        MoveToEx(dc, box.Right, box.Top, 0); LineTo(dc, x + (int)(3 * scale), y - (int)(8 * scale));
        LineTo(dc, x + (int)(3 * scale), y + (int)(8 * scale)); LineTo(dc, box.Right, box.Bottom);
        if (muted)
        {
            MoveToEx(dc, x + (int)(7 * scale), y - (int)(6 * scale), 0); LineTo(dc, x + (int)(15 * scale), y + (int)(6 * scale));
            MoveToEx(dc, x + (int)(15 * scale), y - (int)(6 * scale), 0); LineTo(dc, x + (int)(7 * scale), y + (int)(6 * scale));
        }
        SelectObject(dc, old);
    }

    private static void DrawFullscreen(nint dc, int x, int y, bool active, float scale)
    {
        using var pen = new GdiObject(CreatePen(PsSolid, Math.Max(1, (int)(2 * scale)), ToColorRef(0xF3F6FB)));
        var old = SelectObject(dc, pen.Handle);
        var r = (int)(8 * scale); var q = (int)(4 * scale);
        if (!active)
        {
            MoveToEx(dc, x-r, y-q, 0); LineTo(dc, x-r, y-r); LineTo(dc, x-q, y-r);
            MoveToEx(dc, x+q, y-r, 0); LineTo(dc, x+r, y-r); LineTo(dc, x+r, y-q);
            MoveToEx(dc, x-r, y+q, 0); LineTo(dc, x-r, y+r); LineTo(dc, x-q, y+r);
            MoveToEx(dc, x+q, y+r, 0); LineTo(dc, x+r, y+r); LineTo(dc, x+r, y+q);
        }
        else Rectangle(dc, x-r, y-r, x+r, y+r);
        SelectObject(dc, old);
    }

    private static void ToggleFullscreen(State state)
    {
        if (!state.Fullscreen)
        {
            GetWindowRect(state.Parent, out state.RestoreBounds);
            state.RestoreStyle = GetWindowLongPtrW(state.Parent, GwlStyle);
            state.RestoreMaximized = IsZoomed(state.Parent);
            var probe = state.RestoreBounds;
            var monitor = MonitorFromRect(ref probe, 2);
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (monitor == 0 || !GetMonitorInfoW(monitor, ref info)) return;
            SetWindowLongPtrW(state.Parent, GwlStyle, WsPopupVisible);
            SetWindowPos(state.Parent, new nint(-1), info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top,
                SwpNoActivate | SwpFrameChanged | SwpShowWindow);
            state.Fullscreen = true;
        }
        else
        {
            SetWindowLongPtrW(state.Parent, GwlStyle, state.RestoreStyle == 0 ? WsOverlappedWindow : state.RestoreStyle);
            SetWindowPos(state.Parent, new nint(-1), state.RestoreBounds.Left, state.RestoreBounds.Top,
                state.RestoreBounds.Right - state.RestoreBounds.Left,
                state.RestoreBounds.Bottom - state.RestoreBounds.Top,
                SwpNoActivate | SwpFrameChanged | SwpShowWindow);
            state.Fullscreen = false;
        }
        state.LastInteraction = Environment.TickCount64;
        Resize(state.Parent);
    }

    private static void ShowTrackMenu(State state, int x, int y)
    {
        if (x == -1 && y == -1 && GetWindowRect(state.Parent, out var window)) { x = window.Left + 24; y = window.Top + 24; }
        var root = CreatePopupMenu(); var audio = CreatePopupMenu(); var subtitles = CreatePopupMenu();
        var audioTracks = state.Controls.AudioTracks();
        for (var i = 0; i < audioTracks.Count; i++)
            AppendMenuW(audio, MfString | (audioTracks[i].Id == state.Controls.SelectedAudioTrack() ? MfChecked : 0), (nuint)(1000 + i), audioTracks[i].Label);
        var subtitleTracks = state.Controls.SubtitleTracks();
        for (var i = 0; i < subtitleTracks.Count; i++)
            AppendMenuW(subtitles, MfString | (subtitleTracks[i].Id == state.Controls.SelectedSubtitleTrack() ? MfChecked : 0), (nuint)(2000 + i), subtitleTracks[i].Label);
        if (audioTracks.Count == 0) AppendMenuW(audio, MfString, 0, "No selectable tracks");
        if (subtitleTracks.Count == 0) AppendMenuW(subtitles, MfString, 0, "No selectable tracks");
        AppendMenuW(root, MfPopup, (nuint)audio, "Audio"); AppendMenuW(root, MfPopup, (nuint)subtitles, "Subtitles");
        SetForegroundWindow(state.Parent);
        var selected = TrackPopupMenu(root, TpmReturnCmd | TpmRightButton, x, y, 0, state.Parent, 0);
        if (selected >= 1000 && selected < 1000 + audioTracks.Count) state.Controls.SelectAudioTrack(audioTracks[selected - 1000].Id);
        else if (selected >= 2000 && selected < 2000 + subtitleTracks.Count) state.Controls.SelectSubtitleTrack(subtitleTracks[selected - 2000].Id);
        DestroyMenu(root); // destroys its submenus
    }

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.Minutes}:{value.Seconds:00}");
    }

    private static int SignedLow(nint value) => unchecked((short)((long)value & 0xffff));
    private static int SignedHigh(nint value) => unchecked((short)(((long)value >> 16) & 0xffff));
    private static uint ToColorRef(uint rgb) => ((rgb & 0xff) << 16) | (rgb & 0xff00) | ((rgb >> 16) & 0xff);

    private sealed class GdiObject(nint handle) : IDisposable { internal nint Handle => handle; public void Dispose() { if (handle != 0) DeleteObject(handle); } }
    [StructLayout(LayoutKind.Sequential)] private struct Point(int x, int y) { internal int X = x; internal int Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left; internal int Top; internal int Right; internal int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private unsafe struct PaintStruct { internal nint Dc; internal int Erase; internal Rect Paint; internal int Restore; internal int IncUpdate; internal fixed byte Reserved[32]; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { internal uint Size; internal Rect Monitor; internal Rect Work; internal uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowClass { internal uint Style; internal nint WindowProcedure; internal int ClassExtraBytes; internal int WindowExtraBytes; internal nint Instance; internal nint Icon; internal nint Cursor; internal nint Background; internal nint MenuName; internal nint ClassName; }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")] private static partial nint GetModuleHandleW(nint name);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClassW")] private static partial ushort RegisterClassW(ref WindowClass value);
    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")] private static partial nint DefWindowProcW(nint window, uint message, nuint wp, nint lp);
    [LibraryImport("user32.dll")] private static partial nint BeginPaint(nint window, ref PaintStruct paint);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EndPaint(nint window, ref PaintStruct paint);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetClientRect(nint window, out Rect rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetWindowRect(nint window, out Rect rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorPos(out Point point);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsZoomed(nint window);
    [LibraryImport("user32.dll")] private static partial uint GetDpiForWindow(nint window);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetWindowPos(nint window, nint after, int x, int y, int w, int h, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint window, int command);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint window);
    [LibraryImport("user32.dll")] private static partial nuint SetTimer(nint window, int id, uint milliseconds, nint callback);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool KillTimer(nint window, int id);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool InvalidateRect(nint window, nint rect, [MarshalAs(UnmanagedType.Bool)] bool erase);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static partial nuint GetWindowLongPtrW(nint window, int index);
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static partial nuint SetWindowLongPtrW(nint window, int index, nuint value);
    [LibraryImport("user32.dll")] private static partial nint MonitorFromRect(ref Rect rect, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [LibraryImport("user32.dll")] private static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string text);
    [LibraryImport("user32.dll")] private static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rect);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyMenu(nint menu);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetForegroundWindow(nint window);
    [LibraryImport("user32.dll")] private static partial nint SetFocus(nint window);
    [LibraryImport("user32.dll")] private static partial nint SetCapture(nint window);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ReleaseCapture();
    [LibraryImport("gdi32.dll")] private static partial nint GetStockObject(int index);
    [LibraryImport("gdi32.dll")] private static partial nint CreateSolidBrush(uint color);
    [LibraryImport("gdi32.dll")] private static partial nint CreatePen(int style, int width, uint color);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint value);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint value);
    [LibraryImport("user32.dll")] private static partial int FillRect(nint dc, ref Rect rect, nint brush);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool MoveToEx(nint dc, int x, int y, nint old);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool LineTo(nint dc, int x, int y);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool Rectangle(nint dc, int left, int top, int right, int bottom);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool Ellipse(nint dc, int left, int top, int right, int bottom);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool Polygon(nint dc, [In] Point[] points, int count);
    [LibraryImport("gdi32.dll")] private static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] private static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)] private static partial nint CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charset, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)] private static partial int DrawTextW(nint dc, string text, int count, ref Rect rect, uint format);
}
