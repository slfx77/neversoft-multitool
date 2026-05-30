using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Core.Formats.GsDump;

/// <summary>
///     Per-(FBP, FBW, PSM) render-target shadow cache. Mirrors framebuffer pixel writes
///     into a logical surface keyed by the destination tuple. Texture samples whose TBP
///     overlaps any surface can be composed from the cache instead of going through the
///     shared VRAM swizzle path, sidestepping FBW-aliasing artifacts that show up when
///     multiple draws write to the same FBP with different FBW values (a common THAW
///     bloom-pyramid pattern). Modeled on PCSX2 HW's per-(FBP, FBW) render-target cache.
/// </summary>
internal sealed class GsRenderTargetCache
{
    private readonly Dictionary<RtKey, RenderTargetSurface> _surfaces = [];

    /// <summary>
    ///     Mirror a framebuffer pixel write to the matching surface. Creates the surface
    ///     on first write. PSMs that are not page-aligned PSMCT-family (PSMT4/PSMT8/PSMZ16/16S)
    ///     are skipped — those are not used as render targets in THAW.
    /// </summary>
    public void WritePixel(uint fbp, uint fbw, uint psm, int x, int y, byte r, byte g, byte b, byte a)
    {
        if (fbw == 0 || x < 0 || y < 0)
            return;
        if (!IsSupportedPsm(psm))
            return;

        var key = new RtKey(fbp, fbw, psm);
        if (!_surfaces.TryGetValue(key, out var surface))
        {
            surface = new RenderTargetSurface(fbp, fbw, psm);
            _surfaces[key] = surface;
        }

        surface.Write(x, y, r, g, b, a);
    }

    /// <summary>
    ///     Try to compose the sampled region from existing render-target surfaces.
    ///     Returns RGBA8888 bytes of width*height pixels, or null if no surface
    ///     overlaps the sample's address range. The composition walks PSMCT-family
    ///     surfaces whose FBP block-address falls within the sample's range and
    ///     blits each surface's pixels into the matching offset of the output.
    /// </summary>
    public byte[]? TryComposeSample(uint tbp, int tw, int th, uint tpsm)
    {
        if (!IsSupportedPsm(tpsm) || tw <= 0 || th <= 0)
            return null;

        // Sample's page geometry — only handle PSMCT32-family for now (page 64x32).
        // PSMCT16 uses 64x64 page geometry so the offset math differs; defer for now.
        if (!IsPsmct32Family(tpsm))
            return null;

        var samplePagesPerRow = tw / 64;
        var samplePageRows = th / 32;
        if (samplePagesPerRow <= 0 || samplePageRows <= 0)
            return null;

        // Sample's total page span (one page = 32 blocks). The sample reads VRAM block
        // addresses [tbp, tbp + samplePageRows * samplePagesPerRow * 32).
        var sampleBlocksTotal = (uint)(samplePagesPerRow * samplePageRows * 32);
        var sampleBlockEnd = tbp + sampleBlocksTotal;

        byte[]? output = null;
        var anyHit = false;

        foreach (var (_, surface) in _surfaces)
        {
            if (!IsPsmct32Family(surface.Psm))
                continue;

            var surfacePagesTall = (surface.MaxYWritten + 32) / 32;
            if (surfacePagesTall <= 0)
                continue;
            var surfaceBlockStart = surface.Fbp;
            var surfaceBlockEnd = surface.Fbp + (uint)(surfacePagesTall * surface.Fbw * 32);

            // Surface starts before the sample, or after the sample's range — skip.
            if (surfaceBlockStart < tbp || surfaceBlockStart >= sampleBlockEnd)
                continue;

            var blockOffset = (int)(surfaceBlockStart - tbp);
            var pageOffset = blockOffset / 32;
            var samplePageRow = pageOffset / samplePagesPerRow;
            var samplePageCol = pageOffset % samplePagesPerRow;
            var sampleX0 = samplePageCol * 64;
            var sampleY0 = samplePageRow * 32;

            // Allocate output lazily so we don't pay for a 4MB buffer if no surface matches.
            output ??= new byte[tw * th * 4];

            var blitW = Math.Min(surface.Width, tw - sampleX0);
            var blitH = Math.Min(surface.MaxYWritten + 1, th - sampleY0);

            for (var y = 0; y < blitH; y++)
            {
                var dstY = sampleY0 + y;
                if (dstY < 0 || dstY >= th)
                    continue;
                for (var x = 0; x < blitW; x++)
                {
                    var srcPixelIdx = y * surface.Width + x;
                    if (!surface.IsPixelWritten(srcPixelIdx))
                        continue;
                    var dstX = sampleX0 + x;
                    if (dstX < 0 || dstX >= tw)
                        continue;
                    var srcByteIdx = srcPixelIdx * 4;
                    var dstByteIdx = (dstY * tw + dstX) * 4;
                    output[dstByteIdx] = surface.Rgba[srcByteIdx];
                    output[dstByteIdx + 1] = surface.Rgba[srcByteIdx + 1];
                    output[dstByteIdx + 2] = surface.Rgba[srcByteIdx + 2];
                    output[dstByteIdx + 3] = surface.Rgba[srcByteIdx + 3];
                    anyHit = true;
                }
            }
        }

        return anyHit ? output : null;
    }

