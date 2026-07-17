using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool;

/// <summary>
///     A row in the Character Preview tab's animation panel. Wraps an
///     <see cref="AnimationProbe" /> with two view-side flags:
///     <see cref="IsChecked" /> ("include in multi-anim GLB export") and
///     <see cref="IsActive" /> ("currently previewed in 3D and the GIF render
///     target"). Mismatched-skeleton anims still appear in the list but render
///     greyed-out via <see cref="RowForeground" />.
/// </summary>
internal sealed class AnimationListEntry : INotifyPropertyChanged
{
    private bool _isActive;
    private bool _isChecked;

    public required AnimationProbe Probe { get; init; }

    public string DisplayName => Probe.ResolvedDisplayName;

    public string DurationDisplay => Probe.DurationDisplay;

    public string BoneCountDisplay => Probe.BoneCount.HasValue
        ? Probe.BoneCount.Value.ToString()
        : "?";

    public bool MatchesSkeleton => Probe.MatchesSkeleton;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    ///     Foreground brush for the row. A bound null brush does not inherit the
    ///     theme foreground in WinUI, which made matching animation names and
    ///     durations render blank. Resolve both states to real theme brushes.
    /// </summary>
    public Brush RowForeground => (Brush)Application.Current.Resources[
        MatchesSkeleton ? "TextFillColorPrimaryBrush" : "TextFillColorDisabledBrush"];

    public string? MismatchTooltip => MatchesSkeleton
        ? null
        : $"Bone count {BoneCountDisplay} doesn't match this character's skeleton.";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
