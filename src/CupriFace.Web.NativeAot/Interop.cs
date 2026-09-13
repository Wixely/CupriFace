using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CupriFace.Interaction;

namespace CupriFace.Web;

// The NativeAOT host's half of the browser host: the declarations that reach JS, and nothing else.
// Everything above them — the lifecycle, painting, input, ARIA, IME, clipboard, video — is
// WebHostCore, shared with the Mono host (#79).
//
// There is no Mono runtime here, so no [JSImport]/[JSExport]: exports are UnmanagedCallersOnly over
// the plain C ABI (ints, doubles, UTF-16 pointers) and imports are DllImports bound at link time
// against wwwroot/imports.js. Two consequences run through this file: the ABI has no bool and no
// string, so booleans cross as ints and strings cross as a pointer plus a length.

/// <summary>The JS-facing surface. Public because ILC exports it; an app talks to
/// <see cref="WebHost"/>, never here.</summary>
public static unsafe partial class Interop
{
    /// <summary>What WebHost.Run was handed, before Init consumes it.</summary>
    internal static CupriApp? Pending;
    internal static Action<CupriDocument>? Configure;
    internal static bool Started;

    private static readonly AotBridge Bridge = new();
    private static bool _crashed;

    /// <summary>An exception crossing back into JS would unwind through the C ABI, so every export
    /// catches. Reported once: a failing frame fails ~60 times a second otherwise.</summary>
    private static void Crash(string where, Exception ex)
    {
        if (_crashed) return;
        _crashed = true;
        Console.WriteLine($"[CupriFace] CRASH in {where}: {ex}");
    }

    private static void Guard(string where, Action body)
    { try { body(); } catch (Exception ex) { Crash(where, ex); } }

    // ---- lifecycle -----------------------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "Init")]
    public static void Init() => Guard("Init", () =>
    {
        var app = Pending ?? throw new InvalidOperationException(
            "No app was registered. A CupriFace.Web.NativeAot app's Main must call " +
            "WebHost.Run(new MyApp()) before the page calls Init.");
        Pending = null;
        Started = true;
        WebHostCore.Init(app, Configure, Bridge);
    });

    [UnmanagedCallersOnly(EntryPoint = "Tick")]
    public static int Tick(int width, int height, double nowMs)
    {
        try { return WebHostCore.Tick(width, height, nowMs) ? 1 : 0; }
        catch (Exception ex) { Crash("Tick", ex); return 0; }
    }

    // ---- input ---------------------------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "PointerDown")]
    public static void PointerDown(double x, double y, int clicks) => Guard("PointerDown", () => WebHostCore.PointerDown(x, y, clicks));

    [UnmanagedCallersOnly(EntryPoint = "PointerMove")]
    public static void PointerMove(double x, double y) => Guard("PointerMove", () => WebHostCore.PointerMove(x, y));

    [UnmanagedCallersOnly(EntryPoint = "PointerUp")]
    public static void PointerUp(double x, double y) => Guard("PointerUp", () => WebHostCore.PointerUp(x, y));

    [UnmanagedCallersOnly(EntryPoint = "ContextMenu")]
    public static void ContextMenu(double x, double y) => Guard("ContextMenu", () => WebHostCore.ContextMenu(x, y));

    [UnmanagedCallersOnly(EntryPoint = "Wheel")]
    public static void Wheel(double x, double y, double dy) => Guard("Wheel", () => WebHostCore.Wheel(x, y, dy));

    [UnmanagedCallersOnly(EntryPoint = "TouchDown")]
    public static void TouchDown(int id, double x, double y, double tMs) => Guard("TouchDown", () => WebHostCore.TouchDown(id, x, y, tMs));

    [UnmanagedCallersOnly(EntryPoint = "TouchMove")]
    public static void TouchMove(int id, double x, double y, double tMs) => Guard("TouchMove", () => WebHostCore.TouchMove(id, x, y, tMs));

    [UnmanagedCallersOnly(EntryPoint = "TouchUp")]
    public static void TouchUp(int id, double x, double y, double tMs) => Guard("TouchUp", () => WebHostCore.TouchUp(id, x, y, tMs));

    [UnmanagedCallersOnly(EntryPoint = "TouchCancel")]
    public static void TouchCancel(int id, double tMs) => Guard("TouchCancel", () => WebHostCore.TouchCancel(id, tMs));

