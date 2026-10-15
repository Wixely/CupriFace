using CupriFace.Media;

namespace CupriFace.Media.Windows;

internal sealed class PopOutPlayerControls
{
    internal required Func<bool> IsPlaying { get; init; }
    internal required Action TogglePlayback { get; init; }
    internal required Func<bool> IsMuted { get; init; }
    internal required Action ToggleMute { get; init; }
    internal required Func<double> Position { get; init; }
    internal required Func<double> Duration { get; init; }
    internal required Action<double> Seek { get; init; }
    internal required Func<IReadOnlyList<VideoTrack>> AudioTracks { get; init; }
    internal required Func<IReadOnlyList<VideoTrack>> SubtitleTracks { get; init; }
    internal required Func<int> SelectedAudioTrack { get; init; }
    internal required Func<int> SelectedSubtitleTrack { get; init; }
    internal required Action<int> SelectAudioTrack { get; init; }
    internal required Action<int> SelectSubtitleTrack { get; init; }
    internal required Func<IReadOnlyList<VideoChapter>> Chapters { get; init; }
}
