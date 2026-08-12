using System.Globalization;
using System.Reflection;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class StrConverterTests
{
    [Fact]
    public void BuildFfmpegArgs_UsesInvariantFrameRate()
    {
        var method = typeof(StrConverter).GetMethod(
            "BuildFfmpegArgs",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            var arguments = Assert.IsType<string>(method.Invoke(null, [
                320,
                240,
                12.5,
                null,
                "output.mp4"
            ]));

            Assert.Contains("-r 12.50", arguments, StringComparison.Ordinal);
            Assert.DoesNotContain("12,50", arguments, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }
}
