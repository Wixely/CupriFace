using CupriFace.Media;
using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// <see cref="NativeCallGate"/>: no worker may be inside a native call when its object is freed.
///
/// <para>The Android player itself cannot run here (it is <c>net10.0-android</c> and needs LibVLC),
/// so these tests model the sequence that crashed it in #317 with a stand-in for the shared LibVLC
/// instance that records any use after it was freed. On a device that use is
/// <c>libvlc_retain: assertion "p_instance != NULL" failed</c> and SIGABRT; here it is a flag.</para>
///
/// <para>The sequence: a local video's prepare worker is spooling the file on the thread pool; the
/// activity is destroyed during startup, which disposes the player and then frees the shared
/// instance; the spool finishes and the worker builds its media from that instance.</para>
/// </summary>
public class NativeCallGateTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10);

    /// <summary>The shared native instance: usable until freed, and a record of any use after.</summary>
    private sealed class FakeInstance
    {
        private volatile bool _freed;
        public int UsesAfterFree;
        public int Builds;
        public void Free() => _freed = true;
        public object Build()
        {
            if (_freed) Interlocked.Increment(ref UsesAfterFree);
            Interlocked.Increment(ref Builds);
            return new object();
        }
    }

    /// <summary>The player as #317 found it, reduced to the parts that matter: a lock, the gate on
    /// it, and the media its prepare worker publishes.</summary>
    private sealed class Player
    {
        private readonly FakeInstance instance;
        public readonly object Sync = new();
        public readonly NativeCallGate Gate;
        public object? Media;
        public int MediaFreedOnPublish;

        public Player(FakeInstance instance)
        {
            this.instance = instance;
            Gate = new NativeCallGate(Sync);
        }

        /// <summary>The FIXED prepare step: build and publish inside one entry.</summary>
        public async Task PrepareAsync(Task spool)
        {
            await spool.ConfigureAwait(false);
            Gate.Run(() =>
            {
                var media = instance.Build();
                lock (Sync)
                {
                    if (Gate.IsClosed) { MediaFreedOnPublish++; return; }
                    Media = media;
                }
            });
        }

        /// <summary>The prepare step as it shipped in v0.44.1: build first, check after.</summary>
        public async Task PrepareAsyncAsShipped(Task spool)
        {
            await spool.ConfigureAwait(false);
            var media = instance.Build();
            lock (Sync)
            {
                if (Gate.IsClosed) { MediaFreedOnPublish++; return; }
                Media = media;
            }
        }

        public void Dispose() => Gate.Close();
    }

    private static Player NewPlayer(FakeInstance instance) => new(instance);

    [Fact]
    public void A_worker_still_spooling_when_the_backend_is_torn_down_never_touches_the_freed_instance()
    {
        var instance = new FakeInstance();
        var player = NewPlayer(instance);
        var spool = new TaskCompletionSource();
        var prepare = player.PrepareAsync(spool.Task);

        player.Dispose();          // the activity is destroyed during startup…
        instance.Free();           // …and the backend frees the shared LibVLC instance
        spool.SetResult();         // then the spool finishes
        Assert.True(prepare.Wait(Long));

        Assert.Equal(0, instance.UsesAfterFree);
        Assert.Equal(0, instance.Builds);          // refused outright, not built and thrown away
        Assert.Null(player.Media);
    }

    /// <summary>The control: the same sequence with the prepare step as it shipped. This is the
    /// crash, and it is what proves the test above can see one.</summary>
    [Fact]
    public void The_shipped_order_builds_against_the_freed_instance()
    {
        var instance = new FakeInstance();
        var player = NewPlayer(instance);
        var spool = new TaskCompletionSource();
        var prepare = player.PrepareAsyncAsShipped(spool.Task);

        player.Dispose();
        instance.Free();
        spool.SetResult();
        Assert.True(prepare.Wait(Long));

        Assert.Equal(1, instance.UsesAfterFree);
    }

    /// <summary>The other interleaving: the teardown arrives WHILE the media is being built. Close
    /// must wait for the build, the built media must be freed rather than kept, and only then may
    /// the instance go.</summary>
    [Fact]
    public void A_teardown_during_the_build_waits_for_it_and_the_media_is_freed_not_kept()
    {
        var sync = new object();
        var gate = new NativeCallGate(sync);
        var instance = new FakeInstance();
        using var inBuild = new ManualResetEventSlim();
        using var finishBuild = new ManualResetEventSlim();
        object? published = null;
        var freedOnPublish = 0;

        var worker = Task.Run(() => gate.Run(() =>
        {
            inBuild.Set();
            finishBuild.Wait(Long);
            var media = instance.Build();
            lock (sync)
            {
                if (gate.IsClosed) { freedOnPublish++; return; }
                published = media;
            }
        }));
        Assert.True(inBuild.Wait(Long));

        var teardown = Task.Run(() => { gate.Close(); instance.Free(); });
        Assert.False(teardown.Wait(200), "the close returned while a worker was still inside a native call");

        finishBuild.Set();
        Assert.True(teardown.Wait(Long));
        Assert.True(worker.Wait(Long));

        Assert.Equal(0, instance.UsesAfterFree);
        Assert.Equal(1, instance.Builds);
        Assert.Equal(1, freedOnPublish);
        Assert.Null(published);
    }

    [Fact]
    public void A_worker_that_starts_after_the_close_is_refused()
    {
        var gate = new NativeCallGate(new object());
        Assert.True(gate.Close());

        Assert.False(gate.TryEnter());
        var ran = false;
        Assert.False(gate.Run(() => ran = true));
        Assert.False(ran);
        Assert.True(gate.IsClosed);
    }

    [Fact]
    public void Closing_twice_is_a_no_op()
    {
        var gate = new NativeCallGate(new object());
        Assert.True(gate.Close());
        Assert.False(gate.Close());
    }

    [Fact]
    public void An_open_gate_runs_the_body_and_a_close_after_it_does_not_wait()
    {
        var gate = new NativeCallGate(new object());
        var ran = false;
        Assert.True(gate.Run(() => ran = true));
        Assert.True(ran);
        Assert.True(Task.Run(() => gate.Close()).Wait(Long));
    }

    /// <summary>A body that takes the owner's lock while a close is waiting on that same lock must
    /// not deadlock: the waiting close has released it (Monitor.Wait). This is the shape the
    /// player's publish step has.</summary>
    [Fact]
    public void A_body_taking_the_owners_lock_while_a_close_waits_does_not_deadlock()
    {
        var sync = new object();
        var gate = new NativeCallGate(sync);
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();

        var worker = Task.Run(() => gate.Run(() =>
        {
            entered.Set();
            proceed.Wait(Long);
            lock (sync) { }
        }));
        Assert.True(entered.Wait(Long));
        var close = Task.Run(() => gate.Close());
        Thread.Sleep(50);                 // let the close reach its wait
        proceed.Set();

        Assert.True(Task.WaitAll([worker, close], Long), "deadlocked");
    }
}
