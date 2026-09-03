namespace CupriFace.Web;

/// <summary>
/// Everything the host needs to ask of the page — and the ONLY thing that differs between the two
/// web hosts.
///
/// <para>CupriFace.Web.Mono reaches JS through <c>[JSImport]</c> on the Mono runtime;
/// CupriFace.Web.NativeAot reaches it through <c>DllImport</c> over the C ABI, bound at link time
/// against an Emscripten JS library. Those are genuinely different mechanisms, but what they carry
/// is the same nineteen calls — so this interface is the seam, and everything above it is shared
/// (#79).</para>
///
/// <para>Signatures are chosen so neither host has to allocate to satisfy them. The frame buffer
/// arrives as an address plus a length rather than a <c>Span&lt;byte&gt;</c> or a <c>byte*</c>,
/// because each host wants a different one of those and both can form theirs from an address for
/// free — a shared type here would have forced a copy on one side. Booleans rather than ints,
/// because the C ABI's lack of a bool is the NativeAOT host's problem to solve, not the core's.</para>
/// </summary>
public interface IWebBridge
{
    /// <summary>Blit the frame. <paramref name="pixels"/> points at RGBA8888 bytes living in wasm
    /// memory — never copied, never owned — and the damage rect narrows the blit to what changed.</summary>
    void Present(nint pixels, int byteCount, int width, int height, int dx, int dy, int dw, int dh);

    /// <summary>Set the canvas cursor. Called only when it changes: assigning it every mouse-move
    /// is needless DOM churn.</summary>
    void SetCursor(string cssCursor);

    /// <summary>Open an external link in a new tab. Internal routing and #anchors never reach here.</summary>
    void Navigate(string href);

    /// <summary>Point the page's icon at a data URI, from the app's own <c>CupriApp.Icon</c> bytes.</summary>
    void SetFavicon(string dataUri);

    void ClipboardWrite(string text);

    /// <summary>Ask the page for the clipboard. Asynchronous on the browser side, so the text comes
    /// back later through the host's own paste entry point rather than as a return value.</summary>
    void ClipboardPaste();

    /// <summary>Publish the semantics tree into the off-screen mirror a screen reader reads.</summary>
    void PublishAria(string html);

    /// <summary>Move the hidden textarea to the caret and set its input mode, so an IME's candidate
    /// window opens at the field and a touch keyboard offers the right layout. The coordinates are
    /// the caret's BOTTOM, in canvas pixels.</summary>
    void SetTextInput(bool focused, bool numeric, bool multiline, double x, double y);

    /// <summary>0 toggle, 1 enter, 2 exit — the browser's Fullscreen API.</summary>
    void WindowCommand(int command);

    // ---- video underlay: the browser decodes, the engine punches a hole and draws the controls ---

    void VideoOpen(int id, string src);

    /// <summary>An embedded/file/data source, resolved to bytes by the same pipeline images use, so
    /// an app's embedded clip plays on the web exactly as it does elsewhere.</summary>
    void VideoOpenBytes(int id, byte[] bytes);

    void VideoClose(int id);
    void VideoPlay(int id);
    void VideoPause(int id);
    void VideoMuted(int id, bool muted);
    void VideoVolume(int id, double volume);
    void VideoLoop(int id, bool loop);
    void VideoSeek(int id, double seconds);

    // ---- underlays: any element the host composites BENEATH the engine's canvas ------------------
    // Named for the job rather than for video, because none of it is video-specific: a WebGL canvas
    // under a punched hole needs exactly the same box, clip and transform tracking.

    /// <summary>Create a <c>&lt;canvas&gt;</c> beneath the engine's canvas, for a surface that
    /// reports <c>UnderlayElement == "canvas"</c>. Video does NOT come through here — it creates its
    /// own element, because that lifetime belongs to loading and playback rather than to layout.
    ///
    /// <para><paramref name="surfaceKey"/> becomes the element's DOM id as
    /// <c>cupri-underlay-{key}</c>, which is the only way an app can find the canvas the host made
    /// for it: <c>emscripten_webgl_create_context</c> takes a CSS selector, and a numeric id the app
    /// never sees would leave the seam unusable.</para></summary>
    void UnderlayOpenCanvas(int id, string surfaceKey);

    /// <summary>Remove an underlay this host created, when its element leaves the tree.</summary>
    void UnderlayClose(int id);

    /// <summary>Where the underlaid element must sit, in canvas pixels: box, clip insets, whether it
    /// shows at all, the object-fit keyword, and the 2x3 transform matrix of the engine's own
    /// transform chain — the painted hole moves through those, so the element has to move with it.</summary>
    void UnderlayRect(int id, double x, double y, double w, double h,
                   double clipTop, double clipRight, double clipBottom, double clipLeft,
                   bool visible, string fit,
                   double a, double b, double c, double d, double e, double f);
}
