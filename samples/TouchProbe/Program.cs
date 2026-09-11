// Does SDL actually receive touch on this machine? (#143)
//
// Every proposed fix for "taps do nothing on a desktop build" assumes SDL_FINGER* events arrive.
// On a Steam Deck nobody has tested that: the Deck has working GL, so the app takes the GLFW window,
// and GLFW has no touch API at all — so "nothing reaches the engine" is equally consistent with
// gamescope never delivering touch to SDL either. Those two possibilities need completely different
// fixes, and this binary is the one run that tells them apart.
//
// It is deliberately NOT built on DesktopHost: a raw SDL window and event loop means nothing in
// CupriFace can mask or explain away the answer. The CupriFace part is one document rendered into
// the window, used to answer the second question — if fingers DO arrive, does routing them into the
// engine hit the right element?
//
//   TouchProbe                 report every event, and route fingers into the document
//   TouchProbe --raw           report only; do not dispatch into the document
//   TouchProbe --mouse-hint    ask SDL to synthesise mouse events from touch, then report both
//
// Run it in Desktop Mode first, then in Game Mode (gamescope) — the answer can differ, and the
// gamescope one is the one that matters.
using CupriFace;
using CupriFace.Interaction;
using Silk.NET.SDL;

var routeFingers = !Args("--raw");
var mouseHint = Args("--mouse-hint");

var sdl = Sdl.GetApi();

// SDL_HINT_TOUCH_MOUSE_EVENTS: whether SDL manufactures mouse events from touches. Left at the
// platform default unless asked, because the DEFAULT is what the shipped app experiences — and if
// touches were already arriving as mouse events, taps would work today and this issue would not
// exist. Worth a run either way: if --mouse-hint makes taps work and plain does not, the cheapest
// possible fix is one SetHint call in SdlSoftwareWindow.
if (mouseHint) sdl.SetHint("SDL_TOUCH_MOUSE_EVENTS", "1");

if (sdl.Init(Sdl.InitVideo) != 0)
{
    Console.Error.WriteLine($"SDL_Init failed: {sdl.GetErrorS()}");
    return 1;
}

