namespace NeversoftMultitool.Core.Formats.Rle;

/// <summary>
/// Corrects column-shifted pixels in RLE/BMR images.
///
/// Some RLE images encode the first two columns at the end of each row,
/// shifted down by one pixel. This class detects and corrects that encoding.
/// </summary>
internal static class ColumnUnshifter
{
    private static readonly RgbColor BlueMarker1 = new(0, 0, 144);
    private static readonly RgbColor BlueMarker2 = new(0, 0, 208);

    public static List<List<RgbColor>> Unshift(List<List<RgbColor>> canvas)
    {
        if (canvas.Count == 0 || canvas[0].Count < 2)
            return canvas;

        // Check for blue marker pixel at the end of the first row
        var firstRow = canvas[0];
        var lastPixel = firstRow[^1];

        if (lastPixel != BlueMarker1 && lastPixel != BlueMarker2)
            return canvas;

        // Move final two columns to start of each row.
        // RLE encodes these columns at the end of the rows.
        foreach (var row in canvas)
        {
            if (row.Count < 2) continue;

            var last = row[^1];
            row.RemoveAt(row.Count - 1);
            row.Insert(0, last);

            last = row[^1];
            row.RemoveAt(row.Count - 1);
            row.Insert(0, last);
        }

        // Shift pixels in the first two columns up by one row.
        // The encoder shifts them down, so we reverse that.
        for (var i = 0; i < canvas.Count; i++)
        {
            if (i + 2 < canvas.Count)
            {
                canvas[i][0] = canvas[i + 1][0];
                canvas[i][1] = canvas[i + 1][1];
            }
        }

        return canvas;
    }
}
