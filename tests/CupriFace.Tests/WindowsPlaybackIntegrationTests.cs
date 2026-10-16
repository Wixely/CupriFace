using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// One contract in the Windows media host that has no headless expression.
///
/// <para>Read what this can and cannot prove. It asserts over the FILE'S OWN SOURCE TEXT, because
/// the behaviour it guards needs a real HWND, a LibVLC instance and a decoder thread — none of
/// which this suite has. So it cannot tell you the teardown works; it can only tell you the line
/// that makes it work is still there. It fails if someone deletes the guard, and it passes if
/// someone keeps the guard and breaks everything around it.</para>
///
/// <para>That is a weak test, and it is here only because the alternative for this one line is no
/// test at all. Do not add to this pattern: a grep over source is not a substitute for behaviour,
/// and a file that accumulates them starts reporting green for a feature nobody has run. Anything
/// that CAN be driven through <c>CupriDocument</c> belongs in the suites that actually drive it.</para>
/// </summary>
public class WindowsPlaybackIntegrationTests
{
    /// <summary>
    /// <c>Stop()</c> is synchronous and joins LibVLC's playback threads. Once <c>EndReached</c> has
    /// fired the input is already stopped, and calling <c>Stop()</c> while that native callback is
    /// still unwinding deadlocks an autoplay source replacement on the UI thread — the app hangs
    /// with no exception and no log line, which is why the guard is worth pinning at all.
    /// </summary>
    [Fact]
    public void Naturally_ended_windows_video_does_not_synchronously_stop_during_disposal()
    {
        var backend = Media("LibVlcVideoBackend.cs");

        Assert.Contains("_endReached = true;", backend, StringComparison.Ordinal);
        Assert.Contains("if (!_endReached) _player.Stop();", backend, StringComparison.Ordinal);
    }

    private static string Media(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CupriFace.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("repo root not found");
        return File.ReadAllText(Path.Combine(root, "src", "CupriFace.Media.Windows", file));
    }
}