    public void Reset()
    {
        _surfaces.Clear();
    }

    private static bool IsSupportedPsm(uint psm)
    {
        return psm is Ps2TexPixelDecoder.PSMCT32
            or Ps2TexPixelDecoder.PSMCT24
            or Ps2GsVram.PSMZ32
            or Ps2GsVram.PSMZ24;
    }

    private static bool IsPsmct32Family(uint psm)
    {
        return psm is Ps2TexPixelDecoder.PSMCT32
            or Ps2TexPixelDecoder.PSMCT24
            or Ps2GsVram.PSMZ32
            or Ps2GsVram.PSMZ24;
    }

    private readonly record struct RtKey(uint Fbp, uint Fbw, uint Psm);

    private sealed class RenderTargetSurface
    {
        private const int RowChunkHeight = 32;

        public uint Fbp { get; }
        public uint Fbw { get; }
        public uint Psm { get; }
        public int Width { get; }
        public byte[] Rgba { get; private set; }
        public int MaxYWritten { get; private set; } = -1;

        private bool[] _written;
        private int _allocatedHeight;

        public RenderTargetSurface(uint fbp, uint fbw, uint psm)
        {
            Fbp = fbp;
            Fbw = fbw;
            Psm = psm;
            Width = (int)(fbw * 64);
            _allocatedHeight = RowChunkHeight;
            Rgba = new byte[Width * _allocatedHeight * 4];
            _written = new bool[Width * _allocatedHeight];
        }

        public void Write(int x, int y, byte r, byte g, byte b, byte a)
        {
            if (x < 0 || y < 0 || x >= Width)
                return;
            if (y >= _allocatedHeight)
                Grow(y + 1);

            var pixelIdx = y * Width + x;
            var byteIdx = pixelIdx * 4;
            Rgba[byteIdx] = r;
            Rgba[byteIdx + 1] = g;
            Rgba[byteIdx + 2] = b;
            Rgba[byteIdx + 3] = a;
            _written[pixelIdx] = true;
            if (y > MaxYWritten)
                MaxYWritten = y;
        }

        public bool IsPixelWritten(int pixelIdx)
        {
            return pixelIdx >= 0 && pixelIdx < _written.Length && _written[pixelIdx];
        }

        private void Grow(int requiredHeight)
        {
            var newHeight = ((requiredHeight + RowChunkHeight - 1) / RowChunkHeight) * RowChunkHeight;
            var newRgba = new byte[Width * newHeight * 4];
            var newWritten = new bool[Width * newHeight];
            Array.Copy(Rgba, newRgba, Rgba.Length);
            Array.Copy(_written, newWritten, _written.Length);
            Rgba = newRgba;
            _written = newWritten;
            _allocatedHeight = newHeight;
        }
    }
}
