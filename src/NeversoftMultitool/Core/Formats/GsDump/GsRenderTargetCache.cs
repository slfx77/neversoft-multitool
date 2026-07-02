using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Core.Formats.GsDump;

/// <summary>
///     Per-(FBP, FBW, PSM) screen-space surface store. Mirrors framebuffer pixel writes
///     into a logical surface keyed by the destination tuple. Three uses:
///     <list type="bullet">
///         <item>
///             Texture samples whose TBP overlaps a surface can compose from the cache
///             (<see cref="TryComposeSample" />) instead of going through the shared VRAM
///             swizzle path, sidestepping FBW-aliasing artifacts on multi-FBW writes to
///             the same FBP (the THAW bloom-pyramid pattern).
///         </item>
///         <item>
///             <see cref="GsGifInterpreter" /> reads back per-pixel destination colors via
///             <see cref="TryReadScreenSpacePixel" /> when <c>BlendPixel</c>'s VRAM read
///             path isn't available (unsupported PSMs / Fbw=0).
///         </item>
///         <item>
///             PCRTC composition reads circuit framebuffers via
///             <see cref="TryGetSurface" />. Per-FBP isolation is what keeps the cyan HUD
///             overlay (FBP=11200) separate from the main scene (FBP=0) without
///             last-write-wins stomping (THAW magenta SPECIAL meter artifact).
///         </item>
///     </list>
///     Modeled on PCSX2 HW's per-(FBP, FBW) render-target cache.
/// </summary>
internal sealed class GsRenderTargetCache
{
    private readonly Dictionary<RtKey, RenderTargetSurface> _surfaces = [];

    /// <summary>
    ///     All cached surfaces in insertion order. Used by the diagnostic
    ///     <c>--dump-fbp-buffers</c> CLI option to inspect per-FBP screen-space content
    ///     without going through the VRAM swizzle path.
    /// </summary>
    public IEnumerable<(uint Fbp, uint Fbw, uint Psm, int Width, int Height, byte[] Rgba)> Surfaces
    {
        get
        {
            foreach (var (_, surface) in _surfaces)
            {
                if (surface.MaxYWritten < 0)
                    continue;
                yield return (surface.Fbp, surface.Fbw, surface.Psm, surface.Width,
                    surface.MaxYWritten + 1, surface.SnapshotRgba());
            }
        }
    }

    /// <summary>
    ///     Mirror a framebuffer pixel write to the matching surface. Creates the surface
    ///     on first write. PSMs not page-aligned in the GS register set are skipped.
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
    ///     Returns the stored RGBA at (x, y) of the surface keyed by (fbp, fbw, psm),
    ///     or false if no surface exists or the pixel hasn't been written. Used by
    ///     <c>BlendPixel</c>'s VRAM-unavailable fallback path: matches PCSX2 SW renderer's
    ///     behavior of reading Cd from the screen-space target color, not from raw VRAM.
    /// </summary>
    public bool TryReadScreenSpacePixel(uint fbp, uint fbw, uint psm, int x, int y,
        out (byte R, byte G, byte B, byte A) rgba)
    {
        rgba = default;
        if (fbw == 0 || x < 0 || y < 0)
            return false;
        if (!IsSupportedPsm(psm))
            return false;
        var key = new RtKey(fbp, fbw, psm);
        if (!_surfaces.TryGetValue(key, out var surface))
            return false;
        if (x >= surface.Width)
            return false;
        var pixelIdx = y * surface.Width + x;
        if (!surface.IsPixelWritten(pixelIdx))
            return false;
        var byteIdx = pixelIdx * 4;
        rgba = (surface.Rgba[byteIdx], surface.Rgba[byteIdx + 1],
            surface.Rgba[byteIdx + 2], surface.Rgba[byteIdx + 3]);
        return true;
    }

    /// <summary>
    ///     Returns the full per-(FBP, FBW, PSM) surface as a freshly-allocated RGBA8888
    ///     buffer plus its dimensions. Used by PCRTC composition in
    ///     <c>GsGifInterpreter.Present</c> to read circuit framebuffer content without
    ///     going through the VRAM swizzle path (which would not see writes that targeted
    ///     a different FBW on the same FBP). Returns false if no surface is registered.
    /// </summary>
    public bool TryGetSurface(uint fbp, uint fbw, uint psm,
        out byte[] rgba, out int width, out int height)
    {
        rgba = [];
        width = 0;
        height = 0;
        var key = new RtKey(fbp, fbw, psm);
        if (!_surfaces.TryGetValue(key, out var surface))
            return false;
        if (surface.MaxYWritten < 0)
            return false;
        rgba = surface.SnapshotRgba();
        width = surface.Width;
        height = surface.MaxYWritten + 1;
        return true;
    }

