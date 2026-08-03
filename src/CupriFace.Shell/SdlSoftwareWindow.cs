using System.Diagnostics;
using Silk.NET.SDL;
using SkiaSharp;

namespace CupriFace.Shell;

/// <summary>
/// Cross-platform no-GPU window (DESIGN.md §7.5). Renders to a CPU <see cref="SKBitmap"/>
/// and presents it through SDL's *software* renderer (a streaming texture), so it needs
/// no OpenGL — works on Windows, macOS, and Linux, including over remote sessions. This is
/// the sole CPU present path: it reaches SDL through managed Silk.NET bindings, so the
/// project ships **no hand-written P/Invoke** (only the `unsafe` pointers the SDL API needs).
/// </summary>
public sealed unsafe class SdlSoftwareWindow : IDisposable
{
    // SDL_PIXELFORMAT_ARGB8888 (0xAARRGGBB in a u32 → B,G,R,A in memory ⇒ Bgra8888).
    private const uint PixelFormatArgb8888 = 0x16362004;

    private readonly Sdl _sdl = Sdl.GetApi();
    private readonly string _title;
    private readonly FrameStats _stats = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _last;

    private int _width, _height;
    private Window* _window;
    private Renderer* _renderer;
    private Texture* _texture;
    private SKBitmap? _bitmap;
    private SKCanvas? _canvas;
    private EventFilter? _resizeWatch; // kept alive: fires during the OS modal resize loop

    public event Action<RenderContext>? Render;
    public event Action<float, float>? PointerDown;
    public event Action<float, float>? PointerMove;
    public event Action<float, float>? PointerUp;
    public FrameStats Stats => _stats;

    public SdlSoftwareWindow(string title = "CupriFace", int width = 1024, int height = 768)
    {
        _title = title;
        _width = width;
        _height = height;
    }

    public void Run()
    {
        if (_sdl.Init(Sdl.InitVideo) != 0)
            throw new InvalidOperationException($"SDL_Init failed: {_sdl.GetErrorS()}");

        _window = _sdl.CreateWindow(_title, Sdl.WindowposCentered, Sdl.WindowposCentered,
            _width, _height, (uint)WindowFlags.Resizable);
        if (_window is null) throw new InvalidOperationException($"SDL_CreateWindow failed: {_sdl.GetErrorS()}");

        _renderer = _sdl.CreateRenderer(_window, -1, (uint)RendererFlags.Software);
        if (_renderer is null) throw new InvalidOperationException($"SDL_CreateRenderer failed: {_sdl.GetErrorS()}");

        EnsureSurface(_width, _height);

        // Repaint DURING resize: Windows/macOS run a modal loop that blocks the main loop,
        // but SDL still dispatches size events to an event watch — so we render from there.
        _resizeWatch = ResizeWatch;
        _sdl.AddEventWatch(new PfnEventFilter(_resizeWatch), null);

        var running = true;
        var e = new Event();
        while (running)
        {
            while (_sdl.PollEvent(ref e) != 0)
            {
                switch ((EventType)e.Type)
                {
                    case EventType.Quit:
                        running = false;
                        break;
                    case EventType.Mousebuttondown:
                        PointerDown?.Invoke(e.Button.X, e.Button.Y);
                        break;
                    case EventType.Mousebuttonup:
                        PointerUp?.Invoke(e.Button.X, e.Button.Y);
                        break;
                    case EventType.Mousemotion:
                        PointerMove?.Invoke(e.Motion.X, e.Motion.Y);
                        break;
                    case EventType.Windowevent when (WindowEventID)e.Window.Event == WindowEventID.SizeChanged:
                        EnsureSurface(e.Window.Data1, e.Window.Data2);
                        break;
                }
            }
            RenderFrame();
            _sdl.Delay(16); // ~60 fps cap
        }
    }

    private int ResizeWatch(void* userData, Event* e)
    {
        if ((EventType)e->Type == EventType.Windowevent && (WindowEventID)e->Window.Event == WindowEventID.SizeChanged)
        {
            EnsureSurface(e->Window.Data1, e->Window.Data2);
            RenderFrame();
        }
        return 0;
    }

    private void EnsureSurface(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        _width = w; _height = h;
        _canvas?.Dispose();
        _bitmap?.Dispose();
        _bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        _canvas = new SKCanvas(_bitmap);
        if (_texture is not null) _sdl.DestroyTexture(_texture);
        _texture = _sdl.CreateTexture(_renderer, PixelFormatArgb8888, (int)TextureAccess.Streaming, w, h);
    }

    private void RenderFrame()
    {
        if (_canvas is null || _bitmap is null) return;

        var delta = _clock.Elapsed.TotalSeconds - _last;
        _last = _clock.Elapsed.TotalSeconds;
        _stats.BeginFrame(delta);
        Render?.Invoke(new RenderContext(_canvas, _width, _height, _stats));
        _canvas.Flush();
        _stats.EndFrame();

        _sdl.UpdateTexture(_texture, null, (void*)_bitmap.GetPixels(), _width * 4);
        _sdl.RenderClear(_renderer);
        _sdl.RenderCopy(_renderer, _texture, null, null);
        _sdl.RenderPresent(_renderer);
    }

    public void Dispose()
    {
        if (_texture is not null) _sdl.DestroyTexture(_texture);
        if (_renderer is not null) _sdl.DestroyRenderer(_renderer);
        if (_window is not null) _sdl.DestroyWindow(_window);
        _sdl.Quit();
        _canvas?.Dispose();
        _bitmap?.Dispose();
    }
}
