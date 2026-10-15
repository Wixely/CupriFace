using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Guards Windows media-host wiring which needs a real HWND and therefore cannot be exercised by
/// the headless document harness. The Windows project build still verifies the platform imports.
/// </summary>
public class WindowsPlaybackIntegrationTests
{
    [Fact]
    public void Popout_seek_bar_captures_and_tracks_a_pointer_drag()
    {
        var source = Media("Win32PopOutChrome.cs");

        Assert.Contains("state.Scrubbing = true", source, StringComparison.Ordinal);
        Assert.Contains("SetCapture(window)", source, StringComparison.Ordinal);
        Assert.Contains("message == 0x0200", source, StringComparison.Ordinal); // WM_MOUSEMOVE
        Assert.Contains("SeekFromProgressX(state, SignedLow(longParameter))", source, StringComparison.Ordinal);
        Assert.Contains("message == 0x0202", source, StringComparison.Ordinal); // WM_LBUTTONUP
        Assert.Contains("ReleaseCapture()", source, StringComparison.Ordinal);
        Assert.Contains("message == 0x0215", source, StringComparison.Ordinal); // WM_CAPTURECHANGED
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
