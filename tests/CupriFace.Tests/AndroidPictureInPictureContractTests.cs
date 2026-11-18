using Xunit;

namespace CupriFace.Tests;

/// <summary>Source contract for the Android-only host seam, which cannot execute in the desktop
/// test process. Android project builds provide the binding/API check; these assertions protect the
/// ownership boundary and the no-second-decoder behavior.</summary>
public sealed class AndroidPictureInPictureContractTests
{
    [Fact]
    public void PictureInPictureIsApplicationOptInAndRequiresAReadyBackend()
    {
        var seam = Source("CupriFace.Android", "AndroidVideoSeam.cs");
        Assert.Contains("bool PictureInPictureReady => false", seam, StringComparison.Ordinal);

        var activity = Source("CupriFace.Android", "CupriActivity.cs");
        Assert.Contains("protected virtual bool EnablePictureInPicture => false", activity, StringComparison.Ordinal);
        Assert.Contains("_videoBackend?.PictureInPictureReady == true", activity, StringComparison.Ordinal);
        Assert.Contains("EnterPictureInPictureMode(parameters)", activity, StringComparison.Ordinal);
    }

    [Fact]
    public void NativePipReusesTheActiveViewAndRestoresTheDocumentLayer()
    {
        var activity = Source("CupriFace.Android", "CupriActivity.cs");
        Assert.Contains("_videoBackend?.SetPictureInPictureMode(isInPictureInPictureMode)", activity,
            StringComparison.Ordinal);
        Assert.Contains("ViewStates.Invisible : ViewStates.Visible", activity, StringComparison.Ordinal);

        var backend = Source("CupriFace.Media.Android", "VlcVideoBackend.cs");
        var start = backend.IndexOf("public void SetPictureInPictureMode", StringComparison.Ordinal);
        var end = backend.IndexOf("public IVideoPlayer Open", start, StringComparison.Ordinal);
        var body = backend[start..end];
        Assert.Contains("Fill(view)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Open(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new AndroidPlayer", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NativePipRestoresPlaybackWhenItBecomesVisibleAfterDeviceUnlock()
    {
        var activity = Source("CupriFace.Android", "CupriActivity.cs");
        var start = activity.IndexOf("protected override void OnStart()", StringComparison.Ordinal);
        var resume = activity.IndexOf("protected override void OnResume()", start, StringComparison.Ordinal);
        var stop = activity.IndexOf("protected override void OnStop()", resume, StringComparison.Ordinal);

        Assert.True(start >= 0 && start < resume && resume < stop, "activity lifecycle methods are missing or out of order");
        Assert.Contains("_videoBackend?.SetActivityVisible(true)", activity[start..resume], StringComparison.Ordinal);
        Assert.DoesNotContain("_videoBackend?.SetActivityVisible(true)", activity[resume..stop], StringComparison.Ordinal);
        Assert.Contains("_videoBackend?.SetActivityVisible(false)", activity[stop..], StringComparison.Ordinal);
    }

    private static string Source(string project, string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CupriFace.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("repo root not found");
        return File.ReadAllText(Path.Combine(root, "src", project, file));
    }
}
