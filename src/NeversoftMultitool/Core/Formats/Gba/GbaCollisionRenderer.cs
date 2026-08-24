using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     Renders the THPS2 GBA collision surface isometrically using each cell's <b>real
///     surface</b> (see <see cref="GbaCollisionSurface" />) rather than a flat-topped box.
///     That distinction is the whole point: a quarter-pipe cell whose height function is
///     sampled at one point collapses into a wall, and a ramp becomes a staircase.
///
///     <para>Each cell is sub-sampled into a <see cref="SubDivisions" />×<see cref="SubDivisions" />
///     patch of quads, projected on the same isometric basis as the rest of this file
///     (horizontal term <c>gy - gx</c>), and rasterised through a depth buffer — the
///     surfaces genuinely overlap in an isometric view, so painter's ordering is not
///     enough. Faces are shaded by their true geometric normal, so slope reads as slope.</para>
///
///     <para>Vertical skirts are drawn between neighbouring cells down to the neighbour's
///     surface, which is what makes ledges and wall faces solid rather than floating.</para>
///
///     <para>Every level is enclosed by a one-cell-thick out-of-bounds kill wall standing
///     far above the playfield (the Hangar's is at 34.375 world units against a 0-9 unit
///     playfield). Drawing it would hide the entire interior, so cells whose base height
///     exceeds <see cref="OutOfBoundsHeight" /> are omitted and counted.</para>
/// </summary>
public static class GbaCollisionRenderer
{
    /// <summary>Sub-quads per cell edge. 4 keeps thin rails visible without over-sampling.</summary>
    public const int SubDivisions = 4;

    /// <summary>Cells whose base height exceeds this are the out-of-bounds kill wall.</summary>
    public const double OutOfBoundsHeight = 30.0;

    private const double TileWidth = 18.0 * 5.0;  // matches the iso basis, zoomed
    private const double TileHeight = 9.0 * 5.0;
    private const double HeightScale = 6.0 * 5.0;
    private const int MaxDimension = 8192;
    private const double Fixed = 4096.0;

    private static readonly Vector3 Light = Vector3.Normalize(new Vector3(0.40f, -0.30f, 0.87f));

    /// <summary>A rendered collision surface, plus how many kill-wall cells were omitted.</summary>
    public readonly record struct GbaCollisionRender(int Width, int Height, byte[] Rgba, int OmittedCells);

    /// <summary>How to colour the surface.</summary>
    public enum TintMode
    {
        /// <summary>A distinct hue per collision material.</summary>
        Material,

        /// <summary>Highlight cells whose surface is genuinely not flat.</summary>
        Slope
    }

    /// <summary>
    ///     Renders one level's collision surface, or null when the level's collision
    ///     fields do not validate.
    /// </summary>
    public static GbaCollisionRender? Render(
        ReadOnlySpan<byte> rom, int trueRecordOffset, TintMode tint = TintMode.Material)
    {
        var grid = GbaCollisionSurface.TryLoad(rom, trueRecordOffset);
        if (grid is null)
            return null;

        var w = grid.Width;
        var h = grid.Height;
        const int n = SubDivisions;
        var step = n + 1;

        // Sample every cell once, and decide which cells are in bounds.
        var heights = new double[w * h][];
        var live = new bool[w * h];
        var sloped = new bool[w * h];
        var materials = new int[w * h];
        var omitted = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var index = y * w + x;
            var cell = grid.CellAt(x, y);
            materials[index] = cell.Material;
            var samples = grid.SampleCell(rom, x, y, step);
            var values = new double[samples.Length];
            var min = double.MaxValue;
            var max = double.MinValue;
            for (var i = 0; i < samples.Length; i++)
            {
                values[i] = samples[i] / Fixed;
                min = Math.Min(min, values[i]);
                max = Math.Max(max, values[i]);
            }

            heights[index] = values;
            sloped[index] = max - min > 1e-9;
            if (cell.BaseHeight / Fixed <= OutOfBoundsHeight)
                live[index] = true;
            else
                omitted++;
        }

