using Xunit;

namespace CupriFace.Tests;

/// <summary>
/// Guards Windows media-host wiring which needs a real HWND and therefore cannot be exercised by
/// the headless document harness. The Windows project build still verifies the platform imports.
/// </summary>
public class WindowsPlaybackIntegrationTests
{
    [Fact]
    public void Popout_uses_the_shared_video_component_and_pointer_driver()
    {
        var source = Media("Win32PopOutChrome.cs");

        Assert.Contains("<cupri-video src='cupri-popout-controls' controls chapters>", source, StringComparison.Ordinal);
        Assert.Contains("UseComponents(ComponentRegistry.Default())", source, StringComparison.Ordinal);
        Assert.Contains("UseVideoControls(ControlSource, controls.Player)", source, StringComparison.Ordinal);
        Assert.Contains("state.Document?.DispatchClick(x, y)", source, StringComparison.Ordinal);
        Assert.Contains("SetCapture(window)", source, StringComparison.Ordinal);
        Assert.Contains("message == 0x0200", source, StringComparison.Ordinal); // WM_MOUSEMOVE
        Assert.Contains("state.Document?.DispatchPointerMove(x, y)", source, StringComparison.Ordinal);
        Assert.Contains("message == 0x0202", source, StringComparison.Ordinal); // WM_LBUTTONUP
        Assert.Contains("state.Document?.DispatchPointerUp", source, StringComparison.Ordinal);
        Assert.Contains("ReleaseCapture()", source, StringComparison.Ordinal);
        Assert.Contains("message == 0x0215", source, StringComparison.Ordinal); // WM_CAPTURECHANGED
        Assert.DoesNotContain("DrawProgress(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SeekFromProgressX(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Clicking_the_popout_video_toggles_playback()
    {
        var source = Media("Win32PopOutChrome.cs");

        var parentMessages = source[source.IndexOf("internal static bool HandleParentMessage", StringComparison.Ordinal)..
            source.IndexOf("internal static PopOutBounds? CaptureBounds", StringComparison.Ordinal)];
        Assert.Contains("case 0x0201", parentMessages, StringComparison.Ordinal); // WM_LBUTTONDOWN
        Assert.Contains("state.Controls.TogglePlayback()", parentMessages, StringComparison.Ordinal);
        Assert.Contains("return true", parentMessages, StringComparison.Ordinal);
    }

    [Fact]
    public void Popout_client_areas_use_the_standard_arrow_cursor()
    {
        var videoHost = Media("Win32VideoWindow.cs");
        var controls = Media("Win32PopOutChrome.cs");

        Assert.Contains("Cursor = LoadCursorW(0, new nint(IdcArrow))", videoHost, StringComparison.Ordinal);
        Assert.Contains("Cursor = LoadCursorW(0, new nint(IdcArrow))", controls, StringComparison.Ordinal);
    }

    [Fact]
    public void Popout_accepts_and_decorates_an_application_icon()
    {
        var backend = Media("LibVlcVideoBackend.cs");
        var window = Media("Win32VideoWindow.cs");

        Assert.Contains("VideoPopOutWindowOptions? popOutWindow", backend, StringComparison.Ordinal);
        Assert.Contains("DrawPictureInPictureBadge(canvas, size)", window, StringComparison.Ordinal);
        Assert.Contains("WM_SETICON, ICON_BIG", window, StringComparison.Ordinal);
        Assert.Contains("WM_SETICON, ICON_SMALL", window, StringComparison.Ordinal);
        Assert.Contains("DestroyIcon(large)", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Popout_volume_ui_is_explicitly_configurable()
    {
        var options = Media("VideoPopOutWindowOptions.cs");
        var chrome = Media("Win32PopOutChrome.cs");

        Assert.Contains("bool ShowVolumeControls = false", options, StringComparison.Ordinal);
        Assert.Contains("controls.ShowVolumeControls ? VolumeControlHtml : ControlHtml", chrome, StringComparison.Ordinal);
        Assert.Contains("volume-controls", chrome, StringComparison.Ordinal);
        Assert.Contains("pointerInside && Environment.TickCount64 - state.LastInteraction < 3000", chrome, StringComparison.Ordinal);
        Assert.Contains("ApplyOverlayRegion", chrome, StringComparison.Ordinal);
        Assert.Contains("if (volumeOpen)", chrome, StringComparison.Ordinal);
        Assert.Contains("popup.Style.Display != Style.DisplayType.None", chrome, StringComparison.Ordinal);
        Assert.Contains("VolumePopupVerticalPadding", chrome, StringComparison.Ordinal);
        Assert.Contains("can briefly report stale/empty layout bounds", chrome, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerOverVolumeControl", chrome, StringComparison.Ordinal);
    }

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