unsafe
{
    var window = sdl.CreateWindow("CupriFace touch probe", Sdl.WindowposCentered, Sdl.WindowposCentered,
        900, 600, (uint)WindowFlags.Resizable);
    if (window is null) { Console.Error.WriteLine($"SDL_CreateWindow failed: {sdl.GetErrorS()}"); return 1; }

    var renderer = sdl.CreateRenderer(window, -1, (uint)RendererFlags.Software);
    if (renderer is null) { Console.Error.WriteLine($"SDL_CreateRenderer failed: {sdl.GetErrorS()}"); return 1; }

    int w, h;
    sdl.GetWindowSize(window, &w, &h);

    // THE FIRST ANSWER, before a finger is laid on the screen. SDL enumerates touch devices at init;
    // zero here means SDL cannot see the touchscreen at all, and no amount of event handling in
    // SdlSoftwareWindow would have helped. That alone decides between options 1 and 2 in the issue.
    var devices = sdl.GetNumTouchDevices();
    Console.WriteLine($"[probe] SDL video driver : {sdl.GetCurrentVideoDriverS()}");
    Console.WriteLine($"[probe] touch devices    : {devices}"
        + (devices == 0 ? "   <<< SDL SEES NO TOUCHSCREEN — read the note at the end" : ""));
    for (var i = 0; i < devices; i++)
        Console.WriteLine($"[probe]   device {i}: id={sdl.GetTouchDevice(i)}");
    Console.WriteLine($"[probe] window           : {w}x{h}");
    Console.WriteLine($"[probe] touch->mouse hint: {(mouseHint ? "requested ON" : "platform default")}");
    Console.WriteLine($"[probe] finger routing   : {(routeFingers ? "on" : "off (--raw)")}");
    Console.WriteLine("[probe] Tap the coloured boxes. Ctrl+C to finish.");
    Console.WriteLine();

    using var doc = CupriDocument.Load(Html, Css);
    doc.Refresh();

    var texture = sdl.CreateTexture(renderer, Sdl.PixelformatAbgr8888,
        (int)TextureAccess.Streaming, w, h);

    var running = true;
    var fingerEvents = 0;
    var mouseEvents = 0;
    var syntheticMouse = 0;
    var hits = 0;
    var lastMark = (X: -1f, Y: -1f);

    // SDL reports a touch-synthesised mouse event with which == SDL_TOUCH_MOUSEID (unsigned -1).
    // Counting those separately is what makes "taps arrive twice" visible instead of mysterious.
    const uint TouchMouseId = 0xFFFFFFFFu;

    var e = new Event();
    while (running)
    {
        while (sdl.PollEvent(ref e) != 0)
        {
            switch ((EventType)e.Type)
            {
                case EventType.Quit:
                    running = false;
                    break;

                case EventType.Windowevent when (WindowEventID)e.Window.Event == WindowEventID.SizeChanged:
                    sdl.GetWindowSize(window, &w, &h);
                    sdl.DestroyTexture(texture);
                    texture = sdl.CreateTexture(renderer, Sdl.PixelformatAbgr8888,
                        (int)TextureAccess.Streaming, w, h);
                    Console.WriteLine($"[size ] {w}x{h}");
                    break;

                case EventType.Fingerdown:
                case EventType.Fingermotion:
                case EventType.Fingerup:
                {
                    fingerEvents++;
                    var t = e.Tfinger;
                    // SDL finger coordinates are NORMALISED 0..1, not pixels — one of the two easy
                    // ways to get this wrong. (The other is the device scale, which this probe does
                    // not apply because it asks SDL for the window size in the same space.)
                    var px = t.X * w;
                    var py = t.Y * h;
                    var phase = (EventType)e.Type switch
                    {
                        EventType.Fingerdown => PointerPhase.Down,
                        EventType.Fingerup => PointerPhase.Up,
                        _ => PointerPhase.Move,
                    };
                    if ((EventType)e.Type != EventType.Fingermotion || fingerEvents % 10 == 0)
                        Console.WriteLine($"[touch] {phase,-4} touchId={t.TouchId} fingerId={t.FingerId} "
                            + $"norm=({t.X:F3},{t.Y:F3}) px=({px:F0},{py:F0}) pressure={t.Pressure:F2}");

                    if (routeFingers)
                    {
                        // The finger id is what makes two fingers two pointers. The desktop host
                        // currently hardcodes pointer 0 for the mouse, so a real fix has to carry
                        // this through rather than reuse that constant.
                        var pointerId = unchecked((int)t.FingerId) & 0x7FFFFFFF;
                        var handled = doc.DispatchPointer(pointerId, phase, px, py);
                        if (phase == PointerPhase.Down)
                        {
                            var clicked = handled || doc.DispatchClick(px, py);
                            if (clicked) hits++;
                            lastMark = (px, py);
                            Console.WriteLine($"[route] pointer={pointerId} -> "
                                + (clicked ? "HIT an element" : "hit nothing")
                                + $"   (hits so far: {hits})");
                        }
                    }
                    break;
                }

                case EventType.Mousebuttondown:
                case EventType.Mousemotion:
                case EventType.Mousebuttonup:
                {
                    mouseEvents++;
                    var which = (EventType)e.Type == EventType.Mousemotion ? e.Motion.Which : e.Button.Which;
                    var synthetic = which == TouchMouseId;
                    if (synthetic) syntheticMouse++;
                    if ((EventType)e.Type != EventType.Mousemotion)
                    {
                        var mx = e.Button.X; var my = e.Button.Y;
                        Console.WriteLine($"[mouse] {(EventType)e.Type} at ({mx},{my}) which={which}"
                            + (synthetic ? "  <<< SYNTHESISED FROM TOUCH" : "  (real pointing device)"));
                        // Routed as well as reported. If this machine delivers taps ONLY as
                        // synthesised mouse events, the boxes must still respond — otherwise the run
                        // reads as "the tap missed" when the truth is "the tap arrived by the other
                        // door". It also makes the probe verifiable on a machine with a mouse and no
                        // touchscreen, which is where it gets written.
                        if (routeFingers && (EventType)e.Type == EventType.Mousebuttondown)
                        {
                            var clicked = doc.DispatchPointer(0, PointerPhase.Down, mx, my)
                                          || doc.DispatchClick(mx, my);
                            if (clicked) hits++;
                            lastMark = (mx, my);
                            Console.WriteLine($"[route] mouse -> " + (clicked ? "HIT an element" : "hit nothing")
                                + $"   (hits so far: {hits})");
                        }
                        if (routeFingers && (EventType)e.Type == EventType.Mousebuttonup)
                            doc.DispatchPointer(0, PointerPhase.Up, mx, my);
                    }
                    break;
                }
            }
        }

        var pixels = doc.RenderToPixels(w, h, new SkiaSharp.SKColor(0x10, 0x14, 0x18));
        // A marker where the last press landed: the cheapest way to see a coordinate-space mistake,
        // which would otherwise look exactly like "the tap missed".
        if (lastMark.X >= 0) Mark(pixels, w, h, (int)lastMark.X, (int)lastMark.Y);
        fixed (byte* p = pixels) sdl.UpdateTexture(texture, null, p, w * 4);
        sdl.RenderClear(renderer);
        sdl.RenderCopy(renderer, texture, null, null);
        sdl.RenderPresent(renderer);
        sdl.Delay(16);
    }

    Console.WriteLine();
    Console.WriteLine($"[probe] finger events {fingerEvents}, mouse events {mouseEvents} "
        + $"(of which {syntheticMouse} synthesised from touch), element hits {hits}");
    sdl.DestroyTexture(texture);
    sdl.DestroyRenderer(renderer);
    sdl.DestroyWindow(window);
}
sdl.Quit();
return 0;

