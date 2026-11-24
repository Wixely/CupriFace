using CupriFace.Platform;

namespace CupriFace.Shell.Dialogs;

/// <summary>Picks this OS's <see cref="FileDialogs"/> for one window, or none.</summary>
internal static class DesktopFileDialogs
{
    /// <summary>
    /// The file dialogs for a window: owned by it on Windows, a sheet on it on macOS, the desktop
    /// portal on Linux. Null on a platform with none, and on Linux with no session bus, so
    /// <see cref="CupriDocument.FileDialogs"/> says "not here" rather than offering a button that
    /// cannot work. <c>CUPRIFACE_FILE_DIALOGS=0</c> turns them off, to try an app's fallback.
    /// </summary>
    /// <param name="win32Owner">The window's HWND on Windows, read when a dialog is shown.</param>
    /// <param name="cocoaWindow">The window's NSWindow on macOS, read when a dialog is shown.</param>
    /// <param name="post">How a result reaches the document's thread.</param>
    public static FileDialogs? Create(Func<nint?> win32Owner, Func<nint?> cocoaWindow, Action<Action> post)
    {
        if (Environment.GetEnvironmentVariable("CUPRIFACE_FILE_DIALOGS") is "0" or "false" or "FALSE") return null;
        if (OperatingSystem.IsWindows()) return new WindowsFileDialogs(win32Owner, post);
        if (OperatingSystem.IsMacOS()) return new MacFileDialogs(cocoaWindow, post);
        if (OperatingSystem.IsLinux() && PortalFileDialogs.Available) return new PortalFileDialogs(post);
        return null;
    }
}
