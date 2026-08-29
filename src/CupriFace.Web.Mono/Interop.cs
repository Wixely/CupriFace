using System.Runtime.InteropServices.JavaScript;

namespace CupriFace.Web;

// The Mono host's half of the browser host: the declarations that reach JS, and nothing else.
// Everything above them — the lifecycle, painting, input, ARIA, IME, clipboard, video — is
// WebHostCore, shared with the NativeAOT host (#79).
//
// Exports are [JSExport] methods the page calls by name; imports are [JSImport] into the module
// main.js registers as "cupri". Both halves ship in this package so they cannot drift.

/// <summary>The JS-facing surface. Internal: an app talks to <see cref="WebHost"/>, never here.</summary>
internal partial class Interop
{
    /// <summary>What WebHost.Run was handed, before Init consumes it.</summary>
    internal static CupriApp? Pending;
    internal static Action<CupriDocument>? Configure;
    internal static bool Started;

    private static readonly MonoBridge Bridge = new();

    // ---- lifecycle -----------------------------------------------------------------------------

    /// <summary>Build the app's document once. Called by main.js after the runtime is resident,
    /// which is after Main has run — so the app WebHost.Run registered is waiting here.</summary>
    [JSExport]
    internal static void Init()
    {
        var app = Pending ?? throw new InvalidOperationException(
            "No app was registered. A CupriFace.Web app's Main must call WebHost.Run(new MyApp()) — " +
            "the host is handed its app there, and the page boots into whatever Main registered.");
        Pending = null;
        Started = true;
        WebHostCore.Init(app, Configure, Bridge);
    }

    [JSExport] internal static bool Tick(int width, int height, double nowMs) => WebHostCore.Tick(width, height, nowMs);

    // ---- input ---------------------------------------------------------------------------------

    [JSExport] internal static void PointerDown(double x, double y, int clicks) => WebHostCore.PointerDown(x, y, clicks);
    [JSExport] internal static void PointerMove(double x, double y) => WebHostCore.PointerMove(x, y);
    [JSExport] internal static void PointerUp(double x, double y) => WebHostCore.PointerUp(x, y);
    [JSExport] internal static void ContextMenu(double x, double y) => WebHostCore.ContextMenu(x, y);
    [JSExport] internal static void Wheel(double x, double y, double dy) => WebHostCore.Wheel(x, y, dy);

    [JSExport] internal static void TouchDown(int id, double x, double y, double tMs) => WebHostCore.TouchDown(id, x, y, tMs);
    [JSExport] internal static void TouchMove(int id, double x, double y, double tMs) => WebHostCore.TouchMove(id, x, y, tMs);
    [JSExport] internal static void TouchUp(int id, double x, double y, double tMs) => WebHostCore.TouchUp(id, x, y, tMs);
    [JSExport] internal static void TouchCancel(int id, double tMs) => WebHostCore.TouchCancel(id, tMs);
    [JSExport] internal static void SetCoarsePointer(bool coarse) => WebHostCore.SetCoarsePointer(coarse);

    [JSExport] internal static void KeyChar(string text) => WebHostCore.KeyChar(text);
    [JSExport] internal static void EditKeyPress(int code, int mods) => WebHostCore.EditKeyPress(code, mods);
    [JSExport] internal static bool KeyChord(string text, int mods) => WebHostCore.KeyChord(text, mods);
    [JSExport] internal static string EditKeyMap() => WebHostCore.EditKeyMap();

    [JSExport] internal static void SetComposition(string text) => WebHostCore.SetComposition(text);
    [JSExport] internal static void CommitComposition(string text) => WebHostCore.CommitComposition(text);
    [JSExport] internal static void CancelComposition() => WebHostCore.CancelComposition();

    [JSExport] internal static string? CopySelection() => WebHostCore.CopySelection();
    [JSExport] internal static string? CutSelection() => WebHostCore.CutSelection();
    [JSExport] internal static void Undo() => WebHostCore.Undo();
    [JSExport] internal static void Redo() => WebHostCore.Redo();

    /// <summary>The browser's own fullscreen transitions (its Esc never reaches EditKeyPress).</summary>
    [JSExport] internal static void HostFullscreen(bool active) => WebHostCore.NotifyHostFullscreen(active);

    /// <summary>What the host told the engine it is being driven by. Exists so a browser test can
    /// check the CAPABILITY BOUNDARY — that a touch reached the document as a coarse pointer.</summary>
    [JSExport] internal static bool IsCoarsePointer() => WebHostCore.IsCoarsePointer();

    [JSExport] internal static bool IsTransparent() => WebHostCore.IsTransparent();

    // ---- imports (module "cupri", registered by main.js) ---------------------------------------

