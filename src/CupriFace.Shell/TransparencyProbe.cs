using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CupriFace.Shell;

/// <summary>
/// After a transparent window has been on screen for a moment, ask the compositor what it actually
/// did with it — and say so if the answer is "painted it black".
///
/// <para><b>Why this exists.</b> On some Windows display paths a transparent framebuffer composites
/// with every alpha-zero pixel opaque black (#139, matching the still-open glfw/glfw#2815). The
/// window is drawn correctly, the GPU readback holds the right alpha, and the desktop shows a black
/// rectangle. Nothing in the process notices, because nothing in the process ever looks at the
/// composited result — so an app that asked for transparency and did not get it had no way to find
/// out except by someone looking at the screen.</para>
///
/// <para>The acceptance on that issue asks for exactly this: <i>"the shell should detect/report the
/// limitation rather than silently replacing transparency with black"</i>.</para>
///
/// <para><b>What it is not.</b> Not a fix, and not proof of health when it stays quiet. It samples
/// the screen where the window is and counts pixels that are <i>exactly</i> black, which is the
/// signature that failure leaves (37.39% of the reporting machine's window, 0% on every working one
/// measured). An app whose own design is a large field of pure black over a dark desktop can trip
/// it; that is why it names what it measured and how to silence it rather than asserting a
/// fault.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class TransparencyProbe
{
    /// <summary>Above this share of exactly-black pixels the result is worth reporting. The failure
    /// signature is a third of the window or more; a healthy one measured zero. The gap is wide, so
    /// the threshold sits well inside it rather than at either edge.</summary>
    private const double BlackShareToReport = 0.15;

    private static bool _done;

    /// <summary>
    /// Run once, for a transparent window, a few frames after it first painted. Earlier than that
    /// and the compositor has not necessarily shown anything yet, which would measure the desktop
    /// rather than the window.
    /// </summary>
    /// <param name="hwnd">The window to look at.</param>
    /// <param name="transparent">Whether transparency was actually asked for — opaque windows are
    /// black wherever they are drawn black, and have nothing to report.</param>
    public static void CheckOnce(nint hwnd, bool transparent, bool hasPresented)
    {
        if (_done || !OperatingSystem.IsWindows()) return;
        _done = true;
        // A DIAGNOSTIC MUST NOT BE ABLE TO STOP THE APP. This runs inside the render loop, so
        // anything thrown here — a GDI call refused in a locked session, a marshalling mistake of
        // mine — would propagate into the host's loop and take the window's rendering with it. That
        // is precisely what happened the first time this ran: the loop stopped after exactly one
        // pass, and the failure looked like "the probe never ran" rather than "the probe threw".
        try { Run(hwnd, transparent, hasPresented); }
        catch (Exception ex) { Say($"[CupriFace] transparency probe failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void Run(nint hwnd, bool transparent, bool hasPresented)
    {

        var verbose = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUPRIFACE_ALPHA_PROBE"));
        if (Env("CUPRIFACE_QUIET_TRANSPARENCY") && !verbose) return;

        // The conditions are checked HERE rather than at the call site so that a run which reaches
        // no conclusion can say which one stopped it. A probe that is silent both when everything
        // is fine and when it never ran is not a diagnostic.
        if (!transparent || hwnd == 0 || !hasPresented)
        {
            if (verbose)
                Say($"[CupriFace] transparency probe: not run (transparent={transparent}, "
                    + $"hwnd={(hwnd == 0 ? "none" : "ok")}, presented={hasPresented}).");
            return;
        }

        // ONLY A WINDOW SOMEONE CAN SEE. Under the layered-GPU bypass this GL window still exists as
        // an off-screen context while a different window is what reaches the screen — so measuring
        // it would report whatever happens to lie at its coordinates and call that a compositor
        // fault. A false alarm on the path that WORKS is worse than staying quiet, because it tells
        // someone to abandon the thing that is fixing their problem.
        if (!IsWindowVisible(hwnd))
        {
            if (verbose)
                Say($"[CupriFace] transparency probe: skipped — hwnd 0x{hwnd:X} is not visible, so it "
                    + "is not what reaches the screen (this is normal under layered-GPU presentation, "
                    + "where the GL window is an off-screen context).");
            return;
        }

        var measured = Measure(hwnd);
        if (measured is not { } share)
        {
            if (verbose) Say("[CupriFace] transparency probe: could not read the screen (locked "
                             + "session, or the window has no area). No conclusion either way.");
            return;
        }

        // The window it actually looked at, named. Without this a reader cannot tell a run of the
        // default GL path from a run of the layered bypass — the two leave their output in the same
        // console, and a line that says only a percentage is indistinguishable between them. That
        // ambiguity cost a round trip the first time this was used in anger.
        GetWindowRect(hwnd, out var box);
        Say($"[CupriFace] transparency probe: {share:P2} of hwnd 0x{hwnd:X} "
            + $"({box.Right - box.Left}x{box.Bottom - box.Top} at {box.Left},{box.Top}, "
            + $"visible={IsWindowVisible(hwnd)}) is exactly black. Presentation: GL/GLFW window. "
            + $"Report threshold {BlackShareToReport:P0}.");

        if (share < BlackShareToReport) return;

        Say(
            $"[CupriFace] TRANSPARENCY LOOKS BROKEN ON THIS DISPLAY: {share:P1} of this window's "
            + "area composited to exactly black, which is what happens when the desktop compositor "
            + "treats a transparent framebuffer's alpha as undefined (issue #139, and the still-open "
            + "glfw/glfw#2815). The app drew the right pixels; the compositor did not use them.\n"
            + "  Workaround: DesktopHost.RunWithLayeredGpu(app) keeps GPU drawing and presents with "
            + "UpdateLayeredWindow, or DesktopHost.Run(app, preferSoftware: true) for CPU drawing. "
            + "Both are verified on the affected hardware.\n"
            + "  If your own design really is a large field of pure black, this is a false alarm — "
            + "set CUPRIFACE_QUIET_TRANSPARENCY=1 to silence it.");
    }

    /// <summary>
    /// The share of the window's screen area that is exactly black, or null when the screen could
    /// not be read (a locked session, or a window with no area).
    ///
    /// <para><b>One blit, not a pixel at a time.</b> The obvious implementation — <c>GetPixel</c>
    /// over a sample grid — round-trips to the display driver per call, and on a REMOTE OR VIRTUAL
    /// display that is pathologically slow: the first version of this hung the render loop for
    /// longer than the app was alive. Which is a bitter joke, because a virtual display adapter is
    /// exactly the hardware this probe exists to diagnose. So the rectangle is copied into a memory
    /// bitmap once and the bits are read from there.</para>
    /// </summary>
    private static double? Measure(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;

        var screen = GetDC(0);
        if (screen == 0) return null;
        nint mem = 0, bmp = 0, old = 0;
        try
        {
            mem = CreateCompatibleDC(screen);
            if (mem == 0) return null;
            bmp = CreateCompatibleBitmap(screen, w, h);
            if (bmp == 0) return null;
            old = SelectObject(mem, bmp);
            if (!BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, SrcCopy)) return null;

            var info = new BitmapInfo
            {
                Size = 40, Width = w, Height = -h,      // negative: top-down, so rows read in order
                Planes = 1, BitCount = 32, Compression = 0,
            };
            var pixels = new int[w * h];
            if (GetDIBits(mem, bmp, 0, (uint)h, pixels, ref info, 0) == 0) return null;

            var black = 0;
            foreach (var px in pixels) if ((px & 0x00FFFFFF) == 0) black++;
            return (double)black / pixels.Length;
        }
        finally
        {
            if (old != 0) SelectObject(mem, old);
            if (bmp != 0) DeleteObject(bmp);
            if (mem != 0) DeleteDC(mem);
            ReleaseDC(0, screen);
        }
    }

    private static bool Env(string name) =>
        Environment.GetEnvironmentVariable(name) is "1" or "true" or "TRUE";

    /// <summary>
    /// Say it on the console, and — when a path is given — in a file as well.
    ///
    /// <para>A console line is easy to lose: a windowed app may have no console attached, and a
    /// host that redirects one can drop buffered output when the process is killed rather than
    /// closed. Someone reproducing a compositor bug should not also have to fight that, so
    /// <c>CUPRIFACE_ALPHA_PROBE=&lt;path&gt;</c> writes the same lines somewhere they can be read
    /// afterwards and attached to an issue.</para>
    /// </summary>
    private static bool _saidWhere;

    private static void Say(string message)
    {
        Console.WriteLine(message);
        Console.Out.Flush();

        var path = LogPath();
        if (path is null) return;
        try
        {
            // Where it is writing, once, on the console — so "nothing was appended" can never be a
            // mystery. The first version silently wrote nowhere when the variable was set to `1`,
            // which is the obvious thing to set, and the missing file looked like a failure of the
            // probe rather than of its own plumbing.
            if (!_saidWhere) { _saidWhere = true; Console.WriteLine($"[CupriFace] probe log: {path}"); }
            File.AppendAllText(path, message + Environment.NewLine);
        }
        catch (IOException e) { Console.WriteLine($"[CupriFace] probe log unavailable ({e.Message})."); }
        catch (UnauthorizedAccessException e) { Console.WriteLine($"[CupriFace] probe log unavailable ({e.Message})."); }
        catch (ArgumentException e) { Console.WriteLine($"[CupriFace] probe log path rejected ({e.Message})."); }
    }

    /// <summary>Where the probe writes its lines, or null when it was not asked to. <c>=1</c> means
    /// "yes please" rather than a path, so it gets a sensible default instead of nowhere.</summary>
    private static string? LogPath()
    {
        var v = Environment.GetEnvironmentVariable("CUPRIFACE_ALPHA_PROBE");
        if (string.IsNullOrEmpty(v)) return null;
        return v is "1" or "true" or "TRUE"
            ? Path.Combine(Path.GetTempPath(), "cupriface-alpha-probe.txt")
            : v;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hwnd, nint hdc);

    private const uint SrcCopy = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint hdc, nint bmp, uint start, uint lines,
                                         [Out] int[] bits, ref BitmapInfo info, uint usage);
}
