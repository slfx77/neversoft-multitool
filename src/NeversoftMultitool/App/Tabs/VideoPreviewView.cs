using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NeversoftMultitool;

internal sealed class VideoPreviewView
{
    public required ProgressRing PreviewLoading { get; init; }
    public required UIElement VideoPlaceholderPanel { get; init; }
    public required TextBlock PreviewFileNameText { get; init; }
    public required TextBlock PreviewInfoText { get; init; }
    public required TextBlock PreviewErrorText { get; init; }
    public required Button PlayPauseButton { get; init; }
    public required Button StopButton { get; init; }
    public required Slider PlaybackSlider { get; init; }
    public required Slider VolumeSlider { get; init; }
    public required TextBlock CurrentTimeText { get; init; }
    public required TextBlock TotalTimeText { get; init; }
    public required MediaPlayerElement VideoPlayer { get; init; }
    public required FontIcon PlayPauseIcon { get; init; }
    public required TimeSliderValueConverter PlaybackTooltipConverter { get; init; }
    public required string TempDir { get; init; }
}