    /// <summary>
    ///     Try to compose the sampled region from existing render-target surfaces.
    ///     Returns RGBA8888 bytes of width*height pixels, or null if no surface
    ///     overlaps the sample's address range. The composition walks PSMCT-family
    ///     surfaces whose FBP block-address falls within the sample's range and
    ///     blits each surface's pixels into the matching offset of the output.
    /// </summary>
    /// <param name="baseRgba">
    ///     Optional VRAM-decoded base layer (tw*th*4 bytes). When provided, the compose
    ///     output starts from this content and TBW-matching surfaces overlay it — so
    ///     sample regions the per-FBW surfaces can't represent (cross-FBW stride
    ///     reinterpretation, never-rasterized uploads) keep the VRAM bytes instead of
    ///     collapsing to transparent black.
    /// </param>
    public byte[]? TryComposeSample(uint tbp, uint tbw, int tw, int th, uint tpsm, byte[]? baseRgba = null)
    {
        if (!IsSupportedPsm(tpsm) || tw <= 0 || th <= 0)
            return null;

        // Sample's page geometry — only handle PSMCT32-family for now (page 64x32).
        // PSMCT16 uses 64x64 page geometry so the offset math differs; defer for now.
        if (!IsPsmct32Family(tpsm))
            return null;

        // Page-row stride: TBW is the buffer width in 64-px units (= pages per row for
        // PSMCT32-family). TW only limits the sampling extent; when TBW is set it is the
        // authoritative VRAM row stride (e.g. the THAW display blit samples TW=1024 with
        // TBW=10 — rows stride 10 pages / 640 px, not 16).
        var samplePagesPerRow = tbw != 0 ? (int)tbw : tw / 64;
        var samplePageRows = th / 32;
        if (samplePagesPerRow <= 0 || samplePageRows <= 0)
            return null;

        // Sample's total page span (one page = 32 blocks). The sample reads VRAM block
        // addresses [tbp, tbp + samplePageRows * samplePagesPerRow * 32).
        var sampleBlocksTotal = (uint)(samplePagesPerRow * samplePageRows * 32);
        var sampleBlockEnd = tbp + sampleBlocksTotal;

        byte[]? output = null;
        if (baseRgba != null && baseRgba.Length == tw * th * 4)
            output = (byte[])baseRgba.Clone();
        var anyHit = false;

        foreach (var (_, surface) in _surfaces)
        {
            if (!IsPsmct32Family(surface.Psm))
                continue;

            // TBW-match: the sample's page-row stride must equal the surface's write
            // stride, or the blit places rows at wrong offsets. Multi-FBW FBPs (the THAW
            // bloom pyramid writes 0x2BC0 at both fbw10 and fbw4) otherwise overlay BOTH
            // layouts at offset 0 — misplacing content that the recursive bloom ping-pong
            // then amplifies into a phantom left-decaying haze composited over the scene.
            // PCSX2 HW keys its render targets by (FBP, FBW) for the same reason.
            if (tbw != 0 && surface.Fbw != tbw)
                continue;

            var surfacePagesTall = (surface.MaxYWritten + 32) / 32;
            if (surfacePagesTall <= 0)
                continue;
            var surfaceBlockStart = surface.Fbp;

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

    /// <summary>
    ///     Every PSM that <see cref="GsGifInterpreter.IsFramebufferPsmSupported" /> accepts
    ///     as a draw target. The surface stores post-write RGBA8888 regardless of source
    ///     PSM — Session 1 of the per-FBP-buffer refactor needs ALL framebuffer-targeting
    ///     PSMs tracked (not just PSMCT32 family) so PCRTC composition and BlendPixel Cd
    ///     reads can route through the per-FBP buffer for any draw target.
    /// </summary>
    private static bool IsSupportedPsm(uint psm)
    {
        return psm is Ps2TexPixelDecoder.PSMCT32
            or Ps2TexPixelDecoder.PSMCT24
            or Ps2TexPixelDecoder.PSMCT16
            or Ps2GsVram.PSMCT16S
            or Ps2GsVram.PSMZ32
            or Ps2GsVram.PSMZ24
            or Ps2GsVram.PSMZ16
            or Ps2GsVram.PSMZ16S;
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

        /// <summary>
        ///     Copy of the RGBA buffer trimmed to (Width, MaxYWritten+1) for external
        ///     consumers (PCRTC composition, diagnostic dumps). Returns a fresh array so
        ///     callers can't mutate the surface storage. The full allocated height may be
        ///     larger than the trim (lazy growth rounds up to RowChunkHeight chunks).
        /// </summary>
        public byte[] SnapshotRgba()
        {
            var height = MaxYWritten < 0 ? 0 : MaxYWritten + 1;
            var bytes = Width * height * 4;
            var copy = new byte[bytes];
            Array.Copy(Rgba, copy, bytes);
            return copy;
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
