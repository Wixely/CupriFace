using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

// #212: present exactly N synthetic frames, then keep pumping events without swapping.
// No engine, Skia, animation, input integration, or framebuffer recreation is involved.
internal static class AlphaFrames
{
    public static void Run(int count, bool topMost)
    {
        using var window = Window.Create(WindowOptions.Default with
        {
            Title = "CupriFace alpha frame probe",
            Size = new Vector2D<int>(510, 336),
            Position = new Vector2D<int>(100, 100),
            API = GraphicsAPI.Default,
            TransparentFramebuffer = true,
            WindowBorder = WindowBorder.Hidden,
            TopMost = topMost,
            ShouldSwapAutomatically = false,
            VSync = true,
        });
        GL? gl = null;
        var presented = 0;
        window.Load += () =>
        {
            gl = GL.GetApi(window);
            Console.WriteLine($"alpha frames: renderer={gl.GetStringS(StringName.Renderer)}; target={count}");
        };
        window.Render += _ =>
        {
            if (presented >= count) { Thread.Sleep(8); return; }
            var size = window.FramebufferSize;
            gl!.Disable(EnableCap.ScissorTest);
            gl.ClearColor(0, 0, 0, 0);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.Enable(EnableCap.ScissorTest);
            gl.Scissor(24, 24, (uint)Math.Max(1, size.X - 48), (uint)Math.Max(1, size.Y - 48));
            // Premultiplied #12141ad9, the same panel colour as the HUD.
            const float alpha = 217f / 255;
            gl.ClearColor(18f / 255 * alpha, 20f / 255 * alpha, 26f / 255 * alpha, alpha);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.Disable(EnableCap.ScissorTest);
            var corner = Read(gl, 2, 2);
            var panel = Read(gl, size.X / 2, size.Y / 2);
            var error = gl.GetError();
            if (error != GLEnum.NoError)
                throw new InvalidOperationException($"Alpha probe OpenGL error: {error}");
            window.SwapBuffers();
            presented++;
            Console.WriteLine($"alpha frames: swaps={presented}; framebuffer={size.X}x{size.Y}; corner={corner}; panel={panel}");
            Console.Out.Flush();
        };
        try { window.Run(); }
        finally { gl?.Dispose(); }
        Console.WriteLine($"alpha frames: closed after {presented} swaps (target {count}).");
    }

    private static unsafe string Read(GL gl, int x, int y)
    {
        byte* pixel = stackalloc byte[4];
        gl.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel);
        return $"{pixel[0]},{pixel[1]},{pixel[2]},{pixel[3]}";
    }
}