    [UnmanagedCallersOnly(EntryPoint = "SetCoarsePointer")]
    public static void SetCoarsePointer(int coarse) => Guard("SetCoarsePointer", () => WebHostCore.SetCoarsePointer(coarse != 0));

    [UnmanagedCallersOnly(EntryPoint = "EditKeyPress")]
    public static void EditKeyPress(int code, int mods) => Guard("EditKeyPress", () => WebHostCore.EditKeyPress(code, mods));

    [UnmanagedCallersOnly(EntryPoint = "Undo")] public static void Undo() => Guard("Undo", WebHostCore.Undo);
    [UnmanagedCallersOnly(EntryPoint = "Redo")] public static void Redo() => Guard("Redo", WebHostCore.Redo);

    [UnmanagedCallersOnly(EntryPoint = "HostFullscreen")]
    public static void HostFullscreen(int active) => Guard("HostFullscreen", () => WebHostCore.NotifyHostFullscreen(active != 0));

    [UnmanagedCallersOnly(EntryPoint = "IsCoarsePointer")]
    public static int IsCoarsePointer() { try { return WebHostCore.IsCoarsePointer() ? 1 : 0; } catch { return 0; } }

    [UnmanagedCallersOnly(EntryPoint = "IsTransparent")]
    public static int IsTransparent() { try { return WebHostCore.IsTransparent() ? 1 : 0; } catch { return 0; } }

    // ---- strings in: a shared buffer, because the C ABI has none -------------------------------
    // JS asks for a buffer of N chars, writes UTF-16 into it, then calls the consuming export with
    // the length. One live buffer is enough: all input is dispatched synchronously.

    private static char* _inBuf;
    private static int _inBufChars;

    [UnmanagedCallersOnly(EntryPoint = "TextBuffer")]
    public static char* TextBuffer(int chars)
    {
        if (chars > _inBufChars)
        {
            if (_inBuf is not null) NativeMemory.Free(_inBuf);
            _inBufChars = Math.Max(chars, 256);
            _inBuf = (char*)NativeMemory.Alloc((nuint)(_inBufChars * sizeof(char)));
        }
        return _inBuf;
    }

    private static string In(int len) => new(_inBuf, 0, len);

    [UnmanagedCallersOnly(EntryPoint = "KeyChar")]
    public static void KeyChar(int len) => Guard("KeyChar", () => WebHostCore.KeyChar(In(len)));

    [UnmanagedCallersOnly(EntryPoint = "PasteText")]
    public static void PasteText(int len) => Guard("PasteText", () => WebHostCore.KeyChar(In(len)));

    [UnmanagedCallersOnly(EntryPoint = "KeyChord")]
    public static int KeyChord(int len, int mods)
    {
        try { return WebHostCore.KeyChord(In(len), mods) ? 1 : 0; }
        catch (Exception ex) { Crash("KeyChord", ex); return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "SetComposition")]
    public static void SetComposition(int len) => Guard("SetComposition", () => WebHostCore.SetComposition(In(len)));

    [UnmanagedCallersOnly(EntryPoint = "CommitComposition")]
    public static void CommitComposition(int len) => Guard("CommitComposition", () => WebHostCore.CommitComposition(In(len)));

    [UnmanagedCallersOnly(EntryPoint = "CancelComposition")]
    public static void CancelComposition() => Guard("CancelComposition", WebHostCore.CancelComposition);

    // ---- accessibility: what the ARIA overlay posts back (main.js forwards a click on, or focus
    // arriving at, a mirror node by its data-path, through the same text buffer). The same entry
    // points the native bridges use.

    [UnmanagedCallersOnly(EntryPoint = "A11yActivate")]
    public static void A11yActivate(int len) => Guard("A11yActivate", () => WebHostCore.AccessibilityActivate(In(len)));

    [UnmanagedCallersOnly(EntryPoint = "A11yFocus")]
    public static void A11yFocus(int len) => Guard("A11yFocus", () => WebHostCore.AccessibilityFocus(In(len)));

    [UnmanagedCallersOnly(EntryPoint = "A11ySetValue")]
    public static void A11ySetValue(int len, double value) => Guard("A11ySetValue", () => WebHostCore.AccessibilitySetValue(In(len), value));

