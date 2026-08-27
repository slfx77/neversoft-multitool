namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     Which pixels of a composited level's art the game actually draws.
///
///     <para>The collision grid is a rectangle, but a level's authored art is not:
///     School II has a deep notch between its two building wings, and Rooftops and
///     the Pool are smaller shapes inside their canvas. Surface cells over those
///     regions have no art to sample, and rendered as flat black slabs — the
///     "objects displaying black" a user reported. The art is the authority on
///     where the level exists, so a cell it never draws is not emitted.</para>
///
///     <para>Undrawn is <b>pure black reachable from the canvas edge</b>, not merely
///     pure black. That distinction is load-bearing: the drawn art does contain a
///     few pure-black pixels of its own (20,178 in Rooftops, 1,497 in Warehouse),
///     and dropping cells over those would punch holes in real geometry. Measured
///     over the corpus, 99.991% of School II's black is border-reachable surround,
///     and no drawn pixel in any level is pure black — the darkest sums to 8 of a
///     possible 765.</para>
/// </summary>
internal static class GbaLevelArtCoverage
{
    /// <summary>
    ///     True per pixel where the level's art is undrawn. Null when the art has
    ///     no undrawn pixel at all (four of the nine levels), so the caller can
    ///     skip the test entirely and their geometry provably cannot change.
    /// </summary>
    public static bool[]? BuildUndrawnMask(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var count = width * height;
        if (count <= 0 || rgba.Length < count * 4)
            return null;

        var black = new bool[count];
        var anyBlack = false;
        for (var i = 0; i < count; i++)
        {
            // Alpha is ignored: the compositor writes opaque pixels throughout,
            // and it is the colour that says whether a tile was drawn.
            if (rgba[i * 4] != 0 || rgba[i * 4 + 1] != 0 || rgba[i * 4 + 2] != 0)
                continue;
            black[i] = true;
            anyBlack = true;
        }

        if (!anyBlack)
            return null;

        var undrawn = new bool[count];
        var queue = new Queue<int>();
        void Seed(int index)
        {
            if (black[index] && !undrawn[index])
            {
                undrawn[index] = true;
                queue.Enqueue(index);
            }
        }

        for (var x = 0; x < width; x++)
        {
            Seed(x);
            Seed((height - 1) * width + x);
        }

        for (var y = 0; y < height; y++)
        {
            Seed(y * width);
            Seed(y * width + width - 1);
        }

        while (queue.Count > 0)
        {
            var index = queue.Dequeue();
            var x = index % width;
            var y = index / width;
            if (x > 0) Seed(index - 1);
            if (x + 1 < width) Seed(index + 1);
            if (y > 0) Seed(index - width);
            if (y + 1 < height) Seed(index + width);
        }

        return undrawn;
    }

    /// <summary>
    ///     True when the art draws nothing at this normalised coordinate — including
    ///     off-canvas, which is undrawn by definition.
    /// </summary>
    public static bool IsUndrawn(bool[] mask, int width, int height, float u, float v)
    {
        var x = (int)(u * width);
        var y = (int)(v * height);
        if (x < 0 || y < 0 || x >= width || y >= height)
            return true;
        return mask[y * width + x];
    }
}
