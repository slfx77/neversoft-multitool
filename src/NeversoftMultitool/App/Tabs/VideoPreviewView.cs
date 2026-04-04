using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Controls;

namespace NeversoftMultitool;

internal sealed class VideoPreviewView
{
    public required Border PreviewPanel { get; init; }
    public required GridSplitter PreviewSplitter { get; init; }
    public required ColumnDefinition SplitterColumn { get; init; }
    public required ColumnDefinition PreviewColumn { get; init; }
    public required ProgressRing PreviewLoading { get; init; }
    public required FontIcon VideoPlaceholderIcon { get; init; }
    public required TextBlock PreviewFileNameText { get; init; }
    public required TextBlock PreviewInfoText { get; init; }
    public required TextBlock PreviewErrorText { get; init; }
    public required Button PlayPauseButton { get; init; }
    public required Button StopButton { get; init; }
    public required Slider PlaybackSlider { get; init; }
    public required TextBlock CurrentTimeText { get; init; }
    public required TextBlock TotalTimeText { get; init; }
    public required MediaPlayerElement VideoPlayer { get; init; }
    public required FontIcon PlayPauseIcon { get; init; }
    public required string TempDir { get; init; }
}
