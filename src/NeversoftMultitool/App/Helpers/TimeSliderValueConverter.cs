using Microsoft.UI.Xaml.Data;

namespace NeversoftMultitool;

/// <summary>
///     Formats a playback slider's thumb tooltip as a timestamp. Both preview
///     players' sliders run 0..100 as a percentage of the media duration, so
///     the owning tab feeds the opened stream's duration in through
///     <see cref="DurationSeconds" /> (0 = no media, formats as "00:00").
/// </summary>
public sealed class TimeSliderValueConverter : IValueConverter
{
    public double DurationSeconds { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var fraction = value is double sliderValue ? sliderValue / 100.0 : 0.0;
        return TimeDisplay.Format(TimeSpan.FromSeconds(fraction * DurationSeconds));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
