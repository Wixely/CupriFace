namespace CupriFace.Media;

/// <summary>
/// Stops a native media object being freed while a worker thread is still inside a call on it.
///
/// <para><b>The failure it exists for.</b> A video backend's decoder (LibVLC on Android) is a
/// native library, and a call into a freed native object is a PROCESS crash, not an exception:
/// <c>libvlc_retain: assertion "p_instance != NULL" failed</c>, SIGABRT, gone (#317). Workers on
/// the thread pool call into it — seeks, metadata refreshes, building the media for a spooled
/// local file — while the UI thread can dispose the player at any moment, most often because the
/// activity was destroyed during startup. A worker that checked "not disposed" a moment before
/// Dispose ran would otherwise carry on into freed memory.</para>
///
/// <para><b>The contract.</b> A worker calls <see cref="TryEnter"/> before touching native code and
/// <see cref="Exit"/> after; a refused entry means "do nothing at all". <see cref="Close"/> refuses
/// every later entry and then WAITS for those already inside to leave, so once it returns nothing
/// can reach the native object and it is safe to free. Every native call made off the owning
/// thread must be inside an entry — including CONSTRUCTING native objects from a shared native
/// instance, which is exactly the one #317 found outside it. <see cref="Run"/> is the form for
/// that: build and publish inside one entry.</para>
///
/// <para><b>Public for backend authors.</b> A video backend written against <see cref="IVideoBackend"/>
/// in another package faces the same teardown race as the built-in LibVLC one, so the gate that
/// closes it is part of the seam rather than private to one backend.</para>
///
/// <para><b>One lock, shared with the owner.</b> The gate takes the owner's own lock object rather
/// than having its own, so the owner's existing <c>lock (sync) { if (gate.IsClosed) … }</c> checks
/// stay atomic with <see cref="Close"/>: a native call made under that lock cannot interleave with
/// the close.</para>
/// </summary>
public sealed class NativeCallGate(object sync)
{
    private int _busy;
    private volatile bool _closed;

    /// <summary>True once <see cref="Close"/> has started. Safe to read without the lock for a
    /// best-effort early return; read it under the lock when the answer guards a native call.</summary>
    public bool IsClosed => _closed;

    /// <summary>Enter before calling into native code off the owning thread. False when the owner is
    /// closing or closed — the caller must then do nothing.</summary>
    public bool TryEnter()
    {
        lock (sync)
        {
            if (_closed) return false;
            _busy++;
            return true;
        }
    }

    /// <summary>Leave after a successful <see cref="TryEnter"/>. Wakes a <see cref="Close"/> that is
    /// waiting for the last caller out.</summary>
    public void Exit()
    {
        lock (sync)
        {
            if (--_busy <= 0) Monitor.PulseAll(sync);
        }
    }

    /// <summary>Refuse all later entries and wait until every caller already inside has left. True
    /// the first time, false if already closed — so a second Dispose is a no-op.</summary>
    public bool Close()
    {
        lock (sync)
        {
            if (_closed) return false;
            _closed = true;
            while (_busy > 0) Monitor.Wait(sync);
            return true;
        }
    }

    /// <summary>Run <paramref name="body"/> inside an entry: not at all if the owner is closing, and
    /// with any <see cref="Close"/> waiting until it returns. False when refused.
    ///
    /// <para>Make AND publish a native object inside one call. A close can begin while the object is
    /// being made, so the body checks <see cref="IsClosed"/> under the owner's lock before keeping
    /// it, and frees it there if the owner is going — still inside the entry, so the native
    /// instance it was made from is still alive to free it against. Taking the owner's lock inside
    /// the body cannot deadlock with a waiting close: <see cref="Monitor.Wait(object)"/> releases
    /// the lock while it waits.</para></summary>
    public bool Run(Action body)
    {
        if (!TryEnter()) return false;
        try { body(); }
        finally { Exit(); }
        return true;
    }
}