static void Mark(byte[] rgba, int w, int h, int cx, int cy)
{
    for (var y = Math.Max(0, cy - 14); y < Math.Min(h, cy + 14); y++)
    for (var x = Math.Max(0, cx - 14); x < Math.Min(w, cx + 14); x++)
    {
        var dx = x - cx; var dy = y - cy;
        if (dx * dx + dy * dy > 196) continue;
        var i = (y * w + x) * 4;
        rgba[i] = 0xFF; rgba[i + 1] = 0x00; rgba[i + 2] = 0xAA; rgba[i + 3] = 0xFF;
    }
}

static bool Args(string flag) => Environment.GetCommandLineArgs().Contains(flag);

partial class Program
{
    private const string Html = """
        <body>
          <div class="title">CupriFace touch probe — tap the boxes</div>
          <div class="row">
            <div class="box a">ONE</div>
            <div class="box b">TWO</div>
            <div class="box c">THREE</div>
          </div>
          <div class="hint">Every event is printed to the terminal. A magenta dot marks the last
          press — if the dot is not under your finger, the coordinate mapping is wrong rather than
          the tap.</div>
        </body>
        """;

    private const string Css = """
        body { font-family:sans-serif; background:#101418; padding:24px; }
        .title { color:#f5b301; font-size:22px; font-weight:bold; margin-bottom:20px; }
        .row { display:flex; gap:18px; }
        .box { flex:1; height:180px; border-radius:14px; color:#101418; font-size:26px;
               font-weight:bold; padding:16px; }
        .a { background:#f5b301; }
        .b { background:#5bc8a8; }
        .c { background:#8b9ff5; }
        .hint { color:#8b93a7; font-size:14px; margin-top:22px; }
        """;
}
