using CupriFace.Media;

namespace CupriFace.Media.Windows;

internal sealed class PopOutPlayerControls
{
    internal required IVideoPlayer Player { get; init; }
    internal required Action RequestFrame { get; init; }
    internal required Func<bool> IsPlaying { get; init; }
    internal required Action TogglePlayback { get; init; }
    internal required bool ShowVolumeControls { get; init; }
    internal required Func<double> Duration { get; init; }
    internal required Func<IReadOnlyList<VideoTrack>> AudioTracks { get; init; }
    internal required Func<IReadOnlyList<VideoTrack>> SubtitleTracks { get; init; }
    internal required Func<int> SelectedAudioTrack { get; init; }
    internal required Func<int> SelectedSubtitleTrack { get; init; }
    internal required Action<int> SelectAudioTrack { get; init; }
    internal required Action<int> SelectSubtitleTrack { get; init; }
    internal required Func<IReadOnlyList<VideoChapter>> Chapters { get; init; }
}