    // ---- real editing elements ------------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "SetEditText")]
    public static void SetEditText(int len, int selStart, int selEnd) =>
        Guard("SetEditText", () => WebHostCore.SetEditText(In(len), selStart, selEnd));

    [UnmanagedCallersOnly(EntryPoint = "SetEditSelection")]
    public static void SetEditSelection(int start, int end) =>
        Guard("SetEditSelection", () => WebHostCore.SetEditSelection(start, end));

    /// <summary>A fill for a field nobody is editing. TWO strings over an ABI with one buffer, so
    /// they arrive concatenated and are split by the first one's length — a path is digits and
    /// slashes, so no delimiter could be both safe and free.</summary>
    [UnmanagedCallersOnly(EntryPoint = "A11ySetText")]
    public static void A11ySetText(int pathLen, int totalLen) => Guard("A11ySetText", () =>
    {
        var both = In(totalLen);
        WebHostCore.AccessibilitySetText(both[..pathLen], both[pathLen..]);
    });

    // ---- strings out: a null-terminated UTF-16 buffer the page reads with UTF16ToString --------

    private static char* _outBuf;
    private static int _outBufChars;

    private static char* Out(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length + 1 > _outBufChars)
        {
            if (_outBuf is not null) NativeMemory.Free(_outBuf);
            _outBufChars = Math.Max(s.Length + 1, 256);
            _outBuf = (char*)NativeMemory.Alloc((nuint)(_outBufChars * sizeof(char)));
        }
        s.CopyTo(new Span<char>(_outBuf, s.Length));
        _outBuf[s.Length] = ' ';
        return _outBuf;
    }

    [UnmanagedCallersOnly(EntryPoint = "CopySelection")]
    public static char* CopySelection() { try { return Out(WebHostCore.CopySelection()); } catch (Exception ex) { Crash("CopySelection", ex); return null; } }

    [UnmanagedCallersOnly(EntryPoint = "CutSelection")]
    public static char* CutSelection() { try { return Out(WebHostCore.CutSelection()); } catch (Exception ex) { Crash("CutSelection", ex); return null; } }

    [UnmanagedCallersOnly(EntryPoint = "EditKeyMap")]
    public static char* EditKeyMap() { try { return Out(WebHostCore.EditKeyMap()); } catch (Exception ex) { Crash("EditKeyMap", ex); return null; } }

    // ---- imports (wwwroot/imports.js; DirectPInvoke("js") binds these at link time) -------------

    [DllImport("js", EntryPoint = "js_present")]
    private static extern void JsPresent(byte* rgba, int width, int height, int dx, int dy, int dw, int dh);

    [DllImport("js", EntryPoint = "js_cursor")] private static extern void JsCursor(char* utf16, int len);
    [DllImport("js", EntryPoint = "js_navigate")] private static extern void JsNavigate(char* utf16, int len);
    [DllImport("js", EntryPoint = "js_favicon")] private static extern void JsFavicon(char* utf16, int len);
    [DllImport("js", EntryPoint = "js_clipboard_write")] private static extern void JsClipboardWrite(char* utf16, int len);
    [DllImport("js", EntryPoint = "js_clipboard_paste")] private static extern void JsClipboardPaste();
    [DllImport("js", EntryPoint = "js_a11y")] private static extern void JsA11y(char* utf16, int len);
    [DllImport("js", EntryPoint = "js_text_input")]
    private static extern void JsTextInput(int focused, int numeric, int multiline, double x, double y);
    [DllImport("js", EntryPoint = "js_window_command")] private static extern void JsWindowCommand(int command);

    [DllImport("js", EntryPoint = "js_video_open")] private static extern void JsVideoOpen(int id, char* src, int len);
    [DllImport("js", EntryPoint = "js_video_open_bytes")] private static extern void JsVideoOpenBytes(int id, byte* data, int len);
    [DllImport("js", EntryPoint = "js_video_close")] private static extern void JsVideoClose(int id);
    [DllImport("js", EntryPoint = "js_video_play")] private static extern void JsVideoPlay(int id);
    [DllImport("js", EntryPoint = "js_video_pause")] private static extern void JsVideoPause(int id);
    [DllImport("js", EntryPoint = "js_video_muted")] private static extern void JsVideoMuted(int id, int muted);
    [DllImport("js", EntryPoint = "js_video_volume")] private static extern void JsVideoVolume(int id, double volume);
    [DllImport("js", EntryPoint = "js_video_loop")] private static extern void JsVideoLoop(int id, int loop);
    [DllImport("js", EntryPoint = "js_video_seek")] private static extern void JsVideoSeek(int id, double seconds);
    [DllImport("js", EntryPoint = "js_underlay_open_canvas")] private static extern void JsUnderlayOpenCanvas(int id, char* key, int keyLen);
    [DllImport("js", EntryPoint = "js_underlay_close")] private static extern void JsUnderlayClose(int id);
    [DllImport("js", EntryPoint = "js_underlay_rect")] private static extern void JsUnderlayRect(int id,
        double x, double y, double w, double h,
        double clipTop, double clipRight, double clipBottom, double clipLeft,
        int visible, char* fit, int fitLen,
        double ta, double tb, double tc, double td, double te, double tf); // CSS matrix(a..f)

    // Each string call pins for exactly the duration of the call: the pointer is only valid inside
    // the fixed scope, and every one of these is synchronous on the JS side.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Send(string s, delegate*<char*, int, void> to) { fixed (char* p = s) to(p, s.Length); }

    internal static void SendCursor(string s) => Send(s, &JsCursor);
    internal static void SendNavigate(string s) => Send(s, &JsNavigate);
    internal static void SendFavicon(string s) => Send(s, &JsFavicon);
    internal static void SendClipboardWrite(string s) => Send(s, &JsClipboardWrite);
    internal static void SendA11y(string s) => Send(s, &JsA11y);

    /// <summary>The bridge, over the C ABI. Nothing but adaptation: bools become ints and strings
    /// become a pinned pointer plus a length, because the ABI has neither.</summary>
    internal sealed class AotBridge : IWebBridge
    {
        /// <summary>The one call whose shape differs between the hosts. Here the address the core
        /// hands over is passed straight through — JS wraps HEAPU8 at it, so nothing is copied.</summary>
        public void Present(nint pixels, int byteCount, int width, int height, int dx, int dy, int dw, int dh) =>
            JsPresent((byte*)pixels, width, height, dx, dy, dw, dh);

        public void SetCursor(string cssCursor) => SendCursor(cssCursor);
        public void Navigate(string href) => SendNavigate(href);
        public void SetFavicon(string dataUri) => SendFavicon(dataUri);
        public void ClipboardWrite(string text) => SendClipboardWrite(text);
        public void ClipboardPaste() => JsClipboardPaste();
        public void PublishAria(string html) => SendA11y(html);
        public void SetTextInput(bool focused, bool numeric, bool multiline, double x, double y) =>
            JsTextInput(focused ? 1 : 0, numeric ? 1 : 0, multiline ? 1 : 0, x, y);
        public void WindowCommand(int command) => JsWindowCommand(command);

        public void VideoOpen(int id, string src) { fixed (char* p = src) JsVideoOpen(id, p, src.Length); }
        public void VideoOpenBytes(int id, byte[] bytes) { fixed (byte* p = bytes) JsVideoOpenBytes(id, p, bytes.Length); }
        public void VideoClose(int id) => JsVideoClose(id);
        public void VideoPlay(int id) => JsVideoPlay(id);
        public void VideoPause(int id) => JsVideoPause(id);
        public void VideoMuted(int id, bool muted) => JsVideoMuted(id, muted ? 1 : 0);
        public void VideoVolume(int id, double volume) => JsVideoVolume(id, volume);
        public void VideoLoop(int id, bool loop) => JsVideoLoop(id, loop ? 1 : 0);
        public void VideoSeek(int id, double seconds) => JsVideoSeek(id, seconds);

        public void UnderlayOpenCanvas(int id, string surfaceKey)
        { fixed (char* p = surfaceKey) JsUnderlayOpenCanvas(id, p, surfaceKey.Length); }
        public void UnderlayClose(int id) => JsUnderlayClose(id);

        public void UnderlayRect(int id, double x, double y, double w, double h,
                              double clipTop, double clipRight, double clipBottom, double clipLeft,
                              bool visible, string fit,
                              double a, double b, double c, double d, double e, double f)
        {
            fixed (char* p = fit)
                JsUnderlayRect(id, x, y, w, h, clipTop, clipRight, clipBottom, clipLeft,
                            visible ? 1 : 0, p, fit.Length, a, b, c, d, e, f);
        }
    }
}
