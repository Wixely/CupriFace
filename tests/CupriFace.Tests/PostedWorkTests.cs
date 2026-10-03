using CupriFace.Interaction;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <c>doc.Post</c> — the one member of a document safe to call from another thread.
///
/// <para>Input of any kind ends in a dispatch that REBUILDS the render tree, so a reader thread
/// calling one directly is replacing the tree underneath layout and paint. That is not a theoretical
/// hazard: it is what a Linux evdev reader feeding a <see cref="GamepadDriver"/> does, and it was
/// reported from a real integration. The failure mode is the expensive one — no exception at the
/// call site, just sporadic corruption somewhere else.</para>
/// </summary>
public class PostedWorkTests
{
    private const string Html = """
        <body>
          <div class='b' id='one' role='button'>one</div>
          <div class='b' id='two' role='button'>two</div>
          <div class='b' id='three' role='button'>three</div>
        </body>
        """;
    private const string Css = "body{margin:0} .b{width:200px;height:40px;background:#345;color:#fff}";

    /// <summary>Posted work runs on the next frame, not at the call.</summary>
    [Fact]
    public void Work_runs_on_the_next_frame_rather_than_at_the_call()
    {
        using var t = new TestDoc(Html, Css, width: 400, height: 300);
        var ran = false;
        t.Doc.Post(() => ran = true);

        Assert.False(ran, "posting must not run it here — that is the whole point");
        t.Layout();
        Assert.True(ran);
    }

    /// <summary>And its effects are visible in THAT frame, not the one after — it is drained before
    /// layout, so a posted navigation paints where it landed rather than a frame late.</summary>
    [Fact]
    public void Its_effects_land_in_the_same_frame()
    {
        using var t = new TestDoc(Html, Css, width: 400, height: 300);
        t.Key(EditKey.Tab);
        Assert.Equal("one", t.FocusedName());

        t.Doc.Post(() => t.Doc.DispatchKey(null, EditKey.Tab));
        t.Layout();                       // one frame: drain, then lay out
        Assert.Equal("two", t.FocusedName());
    }

    /// <summary>Order is preserved. A reader thread posting a stream of samples must not have them
    /// reordered — the last one is the current state of the stick.</summary>
    [Fact]
    public void Work_runs_in_the_order_it_was_posted()
    {
        using var t = new TestDoc(Html, Css, width: 400, height: 300);
        var seen = new List<int>();
        for (var i = 0; i < 50; i++) { var n = i; t.Doc.Post(() => seen.Add(n)); }

        t.Layout();
        Assert.Equal(Enumerable.Range(0, 50), seen);
    }

    /// <summary>
    /// THE ACTUAL RACE, reproduced. Twenty threads post gamepad input while the main thread renders.
    /// Without marshalling, each <c>Stick</c> rebuilds the tree underneath the layout running on this
    /// thread; with it, every sample is executed between frames and nothing overlaps.
    ///
    /// <para>A passing run is never proof of thread safety — races are like that. What makes this one
    /// worth having is what it does to the UNMARSHALLED version: swap <c>PostStick</c> for
    /// <c>Stick</c> and the test host does not fail, it <b>crashes</b>, with
    /// <c>NullReferenceException</c> and <c>ArgumentOutOfRangeException</c> thrown from several
    /// threads at once as they walk a tree being replaced underneath them. That is the shape of the
    /// bug an integrator hits, and it is why these helpers exist rather than a line of
    /// documentation.</para>
    /// </summary>
    [Fact]
    public void Many_threads_can_post_input_while_the_document_renders()
    {
        using var t = new TestDoc(Html, Css, width: 400, height: 300);
        t.Doc.ArrowNavigation = true;
        var pad = new GamepadDriver(t.Doc);
        t.Key(EditKey.Tab);

        using var start = new ManualResetEventSlim();
        var threads = new List<Thread>();
        for (var i = 0; i < 20; i++)
        {
            var th = new Thread(() =>
            {
                start.Wait();
                for (var n = 0; n < 50; n++)
                {
                    pad.PostStick(0f, 0.9f);     // the call an evdev reader makes
                    pad.PostStick(0f, 0f);
                }
            }) { IsBackground = true };
            threads.Add(th);
            th.Start();
        }

        start.Set();
        // Render throughout, which is what makes it a race rather than a queue test.
        for (var frame = 0; frame < 60; frame++) t.Layout();
        foreach (var th in threads) Assert.True(th.Join(TimeSpan.FromSeconds(10)), "a poster hung");
        t.Layout();                               // drain whatever landed last

        Assert.NotEqual("", t.FocusedName());     // the document is still coherent…
        t.Key(EditKey.Tab);                       // …and still usable
        Assert.NotEqual("", t.FocusedName());
    }

    /// <summary>
    /// POSTING WAKES A SLEEPING HOST. The queue is drained inside a frame, and a render-on-demand
    /// window draws only when something says it must — so without this the posted work waits for a
    /// frame that is itself waiting for a reason to happen, and an idle window never receives it at
    /// all. It would work in every test (which render unconditionally) and do nothing in front of a
    /// user, which is the worst combination available.
    ///
    /// <para><c>HasActiveAnimations</c> is the signal every host polls to decide whether to draw —
    /// desktop's <c>NeedsRender</c>, Android's frame request, the web host's drive gate.</para>
    /// </summary>
    [Fact]
    public void A_pending_post_wakes_a_render_on_demand_host()
    {
        using var t = new TestDoc(Html, Css, width: 400, height: 300);
        Assert.False(t.Doc.HasActiveAnimations, "nothing is pending yet");

        t.Doc.Post(() => { });
        Assert.True(t.Doc.HasActiveAnimations,
            "a host polls THIS to decide whether to draw; without it the queue is never reached");

        t.Layout();
        Assert.False(t.Doc.HasActiveAnimations, "and goes quiet once drained");
    }

    /// <summary>Posting from a thread is only useful if the work actually arrives. A sample posted
    /// before any frame is queued, not dropped.</summary>
    [Fact]
    public void Work_posted_before_a_frame_is_queued_not_dropped()
    {
        using var t = new TestDoc(Html, Css, width: 400, height: 300);
        var pad = new GamepadDriver(t.Doc);
        t.Doc.ArrowNavigation = true;

        var done = new ManualResetEventSlim();
        var th = new Thread(() => { pad.PostPress(NavigationDirection.Down); done.Set(); }) { IsBackground = true };
        th.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "the poster should not block on a frame");

        t.Layout();
        Assert.NotEqual("", t.FocusedName());
    }

    /// <summary>An exception from posted work propagates rather than being swallowed. It is
    /// application code; a silently dropped exception here would be invisible in exactly the way this
    /// engine is already forgiving enough to make dangerous.</summary>
    [Fact]
    public void An_exception_in_posted_work_is_not_swallowed()
    {
        using var t = new TestDoc(Html, Css, width: 400, height: 300);
        t.Doc.Post(() => throw new InvalidOperationException("from the reader thread"));

        var ex = Assert.Throws<InvalidOperationException>(() => t.Layout());
        Assert.Contains("reader thread", ex.Message);
    }

    [Fact]
    public void Posting_null_is_refused_at_the_call_not_at_the_frame()
    {
        using var t = new TestDoc(Html, Css, width: 400, height: 300);
        Assert.Throws<ArgumentNullException>(() => t.Doc.Post(null!));
    }
}
