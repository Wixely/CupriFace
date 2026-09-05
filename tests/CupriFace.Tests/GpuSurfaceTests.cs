using CupriFace.Paint;
using SkiaSharp;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// The <see cref="IGpuSurfaceSource"/> seam, from the side a test without a GPU can reach.
///
/// <para>The zero-copy path itself needs a real <see cref="GRContext"/>, which needs a real GL
/// context, which this suite does not have — it is verified in the sample instead, where the log
/// names the path and a frame dump shows the model actually composited. What IS testable here is
/// the property that matters more widely: a producer offering the GPU path must behave exactly like
/// an ordinary surface everywhere the GPU path is not available. Every web host, every Android host
/// and any desktop host that fell back to a software window is in that category, so a producer that
/// broke them to gain a texture would be a bad trade.</para>
/// </summary>
public class GpuSurfaceTests
{
    private sealed class GpuSource : IGpuSurfaceSource
    {
        public int RenderCalls;
        public SKImage? CurrentFrame { get; set; }
        public (int W, int H)? NaturalSize => (64, 64);
        public bool Ticking => true;
        public void RenderOnGpu(GRContext context) => RenderCalls++;
    }

    private sealed class PlainSource : ISurfaceSource
    {
        public SKImage? CurrentFrame => null;
        public (int W, int H)? NaturalSize => (32, 32);
        public bool Ticking => false;
    }

    [Fact]
    public void A_host_without_a_gpu_never_marks_the_hook_and_never_calls_the_producer()
    {
        var r = new SurfaceRegistry();
        var gpu = new GpuSource();
        r.Register("v", gpu);

        // This is the whole contract for a CPU host: it simply never calls RenderGpuFrames.
        Assert.False(r.HasGpuFrameHook);
        Assert.Equal(0, gpu.RenderCalls);

        // …and the surface is still an ordinary surface in every other respect.
        Assert.Same(gpu, r.Get("v"));
        Assert.True(r.AnyTicking);
        Assert.Equal((64, 64), r.Get("v")!.NaturalSize);
    }

    /// <summary>A GPU producer must not change how the engine treats the rest: it is host-composited
    /// only if it says so, and it has no underlay element unless it asks for one. Both default, and
    /// a producer that accidentally flipped either would punch a hole or spawn a canvas.</summary>
    [Fact]
    public void A_gpu_producer_keeps_every_other_default()
    {
        // Through the interface: both are default members, so a producer that never mentions them
        // gets the defaults — which is exactly what is being asserted.
        ISurfaceSource gpu = new GpuSource();
        Assert.False(gpu.HostComposited);
        Assert.Null(gpu.UnderlayElement);
    }

    [Fact]
    public void A_plain_source_is_not_mistaken_for_a_gpu_one()
    {
        var r = new SurfaceRegistry();
        r.Register("p", new PlainSource());
        Assert.IsNotAssignableFrom<IGpuSurfaceSource>(r.Get("p"));
        Assert.False(r.HasGpuFrameHook);
    }

    [Fact]
    public void RenderGpuFrames_refuses_a_null_context_rather_than_half_running()
    {
        var r = new SurfaceRegistry();
        r.Register("v", new GpuSource());
        Assert.Throws<ArgumentNullException>(() => r.RenderGpuFrames(null!));
    }
}
