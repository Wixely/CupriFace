namespace CupriFace.Media.Windows;

/// <summary>Application-owned presentation for the native Windows video pop-out.</summary>
/// <param name="Title">Native window and task-switcher title.</param>
/// <param name="Icon">Encoded PNG/JPEG/WebP application icon, or <see langword="null"/> for the
/// process default.</param>
/// <param name="DecorateIcon">Add a compact picture-in-picture badge so the pop-out remains
/// recognizably related to, but visually distinct from, the main application.</param>
/// <param name="ShowVolumeControls">Enable the optional hover volume slider in the pop-out's
/// shared video controls. Disabled by default for hosts that delegate volume to the device.</param>
public sealed record VideoPopOutWindowOptions(
    string Title = "Video player",
    byte[]? Icon = null,
    bool DecorateIcon = true,
    bool ShowVolumeControls = false);