        // Bounds over every projected corner (plus a little below, for the skirts).
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var index = 0; index < live.Length; index++)
        {
            if (!live[index])
                continue;
            var gx = index % w;
            var gy = index / w;
            var values = heights[index];
            var lowest = double.MaxValue;
            foreach (var v in values)
                lowest = Math.Min(lowest, v);
            foreach (var (i, j) in (ReadOnlySpan<(int, int)>)[(0, 0), (n, 0), (0, n), (n, n)])
            {
                Track(Project(gx, gy, i, j, values[j * step + i]));
                Track(Project(gx, gy, i, j, lowest - 0.5));
            }

            continue;

            void Track((double X, double Y, double D) p)
            {
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }
        }

        if (minX > maxX)
            return null;

        const int pad = 10;
        var width = (int)(maxX - minX) + 2 * pad;
        var height = (int)(maxY - minY) + 2 * pad;
        if (width is <= 0 or > MaxDimension || height is <= 0 or > MaxDimension)
            return null;

        var raster = new Raster(width, height, -minX + pad, -minY + pad);

        // Back to front is not required (there is a depth buffer) but it keeps the
        // depth test doing less work.
        for (var diagonal = 0; diagonal <= w + h - 2; diagonal++)
        for (var gy = 0; gy < h; gy++)
        {
            var gx = diagonal - gy;
            if (gx < 0 || gx >= w)
                continue;
            var index = gy * w + gx;
            if (!live[index])
                continue;

            var baseColor = tint == TintMode.Material
                ? MaterialColor(materials[index])
                : sloped[index] ? new Vector3(214, 128, 60) : new Vector3(150, 152, 158);
            DrawCellTop(raster, gx, gy, heights[index], step, baseColor);
            DrawCellSkirts(raster, gx, gy, w, h, live, heights, step, baseColor);
        }

        return new GbaCollisionRender(width, height, raster.ToRgba(), omitted);
    }

    private static void DrawCellTop(Raster raster, int gx, int gy, double[] values, int step, Vector3 baseColor)
    {
        const int n = SubDivisions;
        for (var j = 0; j < n; j++)
        for (var i = 0; i < n; i++)
        {
            Span<(int I, int J)> quad = [(i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1)];
            Span<Vector3> world = stackalloc Vector3[4];
            Span<(double X, double Y, double D)> screen = stackalloc (double, double, double)[4];
            for (var k = 0; k < 4; k++)
            {
                var (qi, qj) = quad[k];
                var hv = values[qj * step + qi];
                world[k] = new Vector3(
                    (float)((gx + (double)qi / n) * 3.0), (float)((gy + (double)qj / n) * 3.0), (float)hv);
                screen[k] = raster.Place(Project(gx, gy, qi, qj, hv));
            }

            foreach (var (a, b, c) in (ReadOnlySpan<(int, int, int)>)[(0, 1, 2), (0, 2, 3)])
            {
                var normal = Vector3.Cross(world[b] - world[a], world[c] - world[a]);
                if (normal.LengthSquared() < 1e-18f)
                    normal = Vector3.UnitZ;
                else
                    normal = Vector3.Normalize(normal);
                if (normal.Z < 0)
                    normal = -normal;
                var shade = 0.28 + 0.72 * Math.Max(0.0, Vector3.Dot(normal, Light));
                raster.Triangle(screen[a], screen[b], screen[c], baseColor * (float)shade);
            }
        }
    }

    // Vertical faces between a cell and each neighbour: down to the neighbour's own
    // surface inside the level, or a short lip at the boundary.
    private static void DrawCellSkirts(
        Raster raster, int gx, int gy, int w, int h, bool[] live, double[][] heights, int step, Vector3 baseColor)
    {
        const int n = SubDivisions;
        foreach (var (dx, dy) in (ReadOnlySpan<(int, int)>)[(1, 0), (0, 1), (-1, 0), (0, -1)])
        {
            var nx = gx + dx;
            var ny = gy + dy;
            var inside = nx >= 0 && nx < w && ny >= 0 && ny < h && live[ny * w + nx];
            var values = heights[gy * w + gx];
            var neighbour = inside ? heights[ny * w + nx] : null;
            var normal = Vector3.Normalize(new Vector3(dx, dy, 0));
            var shade = 0.28 + 0.72 * Math.Max(0.0, Vector3.Dot(normal, Light));
            var color = baseColor * (float)(0.78 * shade);

            for (var t = 0; t < n; t++)
            {
                (int I, int J) ea, eb;
                if (dx == 1) { ea = (n, t); eb = (n, t + 1); }
                else if (dx == -1) { ea = (0, t); eb = (0, t + 1); }
                else if (dy == 1) { ea = (t, n); eb = (t + 1, n); }
                else { ea = (t, 0); eb = (t + 1, 0); }

                var hi1 = values[ea.J * step + ea.I];
                var hi2 = values[eb.J * step + eb.I];
                double lo1, lo2;
                if (neighbour != null)
                {
                    var na = (I: ea.I - dx * n, J: ea.J - dy * n);
                    var nb = (I: eb.I - dx * n, J: eb.J - dy * n);
                    lo1 = neighbour[na.J * step + na.I];
                    lo2 = neighbour[nb.J * step + nb.I];
                }
                else
                {
                    lo1 = lo2 = Math.Min(hi1, hi2) - 0.5;
                }

                lo1 = Math.Min(lo1, hi1);
                lo2 = Math.Min(lo2, hi2);
                if (hi1 - lo1 < 1e-4 && hi2 - lo2 < 1e-4)
                    continue;

                var a = raster.Place(Project(gx, gy, ea.I, ea.J, hi1));
                var b = raster.Place(Project(gx, gy, eb.I, eb.J, hi2));
                var c = raster.Place(Project(gx, gy, eb.I, eb.J, lo2));
                var d = raster.Place(Project(gx, gy, ea.I, ea.J, lo1));
                raster.Triangle(a, b, c, color);
                raster.Triangle(a, c, d, color);
            }
        }
    }

    // Isometric projection; depth is the exact orthographic depth for this basis
    // (world X + Y + Z), larger meaning nearer.
    private static (double X, double Y, double D) Project(int gx, int gy, int i, int j, double height)
    {
        var fx = gx + (double)i / SubDivisions;
        var fy = gy + (double)j / SubDivisions;
        return ((fy - fx) * TileWidth / 2.0,
            (fx + fy) * TileHeight / 2.0 - height * HeightScale,
            fx * 3.0 + fy * 3.0 + height);
    }

    private static Vector3 MaterialColor(int material)
    {
        var m = ((material % MaterialPalette.Length) + MaterialPalette.Length) % MaterialPalette.Length;
        var packed = MaterialPalette[m];
        return new Vector3((packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF);
    }

    private static readonly int[] MaterialPalette =
    [
        0xA8AAB0, 0xCE986C, 0x76BE84, 0xC07676, 0x8A96CA, 0xD4BE76, 0x94CACA, 0xCA8ABE,
        0x80AC76, 0xB6B680, 0x9E80C0, 0xDEAC8A, 0x769EB6, 0xCACA9E, 0xAC8A80, 0x8AC0AC,
        0xE87662, 0x969696, 0xBED494, 0x6C8CA2, 0xD49ECA, 0x9ECA76, 0xB6809E, 0x80C0CA,
        0xCAB6B6, 0xACCA8A, 0x90A6C4, 0xCE907C, 0x7CC486, 0xC47C7C, 0xBABA86, 0x9A86C4,
        0x86A6BA, 0xCECEA6, 0xB09086, 0x90C4B0, 0xEC7C68
    ];

    /// <summary>A tiny depth-buffered triangle rasterizer, enough for this surface render.</summary>
    private sealed class Raster(int width, int height, double originX, double originY)
    {
        private readonly float[] _depth = CreateDepth(width * height);
        private readonly Vector3[] _color = new Vector3[width * height];

        public (double X, double Y, double D) Place((double X, double Y, double D) p) =>
            (p.X + originX, p.Y + originY, p.D);

        public void Triangle(
            (double X, double Y, double D) p0, (double X, double Y, double D) p1,
            (double X, double Y, double D) p2, Vector3 color)
        {
            var minX = Math.Max((int)Math.Min(p0.X, Math.Min(p1.X, p2.X)), 0);
            var maxX = Math.Min((int)Math.Max(p0.X, Math.Max(p1.X, p2.X)) + 2, width);
            var minY = Math.Max((int)Math.Min(p0.Y, Math.Min(p1.Y, p2.Y)), 0);
            var maxY = Math.Min((int)Math.Max(p0.Y, Math.Max(p1.Y, p2.Y)) + 2, height);
            if (minX >= maxX || minY >= maxY)
                return;

            var area = (p1.X - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (p1.Y - p0.Y);
            if (Math.Abs(area) < 1e-9)
                return;

            for (var y = minY; y < maxY; y++)
            for (var x = minX; x < maxX; x++)
            {
                var px = x + 0.5;
                var py = y + 0.5;
                var w0 = ((p1.X - p0.X) * (py - p0.Y) - (px - p0.X) * (p1.Y - p0.Y)) / area;
                var w1 = ((px - p0.X) * (p2.Y - p0.Y) - (p2.X - p0.X) * (py - p0.Y)) / area;
                var w2 = 1.0 - w0 - w1;
                if (w0 < -1e-6 || w1 < -1e-6 || w2 < -1e-6)
                    continue;
                var depth = (float)(w2 * p0.D + w1 * p1.D + w0 * p2.D);
                var at = y * width + x;
                if (depth <= _depth[at])
                    continue;
                _depth[at] = depth;
                _color[at] = color;
            }
        }

        public byte[] ToRgba()
        {
            var rgba = new byte[width * height * 4];
            for (var i = 0; i < _color.Length; i++)
            {
                var empty = _depth[i] <= float.MinValue / 2;
                var c = empty ? new Vector3(22, 24, 30) : _color[i];
                rgba[i * 4] = (byte)Math.Clamp(c.X, 0, 255);
                rgba[i * 4 + 1] = (byte)Math.Clamp(c.Y, 0, 255);
                rgba[i * 4 + 2] = (byte)Math.Clamp(c.Z, 0, 255);
                rgba[i * 4 + 3] = 0xFF;
            }

            return rgba;
        }

        private static float[] CreateDepth(int count)
        {
            var depth = new float[count];
            Array.Fill(depth, float.MinValue);
            return depth;
        }
    }
}
