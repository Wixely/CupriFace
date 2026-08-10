using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CupriFace;
using CupriFace.Interaction;
using CupriFace.Style;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CupriFace.Samples.Viewer;

/// <summary>
/// Serves a <see cref="CupriApp"/> to a browser over Kestrel — the engine keeps running in THIS
/// process (no WebAssembly, no build step): each frame is rendered server-side with
/// <c>doc.Render</c>, encoded as PNG and pushed down a WebSocket, and the browser sends pointer and
/// key events back. It is the same app, the same document pipeline and the same render-on-demand
/// loop as <c>DesktopHost</c>; only the surface and the transport differ.
///
/// Contrast with <c>samples/WebWasm</c>, which ships the engine INTO the browser. This host is the
/// thin-client counterpart: nothing is downloaded but pixels, so it also works as a way to view a
/// running desktop app remotely.
///
/// Each connection gets its own document (so two tabs can be different sizes) but they share the
/// app's model object, so model state — the current section, dark mode — is common to all viewers.
/// </summary>
public static class WebViewerHost
{
    public static void Run(CupriApp app, int port, bool openBrowser)
    {
        var builder = WebApplication.CreateSlimBuilder();          // slim: AOT-friendly, no MVC/DI weight
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");       // loopback only — this is a dev viewer
        builder.Logging.ClearProviders();                          // the request log would drown the app's own output
        var web = builder.Build();

        var page = ReadEmbedded("WebClient.html");
        web.UseWebSockets();
        web.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));
        web.Map("/ws", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await new Session(app, socket).RunAsync(ctx.RequestAborted);
        });

        var url = $"http://127.0.0.1:{port}/";
        Console.WriteLine($"[CupriFace] {app.Title} — serving at {url}  (Ctrl+C to stop)");
        if (openBrowser) OpenBrowser(url);
        web.Run();
    }

    private static string ReadEmbedded(string name)
    {
        // Plain manifest resource rather than the Assets/ generator: this is one file in a sample,
        // and it keeps the Viewer free of the resource-generator wiring.
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded resource '{name}' is missing");
        return new StreamReader(s).ReadToEnd();
    }

    private static void OpenBrowser(string url)
    {
        // UseShellExecute lets each OS pick its handler (explorer / open / xdg-open). Never fatal:
        // the URL is on stdout regardless.
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Console.WriteLine($"[CupriFace] couldn't open a browser ({ex.GetType().Name}); open {url} yourself."); }
    }

    /// <summary>One connected browser: owns a document, drains input, and pushes frames when — and
    /// only when — something actually changed.</summary>
    private sealed class Session(CupriApp app, WebSocket socket)
    {
        private readonly CupriDocument _doc = app.CreateDocument();
        private readonly ConcurrentQueue<JsonElement> _input = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private int _cssW = 940, _cssH = 720;
        private float _dpr = 1f, _scale = 1f;
        private bool _dirty = true;
        private double _lastRefresh;
        private CursorType _cursor = CursorType.Default;
        private readonly ConcurrentQueue<string> _outbox = new();  // cursor/navigate messages

        public async Task RunAsync(CancellationToken ct)
        {
            // JsonEncodedText, not JsonSerializer: the serializer is RequiresDynamicCode (IL3050) and
            // would make this sample the one thing that breaks a NativeAOT publish. All we need is
            // string escaping, which JsonEncodedText does without reflection.
            _doc.Navigated += e => { if (e.External) _outbox.Enqueue($"{{\"t\":\"open\",\"href\":\"{JsonEncodedText.Encode(e.Href)}\"}}"); };
            var reader = ReceiveLoop(ct);
            try { await RenderLoop(ct); }
            finally { _doc.Dispose(); }
            await reader;
        }

        // Reads client messages onto a queue. Never touches the document: the engine is
        // single-threaded, so every dispatch happens on the render loop below.
        private async Task ReceiveLoop(CancellationToken ct)
        {
            var buffer = new byte[8 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.Count == 0) continue;
                    try { _input.Enqueue(JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone()); }
                    catch (JsonException) { /* a malformed frame should not kill the session */ }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { /* browser closed the tab */ }
        }

        private async Task RenderLoop(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                await timer.WaitForNextTickAsync(ct);
                while (_input.TryDequeue(out var msg)) Handle(msg);
                while (_outbox.TryDequeue(out var text))
                    await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
                if (!NeedsRender()) continue;
                var png = RenderFrame();
                await socket.SendAsync(png, WebSocketMessageType.Binary, true, ct);
            }
        }

        // Same wake-up rules as the desktop host: input that changed something, a periodic re-bind,
        // an image finishing, or a running animation. A still page sends nothing at all.
        private bool NeedsRender()
        {
            if (app.RefreshIntervalSeconds > 0 && _clock.Elapsed.TotalSeconds - _lastRefresh >= app.RefreshIntervalSeconds)
            {
                _lastRefresh = _clock.Elapsed.TotalSeconds;
                _doc.Refresh();
                _dirty = true;
            }
            if (_doc.ConsumeImageArrived()) _dirty = true;
            if (_doc.HasActiveAnimations) _dirty = true;
            var d = _dirty;
            _dirty = false;
            return d;
        }

        private byte[] RenderFrame()
        {
            var p = app.Present(_cssW, _cssH);
            _scale = p.Scale <= 0 ? 1f : p.Scale;
            if (_doc.HasAnimations || _doc.HasActiveTransitions) _doc.Animate(_clock.Elapsed.TotalSeconds);

            // Device pixels = CSS pixels x devicePixelRatio; the present scale rides on top, exactly
            // as the desktop host composes them.
            var info = new SKImageInfo(Math.Max(1, (int)(_cssW * _dpr)), Math.Max(1, (int)(_cssH * _dpr)),
                                       SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(app.Background);
            canvas.Save();
            canvas.Scale(_dpr * _scale);
            _doc.Render(canvas, p.LogicalWidth, p.LogicalHeight);
            canvas.Restore();
            canvas.Flush();

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }

        private void Handle(JsonElement m)
        {
            var type = m.TryGetProperty("t", out var t) ? t.GetString() : null;
            float F(string name) => m.TryGetProperty(name, out var v) ? (float)v.GetDouble() / _scale : 0f;
            int I(string name) => m.TryGetProperty(name, out var v) ? v.GetInt32() : 0;

            switch (type)
            {
                case "size":
                    _cssW = Math.Max(1, I("w"));
                    _cssH = Math.Max(1, I("h"));
                    _dpr = m.TryGetProperty("dpr", out var d) ? Math.Clamp((float)d.GetDouble(), 1f, 2f) : 1f;
                    _dirty = true;
                    break;
                case "down": Mark(_doc.DispatchClick(F("x"), F("y"), I("n"))); SendCursor(F("x"), F("y")); break;
                case "move": Mark(_doc.DispatchPointerMove(F("x"), F("y"))); SendCursor(F("x"), F("y")); break;
                case "up": Mark(_doc.DispatchPointerUp(F("x"), F("y"))); SendCursor(F("x"), F("y")); break;
                case "ctx": Mark(_doc.DispatchContextMenu(F("x"), F("y"))); break;
                // Browser deltaY is already pixels, positive-down — no negation (the WASM hosts
                // copied desktop's notch inversion here and scrolled backwards for it).
                case "wheel": Mark(_doc.DispatchWheel(F("x"), F("y"), m.GetProperty("dy").GetSingle())); break;
                case "char": Mark(_doc.DispatchKey(m.GetProperty("s").GetString(), EditKey.None, (KeyMods)I("m"))); break;
                case "key": Mark(_doc.DispatchKey(null, (EditKey)I("k"), (KeyMods)I("m"))); break;
            }
        }

        private void Mark(bool changed) { if (changed) _dirty = true; }

        private void SendCursor(float x, float y)
        {
            var c = _doc.CursorAt(x, y);
            if (c == _cursor) return;
            _cursor = c;
            _outbox.Enqueue($"{{\"t\":\"cursor\",\"v\":\"{Css(c)}\"}}");
        }

        private static string Css(CursorType c) => c switch
        {
            CursorType.Pointer => "pointer",
            CursorType.Text => "text",
            CursorType.Wait => "wait",
            CursorType.Progress => "progress",
            CursorType.Help => "help",
            CursorType.Crosshair => "crosshair",
            CursorType.Move => "move",
            CursorType.NotAllowed => "not-allowed",
            CursorType.Grab => "grab",
            CursorType.Grabbing => "grabbing",
            CursorType.EwResize => "ew-resize",
            CursorType.NsResize => "ns-resize",
            CursorType.NeswResize => "nesw-resize",
            CursorType.NwseResize => "nwse-resize",
            CursorType.None => "none",
            _ => "default",
        };
    }
}
