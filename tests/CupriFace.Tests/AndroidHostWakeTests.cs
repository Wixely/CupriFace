using Xunit;
using Xunit.Abstractions;

namespace CupriFace.Tests;

/// <summary>
/// The Android half of "a post wakes a sleeping host" — checked against the source, as
/// <see cref="LayeredAlphaTests"/> and <see cref="HostFullscreenStateTests"/> are, because it cannot
/// be exercised by running it.
///
/// <para><b>Why it cannot.</b> The engine half is behavioural and already covered: <c>Post</c> raises
/// <c>FrameRequested</c>, and <see cref="PostedWorkTests"/> asserts that. What no behavioural test can
/// reach is whether a HOST subscribes to it. The test harness lays out and renders unconditionally,
/// so posted work is applied in the next frame whether or not anything asked for a frame — which is
/// exactly the condition that makes the real bug invisible: on a `RENDERMODE_WHEN_DIRTY` GLSurfaceView
/// the thread has stopped polling, nothing asks, and the post waits for ever. Deleting
/// <c>doc.FrameRequested += MarkDirty</c> would leave all 2150 tests green.</para>
///
/// <para>So these assert the three facts the feature actually rests on: it is wired, it is wired to
/// something thread-safe, and the wiring survives an app switch. The third is the one most likely to
/// rot — a new document is built in <c>SwapApp</c>, and a subscription attached in one place and
/// dropped in another is how this breaks quietly for back-navigation only.</para>
/// </summary>
public class AndroidHostWakeTests(ITestOutputHelper output)
{
    private static string Android(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CupriFace.slnx"))) d = d.Parent;
        var root = d?.FullName ?? throw new InvalidOperationException("repo root not found");
        return File.ReadAllText(Path.Combine(root, "src", "CupriFace.Android", file));
    }

    /// <summary>The body of a member, by brace matching from its signature.</summary>
    private static string Body(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no member matching '{signature}'");
        var open = source.IndexOf('{', at);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException($"unbalanced braces after '{signature}'");
    }

    [Fact]
    public void Every_document_the_host_wires_can_wake_a_parked_render_thread()
    {
        // In WireDocument specifically, which is the one place every document goes through — the
        // first one and each one built by an app switch. Subscribing anywhere else would cover the
        // document that happened to be in front and miss the others.
        var body = Body(Android("AndroidHost.cs"), "private void WireDocument(CupriDocument doc)");

        Assert.Contains("doc.FrameRequested += MarkDirty", body, StringComparison.Ordinal);
        output.WriteLine("WireDocument subscribes FrameRequested");
    }

    [Fact]
    public void The_wake_signal_is_thread_safe_because_a_post_can_come_from_any_thread()
    {
        // Post is the ONLY thread-safe member of CupriDocument and may be called from a reader
        // thread, so FrameRequested is raised on that thread. Whatever the host hangs on it must
        // therefore be safe to call from there and must only SIGNAL — no document work, which is
        // what the posted action itself is for. RequestRender is that signal (GLSurfaceView
        // contract); touching _doc here instead would be the bug this guards.
        // Expression-bodied, so this is the declaration through its terminating semicolon rather
        // than a brace-matched block.
        var src = Android("AndroidHost.cs");
        var at = src.IndexOf("internal void MarkDirty()", StringComparison.Ordinal);
        Assert.True(at >= 0, "AndroidHost.MarkDirty is gone — FrameRequested is wired to it");
        var decl = src[at..(src.IndexOf(';', at) + 1)];

        Assert.Contains("RequestRender", decl, StringComparison.Ordinal);
        Assert.DoesNotContain("_doc", decl, StringComparison.Ordinal);
        output.WriteLine(decl);
    }

    [Fact]
    public void An_app_switch_moves_the_subscription_rather_than_leaking_it()
    {
        // SwapApp replaces the document, so the old subscription has to go and the new one has to
        // arrive. It gets the latter from WireDocument; the former is explicit, and without it a
        // disposed document's handler stays on a live host.
        var src = Android("AndroidHost.cs");
        var body = Body(src, "private void SwapApp(CupriApp next, bool pushCurrent)");

        Assert.Contains("FrameRequested -= MarkDirty", body, StringComparison.Ordinal);
        Assert.Contains("WireDocument(_doc)", body, StringComparison.Ordinal);

        // …and in that order: unsubscribe the OLD document, then wire the new one. Reversed, the
        // -= would strip the subscription just made if both names pointed at the same document.
        Assert.True(body.IndexOf("FrameRequested -= MarkDirty", StringComparison.Ordinal)
                    < body.IndexOf("WireDocument(_doc)", StringComparison.Ordinal),
                    "the old document must be unsubscribed before the new one is wired");

        // Disposal owes the same debt: the host outlives the document it is told to drop.
        Assert.Contains("FrameRequested -= MarkDirty", Body(src, "public void Dispose()"),
                        StringComparison.Ordinal);
    }
}