    // The pixels are copied into the 2D canvas via putImageData; the damage rect narrows the blit
    // to the region this frame actually changed.
    [JSImport("present", "cupri")]
    internal static partial void Present([JSMarshalAs<JSType.MemoryView>] Span<byte> rgba, int width, int height,
        int dx, int dy, int dw, int dh);

    [JSImport("cursor", "cupri")] internal static partial void SetCursor(string name);
    [JSImport("navigate", "cupri")] internal static partial void OpenUrl(string href);
    [JSImport("favicon", "cupri")] internal static partial void SetFavicon(string dataUri);
    [JSImport("clipboardWrite", "cupri")] internal static partial void ClipboardWrite(string text);
    [JSImport("clipboardPaste", "cupri")] internal static partial void ClipboardPaste();
    [JSImport("a11y", "cupri")] internal static partial void A11y(string html);
    [JSImport("textInput", "cupri")] internal static partial void TextInputJs(
        bool focused, bool numeric, bool multiline, double x, double y);
    [JSImport("windowCommand", "cupri")] internal static partial void WindowCommand(int command);

    [JSImport("videoOpen", "cupri")] internal static partial void VideoOpen(int id, string src);
    [JSImport("videoOpenBytes", "cupri")] internal static partial void VideoOpenBytes(int id,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);
    [JSImport("videoClose", "cupri")] internal static partial void VideoClose(int id);
    [JSImport("videoPlay", "cupri")] internal static partial void VideoPlay(int id);
    [JSImport("videoPause", "cupri")] internal static partial void VideoPause(int id);
    [JSImport("videoMuted", "cupri")] internal static partial void VideoMuted(int id, bool muted);
    [JSImport("videoVolume", "cupri")] internal static partial void VideoVolume(int id, double volume);
    [JSImport("videoLoop", "cupri")] internal static partial void VideoLoop(int id, bool loop);
    [JSImport("videoSeek", "cupri")] internal static partial void VideoSeek(int id, double seconds);
    [JSImport("videoRect", "cupri")] internal static partial void VideoRect(int id,
        double x, double y, double w, double h,
        double cT, double cR, double cB, double cL,
        bool visible, string fit,
        double ta, double tb, double tc, double td, double te, double tf);
}

/// <summary>The bridge, over Mono's JS interop. Nothing but adaptation: the core asks in plain
/// terms and each method forwards to the matching [JSImport] above.</summary>
internal sealed unsafe class MonoBridge : IWebBridge
{
    /// <summary>The one call whose shape differs between the hosts. Mono marshals a MemoryView, so
    /// the address the core hands over becomes a Span here — a view over the same wasm memory, not
    /// a copy, which is what keeps the frame path allocation-free.</summary>
    public void Present(nint pixels, int byteCount, int width, int height, int dx, int dy, int dw, int dh) =>
        Interop.Present(new Span<byte>((void*)pixels, byteCount), width, height, dx, dy, dw, dh);

    public void SetCursor(string cssCursor) => Interop.SetCursor(cssCursor);
    public void Navigate(string href) => Interop.OpenUrl(href);
    public void SetFavicon(string dataUri) => Interop.SetFavicon(dataUri);
    public void ClipboardWrite(string text) => Interop.ClipboardWrite(text);
    public void ClipboardPaste() => Interop.ClipboardPaste();
    public void PublishAria(string html) => Interop.A11y(html);
    public void SetTextInput(bool focused, bool numeric, bool multiline, double x, double y) =>
        Interop.TextInputJs(focused, numeric, multiline, x, y);
    public void WindowCommand(int command) => Interop.WindowCommand(command);

    public void VideoOpen(int id, string src) => Interop.VideoOpen(id, src);
    public void VideoOpenBytes(int id, byte[] bytes) => Interop.VideoOpenBytes(id, bytes.AsSpan());
    public void VideoClose(int id) => Interop.VideoClose(id);
    public void VideoPlay(int id) => Interop.VideoPlay(id);
    public void VideoPause(int id) => Interop.VideoPause(id);
    public void VideoMuted(int id, bool muted) => Interop.VideoMuted(id, muted);
    public void VideoVolume(int id, double volume) => Interop.VideoVolume(id, volume);
    public void VideoLoop(int id, bool loop) => Interop.VideoLoop(id, loop);
    public void VideoSeek(int id, double seconds) => Interop.VideoSeek(id, seconds);
    public void VideoRect(int id, double x, double y, double w, double h,
                          double clipTop, double clipRight, double clipBottom, double clipLeft,
                          bool visible, string fit,
                          double a, double b, double c, double d, double e, double f) =>
        Interop.VideoRect(id, x, y, w, h, clipTop, clipRight, clipBottom, clipLeft, visible, fit, a, b, c, d, e, f);
}
