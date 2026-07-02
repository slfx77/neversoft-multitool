using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed partial class GsGifInterpreter
{
    private GsTexture? ResolveTexture(ulong tex0)
    {
        if (tex0 == 0)
            return null;

        var width = 1 << (int)((tex0 >> 26) & 0xF);
        var height = 1 << (int)((tex0 >> 30) & 0xF);
        var psm = (uint)((tex0 >> 20) & 0x3F);
        var cpsm = (uint)((tex0 >> 51) & 0xF);
        var texaSensitive = psm is Ps2TexPixelDecoder.PSMCT16 or Ps2GsVram.PSMCT16S
                                or Ps2GsVram.PSMZ16 or Ps2GsVram.PSMZ16S
                            || (psm is Ps2TexPixelDecoder.PSMT8 or Ps2TexPixelDecoder.PSMT4 &&
                                cpsm == Ps2TexPixelDecoder.PSMCT16);
        var cacheKey = new GsTextureCacheKey(tex0, texaSensitive ? state.Texa : 0);

        if (textureCache.TryGetValue(cacheKey, out var cached))
        {
            if (cached != null)
                renderAudit.TextureCacheHits++;
            return cached;
        }

        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
        {
            NoteUnsupported("texture_dimensions_out_of_range");
            textureCache[cacheKey] = null;
            return null;
        }

        GsTexture? texture = null;
        GsTexture? allAlphaVramTexture = null;
        string? textureSource = null;
        string? allAlphaVramTextureSource = null;
        uint? sourceChecksum = null;
        if (psm is Ps2TexPixelDecoder.PSMCT32
            or Ps2TexPixelDecoder.PSMCT24
            or Ps2TexPixelDecoder.PSMCT16
            or Ps2GsVram.PSMCT16S
            or Ps2TexPixelDecoder.PSMT8
            or Ps2TexPixelDecoder.PSMT4
            or Ps2GsVram.PSMZ32
            or Ps2GsVram.PSMZ24
            or Ps2GsVram.PSMZ16
            or Ps2GsVram.PSMZ16S)
        {
            if (psm == Ps2GsVram.PSMZ32)
                NoteApproximation("texture_psmz32_as_color");

            // RT cache fast path: when the sampled TBP overlaps a known per-(FBP,FBW,PSM)
            // surface, blit the surface's pixels into the sample at FBW-correct offsets
            // instead of going through the shared-VRAM swizzle (which produces FBW-aliasing
            // strips when one FBP was written at multiple FBWs — the THAW 1024×1024 bloom
            // compose pattern). Surfaces whose write FBW matches the sample's TBW are
            // preferred; the legacy any-FBW overlay only serves reads no matched surface
            // covers (see TryComposeSample).
            var tbpForCache = (uint)(tex0 & 0x3FFF);
            var tbwForCache = (uint)((tex0 >> 14) & 0x3F);
            var rgbaFromCache = renderTargetCache.TryComposeSample(tbpForCache, tbwForCache, width, height, psm);
            if (rgbaFromCache != null)
            {
                texture = new GsTexture(width, height, rgbaFromCache);
                textureSource = "rt_cache";
                NoteApproximation("texture_from_rt_cache");
            }

            if (texture == null)
            {
                var pixelsFromVram = ThawZoneTexVramSupport.DecodeFromTex0(
                    vram,
                    tex0,
                    false,
                    false,
                    texaSensitive ? state.Texa : null);
                if (pixelsFromVram != null)
                {
                    // Use VRAM decode whenever there's *any* pixel data (RGB or alpha).
                    // Distinguish two cases the renderer must handle differently:
                    //   1. Fully empty VRAM (all bytes zero) — texture was never uploaded;
                    //      let TextureResolver / ZoneTextureCatalog provide a fallback so
                    //      synthetic tests + dumps with missing uploads still see content.
                    //   2. RGB present but alpha=0 — game-intentional signal that this draw
                    //      shouldn't contribute (e.g. character rim lighting in inactive state).
                    //      PCSX2 SW uses VRAM as-is here.
                    // Prior over-aggressive external-fallback caused cyan "silver patch" on
                    // character backs by substituting static catalog RGB into case (2).
                    if (!IsAllPixelsZero(pixelsFromVram))
                    {
                        texture = new GsTexture(width, height, pixelsFromVram);
                        textureSource = IsAllAlphaZero(pixelsFromVram) ? "vram_all_alpha_zero" : "vram";
                        if (textureSource == "vram_all_alpha_zero")
                            NoteApproximation("texture_vram_all_alpha_zero");
                    }
                }
            }
        }
        else
        {
            NoteUnsupported($"texture_psm_0x{psm:X2}");
        }

        if (texture == null && options.TextureResolver != null)
        {
            var resolved = options.TextureResolver(tex0);
            if (resolved != null)
            {
                texture = new GsTexture(resolved.Width, resolved.Height, resolved.Rgba);
                textureSource = "external";
                sourceChecksum = resolved.Checksum;
            }
        }

        if (texture == null && allAlphaVramTexture != null)
        {
            texture = allAlphaVramTexture;
            textureSource = allAlphaVramTextureSource;
        }

        if (texture != null)
            RecordTextureDump(tex0, cacheKey.Texa, state.Context.Clamp, texture, textureSource ?? "unknown",
                sourceChecksum);

        textureCache[cacheKey] = texture;
        return texture;
    }

    private void RecordTextureDump(
        ulong tex0,
        ulong texa,
        ulong clamp,
        GsTexture texture,
        string source,
        uint? sourceChecksum)
    {
        if (options.TextureDumpSink == null)
            return;

        var region = DecodeTextureDumpRegion(clamp, texture.Width, texture.Height);
        var dumpPixels = CropTexture(texture.Rgba, texture.Width, region.X, region.Y, region.Width, region.Height);
        dumpPixels = ApplyTextureDumpAlphaPreview(tex0, dumpPixels);
        var sourceKey = ResolveTextureDumpSourceKey(tex0, source, out var classifiedSource);
        var contentHash = ComputeFnv1A32(dumpPixels);
        var key =
            $"{tex0:X16}|{texa:X16}|{classifiedSource}|{sourceKey}|{region.X},{region.Y},{region.Width}x{region.Height}|{contentHash:X8}";
        if (!dumpedTextureKeys.Add(key))
            return;

        var audit = MakeTextureDumpAuditRow(
            key,
            tex0,
            texa,
            texture,
            classifiedSource,
            sourceKey,
            contentHash,
            sourceChecksum,
            region,
            dumpPixels);
        audit.Path = options.TextureDumpSink(new GsRuntimeTextureDump(audit, dumpPixels));
        renderAudit.TextureDumps.Add(audit);
    }

    private string? ResolveTextureDumpSourceKey(ulong tex0, string source, out string classifiedSource)
    {
        classifiedSource = source;
        if (!source.StartsWith("vram", StringComparison.Ordinal))
            return null;

        if (TryFindFramebufferTextureSource(tex0, out var framebufferKey))
        {
            classifiedSource = "framebuffer";
            return framebufferKey;
        }

        if (TryFindImageUploadTextureSource(tex0, out var imageUploadKey))
        {
            classifiedSource = "image_upload";
            return imageUploadKey;
        }

        return null;
    }

    private bool TryFindFramebufferTextureSource(ulong tex0, out string sourceKey)
    {
        var tbp = (uint)(tex0 & 0x3FFF);
        var tbw = (uint)((tex0 >> 14) & 0x3F);
        var psm = (uint)((tex0 >> 20) & 0x3F);

        var row = renderAudit.FramebufferTargets.Values
            .Where(row => row.Fbp == tbp
                          && row.Fbw == tbw
                          && HasCompatibleFrameTextureLayout(row.Psm, psm))
            .OrderByDescending(row => row.PixelsWritten)
            .FirstOrDefault();
        if (row != null)
        {
            sourceKey = MakeFramebufferKey(row.Fbp, row.Fbw, row.Psm, row.Fbmsk);
            return true;
        }

        sourceKey = "";
        return false;
    }

    private bool TryFindImageUploadTextureSource(ulong tex0, out string sourceKey)
    {
        var tbp = (uint)(tex0 & 0x3FFF);
        var tbw = (uint)((tex0 >> 14) & 0x3F);
        var psm = (uint)((tex0 >> 20) & 0x3F);

        var row = renderAudit.ImageTransferTargets.Values
            .Where(row => row.Dbp == tbp
                          && row.Dbw == tbw
                          && HasCompatibleFrameTextureLayout(row.Dpsm, psm))
            .OrderByDescending(row => row.Bytes)
            .FirstOrDefault();
        if (row != null)
        {
            sourceKey =
                $"DBP={row.Dbp},DBW={row.Dbw},PSM=0x{row.Dpsm:X2},RECT={row.Width}x{row.Height}@{row.Dsax},{row.Dsay}";
            return true;
        }

        sourceKey = "";
        return false;
    }

    private static bool HasCompatibleFrameTextureLayout(uint sourcePsm, uint texturePsm)
    {
        var sourceLayout = GsPixelStorageLayout(sourcePsm);
        return sourceLayout >= 0 && sourceLayout == GsPixelStorageLayout(texturePsm);
    }

    private static int GsPixelStorageLayout(uint psm)
    {
        return psm switch
        {
            Ps2TexPixelDecoder.PSMCT32 or Ps2TexPixelDecoder.PSMCT24 or Ps2GsVram.PSMZ32 or Ps2GsVram.PSMZ24 => 32,
            Ps2TexPixelDecoder.PSMCT16 or Ps2GsVram.PSMCT16S => 16,
            Ps2TexPixelDecoder.PSMT8 => 8,
            Ps2TexPixelDecoder.PSMT4 => 4,
            _ => -1
        };
    }

    private static byte[] ApplyTextureDumpAlphaPreview(ulong tex0, byte[] rgba)
    {
        var tcc = (tex0 >> 34) & 0x1;
        if (tcc != 0)
            return rgba;

        var output = rgba.ToArray();
        for (var i = 3; i < output.Length; i += 4)
            output[i] = 255;

        return output;
    }

    private static (int X, int Y, int Width, int Height) DecodeTextureDumpRegion(ulong clamp, int textureWidth,
        int textureHeight)
    {
        var x0 = 0;
        var y0 = 0;
        var x1 = textureWidth - 1;
        var y1 = textureHeight - 1;
        if ((clamp & 0x3) == 2)
        {
            x0 = (int)((clamp >> 4) & 0x3FF);
            x1 = (int)((clamp >> 14) & 0x3FF);
        }

        if (((clamp >> 2) & 0x3) == 2)
        {
            y0 = (int)((clamp >> 24) & 0x3FF);
            y1 = (int)((clamp >> 34) & 0x3FF);
        }

        if (x1 < x0)
            (x0, x1) = (x1, x0);
        if (y1 < y0)
            (y0, y1) = (y1, y0);

        x0 = Math.Clamp(x0, 0, textureWidth - 1);
        x1 = Math.Clamp(x1, 0, textureWidth - 1);
        y0 = Math.Clamp(y0, 0, textureHeight - 1);
        y1 = Math.Clamp(y1, 0, textureHeight - 1);
        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private static byte[] CropTexture(byte[] rgba, int sourceWidth, int x, int y, int width, int height)
    {
        if (x == 0 && y == 0 && width == sourceWidth && rgba.Length == width * height * 4)
            return rgba;

        var output = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            var src = ((y + row) * sourceWidth + x) * 4;
            var dst = row * width * 4;
            rgba.AsSpan(src, width * 4).CopyTo(output.AsSpan(dst));
        }

        return output;
    }

    private static GsTextureDumpAuditRow MakeTextureDumpAuditRow(
        string key,
        ulong tex0,
        ulong texa,
        GsTexture texture,
        string source,
        string? sourceKey,
        uint contentHash,
        uint? sourceChecksum,
        (int X, int Y, int Width, int Height) region,
        byte[] dumpPixels)
    {
        return new GsTextureDumpAuditRow
        {
            Key = key,
            Source = source,
            SourceKey = sourceKey,
            Tex0 = $"0x{tex0:X16}",
            Texa = $"0x{texa:X16}",
            ContentHash = contentHash,
            SourceChecksum = sourceChecksum,
            Tbp = (uint)(tex0 & 0x3FFF),
            Tbw = (uint)((tex0 >> 14) & 0x3F),
            Psm = (uint)((tex0 >> 20) & 0x3F),
            TextureWidth = texture.Width,
            TextureHeight = texture.Height,
            RegionX = region.X,
            RegionY = region.Y,
            Width = region.Width,
            Height = region.Height,
            Tcc = (uint)((tex0 >> 34) & 0x1),
            Tfx = (uint)((tex0 >> 35) & 0x3),
            Cbp = (uint)((tex0 >> 37) & 0x3FFF),
            Cpsm = (uint)((tex0 >> 51) & 0xF),
            Csm = (uint)((tex0 >> 55) & 0x1),
            Csa = (uint)((tex0 >> 56) & 0x1F),
            Cld = (uint)((tex0 >> 61) & 0x7),
            AllAlphaZero = IsAllAlphaZero(dumpPixels)
        };
    }

    private static uint ComputeFnv1A32(ReadOnlySpan<byte> data)
    {
        var hash = 2166136261u;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
    }

    private static bool IsAllAlphaZero(byte[] rgba)
    {
        for (var i = 3; i < rgba.Length; i += 4)
        {
            if (rgba[i] != 0)
                return false;
        }

        return true;
    }

    private static bool IsAllPixelsZero(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i++)
        {
            if (rgba[i] != 0)
                return false;
        }

        return true;
    }

    private void WriteImageData(ReadOnlySpan<byte> data)
    {
        var xdir = (int)(state.Trxdir & 0x3);
        if (xdir != 0)
        {
            NoteUnsupported($"image_transfer_xdir_{xdir}");
            return;
        }

        var dbp = (uint)((state.Bitbltbuf >> 32) & 0x3FFF);
        var dbw = (uint)((state.Bitbltbuf >> 48) & 0x3F);
        var dpsm = (uint)((state.Bitbltbuf >> 56) & 0x3F);
        var rrw = (int)(state.Trxreg & 0xFFF);
        var rrh = (int)((state.Trxreg >> 32) & 0xFFF);
        var dsax = (int)((state.Trxpos >> 32) & 0x7FF);
        var dsay = (int)((state.Trxpos >> 48) & 0x7FF);
        if (rrw <= 0 || rrh <= 0)
        {
            NoteUnsupported("image_transfer_missing_trxreg");
            return;
        }

        var expectedBytes = ThawZoneTexVramSupport.GetTransferSizeBytes(dpsm, rrw, rrh);
        if (expectedBytes <= 0)
        {
            NoteUnsupported("image_transfer_empty");
            return;
        }

        var descriptor = new GsImageTransferDescriptor(dbp, dbw, dpsm, rrw, rrh, dsax, dsay, expectedBytes);
        if (activeImageTransfer == null || activeImageTransfer.Descriptor != descriptor)
        {
            if (activeImageTransfer is { BytesWritten: > 0 } interrupted)
                NoteUnsupported(
                    $"image_transfer_interrupted_{interrupted.BytesWritten}_of_{interrupted.ExpectedBytes}");

            activeImageTransfer = new GsImageTransfer(descriptor);
            renderAudit.ImageTransfersStarted++;
        }

        var transfer = activeImageTransfer;
        var remaining = transfer.ExpectedBytes - transfer.BytesWritten;
        var bytesToCopy = Math.Min(remaining, data.Length);
        data[..bytesToCopy].CopyTo(transfer.Buffer.AsSpan(transfer.BytesWritten));
        transfer.BytesWritten += bytesToCopy;
        renderAudit.ImageTransferBytes += bytesToCopy;

        if (data.Length > bytesToCopy)
            NoteUnsupported("image_transfer_extra_data_ignored");

        if (transfer.BytesWritten < transfer.ExpectedBytes)
            return;

        vram.WriteRect(dbp, dbw, dpsm, rrw, rrh, transfer.Buffer, dsax, dsay);
        RecordImageTransfer(descriptor);
        renderAudit.ImageTransfersCompleted++;
        activeImageTransfer = null;
        textureCache.Clear();
    }
}
