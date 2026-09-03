using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Font;
using NeversoftMultitool.Core.Formats.Texture.Gba;
using NeversoftMultitool.Core.Formats.Texture.NextGen;
using NeversoftMultitool.Core.Formats.Texture.Ngc;
using NeversoftMultitool.Core.Formats.Texture.Pvr;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;

namespace NeversoftMultitool.Core;

internal static class FormatProbeTexture
{
    private static readonly string[] XboxTexSuffixes = [".tex.xbx", ".tex.wpc", ".stex"];
    private static readonly string[] XboxImgSuffixes = [".img.xbx", ".img.wpc"];
    private static readonly string[] NgcTexSuffixes =
        [".tex.stex.ngc", ".stex.ngc", ".tex.ngc", ".img.ngc"];
    private static readonly string[] N64TexSuffixes = [".tex.n64", ".img.n64"];
    private static readonly string[] CrossPlatformTexSuffixes =
        [".tex.xen", ".stex.xen", ".tex.ps3", ".stex.ps3", ".tex.dat"];
    // PSP suffixes route through the same parser: .tex.psp is the PS2 v5 TEX
    // format verbatim (GS swizzle + CSM1 CLUTs included) and .img.psp is
    // content-discriminated to the Remix/P8 PspImgFile inside Ps2TexFile.Parse.
    private static readonly string[] Ps2TextureSuffixes = [".tex.ps2", ".img.ps2", ".tex.psp", ".img.psp"];

    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var name = Path.GetFileName(filePath);

        if (Thps4PcDatImageFile.IsCandidateFileName(name))
            return ProbeThps4PcDatImageFile(filePath);

        if (Thps4PcDatTextureFile.IsCandidateFileName(name))
            return ProbeThps4PcDatTextureFile(filePath);

        if (OrdinalFileName.HasAnySuffix(name, XboxTexSuffixes))
            return ProbeXbxTexFile(filePath);

        if (OrdinalFileName.HasAnySuffix(name, XboxImgSuffixes))
            return ProbeXbxImgFile(filePath);

        if (IsNgcTextureFileName(name))
            return ProbeNgcTexFile(filePath);

        if (OrdinalFileName.HasAnySuffix(name, N64TexSuffixes))
            return ProbeN64TexFile(filePath);

        // A DS bank carries no pixels of its own: the records name sibling container
        // entries, so a loose file cannot be decoded and the probe says why rather
        // than reporting an unrecognised .bin.
        if (OrdinalFileName.HasSuffix(name, ".textureinfo.bin"))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                "DS texture bank",
                "Open the cart (.nds) or its .gob — the pixels are separate container entries");
        }

        if (IsNextGenTextureFileName(name))
            return ProbeNextGenTexFile(filePath);

        if (OrdinalFileName.HasSuffix(name, ".img.xen"))
            return ProbeXenImgFile(filePath);

        if (OrdinalFileName.HasSuffix(name, ".img.ps3"))
            return ProbePs3ImgFile(filePath);

        if (OrdinalFileName.HasAnySuffix(name, Ps2TextureSuffixes))
            return ProbePs2TexFile(filePath);

        var ext = Path.GetExtension(filePath);
        return ext.ToLowerInvariant() switch
        {
            ".psx" => ProbePsxFile(filePath),
            ".tex" or ".img" => ProbePs2TexFile(filePath),
            ".pvr" => ProbePvrFile(filePath),
            ".fnt" => ProbeFntFile(filePath),
            ".gba" => ProbeGbaRom(filePath),
            ".rle" or ".bmr" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "RLE Bitmap"),
            ".tdx" or ".txx" => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "TDX Texture",
                "RenderWare TDX textures (THPS3) are not yet supported"),
            _ => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                $"Unrecognized texture format: {ext}")
        };
    }

    /// <summary>
    ///     Shared name policy for the probe, GUI and CLI. THAW GameCube STEX
    ///     dictionaries use both <c>name.stex.ngc</c> and
    ///     <c>name.tex.stex.ngc</c> in the shipped corpus.
    /// </summary>
    internal static bool IsNgcTextureFileName(string fileName)
    {
        return OrdinalFileName.HasAnySuffix(fileName, NgcTexSuffixes);
    }

    /// <summary>
    ///     FACECAA7 dictionary suffixes accepted by both the probe and texture
    ///     viewer. This intentionally excludes the distinct next-gen IMG format.
    /// </summary>
    internal static bool IsNextGenTextureFileName(string fileName)
    {
        return OrdinalFileName.HasAnySuffix(fileName, CrossPlatformTexSuffixes);
    }

    private static FormatProbe.FormatProbeResult ProbeThps4PcDatTextureFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadAllBytes(filePath, out var data))
            return HeaderReadFailure();

        var parsed = Thps4PcDatTextureFile.Parse(data);
        return parsed.Success
            ? new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                parsed.Textures.Count == 0
                    ? "THPS4 PC TEX (empty dictionary)"
                    : $"THPS4 PC TEX ({parsed.Textures.Count} textures)")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "THPS4 PC TEX",
                parsed.ErrorMessage ?? "Not a complete THPS4 PC TEX dictionary");
    }

    private static FormatProbe.FormatProbeResult ProbeThps4PcDatImageFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadAllBytes(filePath, out var data))
            return HeaderReadFailure();

        var parsed = Thps4PcDatImageFile.Parse(data);
        if (!parsed.Success)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "THPS4 PC IMG",
                parsed.ErrorMessage ?? "Not a complete THPS4 PC IMG image");
        }

        var texture = parsed.Textures.Single();
        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Supported,
            $"THPS4 PC IMG ({texture.Width}x{texture.Height})");
    }

    /// <summary>
    ///     Validates content rather than trusting the extension: <c>.fnt</c> is shared with
    ///     unrelated THAW and THPS3-PS2 formats, so a scan must say which of them a file is
    ///     instead of listing it as supported and failing later.
    /// </summary>
    private static FormatProbe.FormatProbeResult ProbeFntFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadAllBytes(filePath, out var data))
            return HeaderReadFailure();

        return FntFile.IsFnt(data)
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Neversoft Bitmap Font")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Bitmap Font",
                "Not a PS1-era Neversoft bitmap font (THAW and THPS3-PS2 reuse the .fnt extension)");
    }

    private static FormatProbe.FormatProbeResult ProbePsxFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 4, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 4)
            return FileTooSmall();

        return PsxLibrary.IsValidMagic(header)
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PSX Texture")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "PSX Texture",
                "Not a valid PSX texture library (invalid magic)");
    }

    private static FormatProbe.FormatProbeResult ProbePvrFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[4];
            if (!TryReadExactly(stream, magic))
                return MalformedPvrHeader();

            long pvrtOffset;
            if (magic.SequenceEqual("PVRT"u8))
            {
                pvrtOffset = 0;
            }
            else if (magic.SequenceEqual("GBIX"u8))
            {
                Span<byte> sizeBytes = stackalloc byte[sizeof(uint)];
                if (!TryReadExactly(stream, sizeBytes))
                    return MalformedPvrHeader();

                var gbixDataSize = BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);
                pvrtOffset = 8L + gbixDataSize;
            }
            else
            {
                return MalformedPvrHeader();
            }

            const int pvrtHeaderSize = 16;
            if (pvrtOffset < 0 || pvrtOffset > stream.Length - pvrtHeaderSize)
                return MalformedPvrHeader();

            stream.Position = pvrtOffset;
            Span<byte> header = stackalloc byte[pvrtHeaderSize];
            if (!TryReadExactly(stream, header) || !header[..4].SequenceEqual("PVRT"u8))
                return MalformedPvrHeader();

            var pvrtDataSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var physicallyAvailable = stream.Length - pvrtOffset - 8;
            if (pvrtDataSize < 8 || pvrtDataSize > physicallyAvailable)
                return MalformedPvrHeader();

            var formatType = header[9] << 8;
            if (!PvrTextureDecoder.IsSupportedFormat(formatType))
            {
                return UnsupportedPvr(
                    $"Unsupported PVR texture layout 0x{formatType:X}");
            }

            var width = BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
            var height = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);
            if (width == 0 || height < 2)
                return UnsupportedPvr($"Invalid PVR texture dimensions {width}x{height}");

            var pixelCount = (long)width * height;
            if (pixelCount > Array.MaxLength / 4L)
            {
                return UnsupportedPvr(
                    $"PVR texture dimensions {width}x{height} exceed the maximum supported RGBA output size");
            }

            if (!TryGetRequiredPvrPayloadSize(formatType, width, height, out var requiredPayloadSize))
                return UnsupportedPvr($"Invalid PVR texture dimensions {width}x{height}");

            var declaredPayloadSize = (long)pvrtDataSize - 8;
            if (declaredPayloadSize < requiredPayloadSize)
                return MalformedPvrHeader();

            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                "PVR Texture");
        }
        catch
        {
            return HeaderReadFailure();
        }
    }

    private static bool TryGetRequiredPvrPayloadSize(
        int formatType,
        int width,
        int height,
        out long requiredPayloadSize)
    {
        var chunkSize = Math.Min(width, height);
        var mainTwiddledSize = 2L * chunkSize * chunkSize *
                               Math.Max(width / chunkSize, height / chunkSize);

        switch (formatType)
        {
            case 0x100:
            case 0xD00:
                requiredPayloadSize = mainTwiddledSize;
                return true;
            case 0x200:
                var mipLevelStartIndex = CalculatePvrMipLevelStartIndex(width);
                if (mipLevelStartIndex > int.MaxValue / 2)
                {
                    requiredPayloadSize = 0;
                    return false;
                }

                requiredPayloadSize = 2 * mipLevelStartIndex + mainTwiddledSize;
                return true;
            case 0x300:
            case 0x400:
                if (width < 2)
                {
                    requiredPayloadSize = 0;
                    return false;
                }

                var blockColumns = width / 2;
                var blockRows = height / 2;
                var mipLevelOffset = formatType == 0x400
                    ? CalculatePvrMipLevelStartIndex(width / 2)
                    : 0;
                // Interleaving is monotone in each nonnegative coordinate, so the
                // bottom-right block owns the greatest index byte the decoder reads.
                requiredPayloadSize = 0x800L + mipLevelOffset +
                                      MortonCurve.Interleave(blockColumns - 1, blockRows - 1) + 1;
                return true;
            case 0x900:
                requiredPayloadSize = 2L * width * height;
                return true;
            default:
                requiredPayloadSize = 0;
                return false;
        }
    }

    private static long CalculatePvrMipLevelStartIndex(int mipLevelDimension)
    {
        long startIndex = 1;
        while (mipLevelDimension > 0)
        {
            mipLevelDimension >>= 1;
            startIndex += (long)mipLevelDimension * mipLevelDimension;
        }

        return startIndex;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> destination)
    {
        try
        {
            stream.ReadExactly(destination);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private static FormatProbe.FormatProbeResult MalformedPvrHeader()
    {
        return UnsupportedPvr(
            "Not a valid PVR texture (invalid or truncated PVRT/GBIX header)");
    }

    private static FormatProbe.FormatProbeResult UnsupportedPvr(string reason)
    {
        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Unsupported,
            "PVR Texture",
            reason);
    }

    /// <summary>
    ///     A GBA ROM is a texture source only when it carries the full-screen
    ///     BIOS-LZ77 images the Vicarious Visions engine packs (THPS2 GBA). The scan
    ///     is content-based, so carts that moved their art off BIOS LZ77 (THPS3+)
    ///     report unsupported rather than listing empty.
    /// </summary>
    private static FormatProbe.FormatProbeResult ProbeGbaRom(string filePath)
    {
        if (!BinaryProbeReader.TryReadAllBytes(filePath, out var data))
            return HeaderReadFailure();

        // The Nintendo logo's first word (0x04) gates real GBA ROMs.
        if (data.Length < 0xC0 || data[0x04] != 0x24 || data[0x05] != 0xFF
            || data[0x06] != 0xAE || data[0x07] != 0x51)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported, "GBA ROM", "Not a GBA ROM (missing Nintendo logo)");
        }

        var count = GbaRomImages.ScanFullScreenImages(data).Count;
        return count > 0
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, $"GBA Image ({count} screens)")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "GBA ROM",
                "No full-screen BIOS-LZ77 images (this cart packs its art differently)");
    }

    /// <summary>
    ///     Xbox 360 loose images. Their descriptor may itself be raw-DEFLATE
    ///     wrapped, so recognition must decode first and validate the inner magic.
    /// </summary>
    private static FormatProbe.FormatProbeResult ProbeXenImgFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadAllBytes(filePath, out var data))
            return HeaderReadFailure();

        if (!XenImgFile.TryInspect(data, out var info, out var error))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Xbox 360 IMG",
                error);
        }

        if (!info.IsSupportedFormat || info.EndianMode != 1)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Xbox 360 IMG",
                $"Unsupported {info.FormatName}, endian mode {info.EndianMode}");
        }

        var wrapper = info.WasDeflated ? ", raw-DEFLATE" : "";
        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Supported,
            $"Xbox 360 IMG ({info.FormatName}{wrapper})");
    }

    /// <summary>
    ///     PS3 loose-image descriptors contain metadata only, so a file is
    ///     viewable only when its exact-size IMV/VRAM companion resolves too.
    ///     Missing and undersized companions fail closed: folder scans then keep
    ///     the actionable reason instead of adding an empty texture row.
    /// </summary>
    private static FormatProbe.FormatProbeResult ProbePs3ImgFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadAllBytes(filePath, out var descriptor))
            return HeaderReadFailure();

        if (!Ps3ImgFile.TryInspect(descriptor, out var info, out var error))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "PlayStation 3 IMG",
                error);
        }

        var payload = Ps3ImgPayloadLocator.Resolve(filePath, info.PayloadSize);
        return payload.Found
            ? new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                $"PlayStation 3 IMG ({info.FormatName})")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                $"PlayStation 3 IMG ({info.FormatName})",
                payload.Message);
    }

    /// <summary>
    ///     Next-gen FACECAA7 dictionaries. A PS3 file is reported as partially
    ///     supported when its VRAM twin cannot be found, because the dictionary
    ///     itself carries no pixels — that is a missing companion, not a bad file.
    /// </summary>
    private static FormatProbe.FormatProbeResult ProbeNextGenTexFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadAllBytes(filePath, out var data))
            return HeaderReadFailure();

        if (!Formats.Texture.NextGen.NextGenTexFile.TryProbe(data, out var isPs3, out var error))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Next-Gen TEX",
                error);
        }

        var name = Path.GetFileName(filePath);
        if (OrdinalFileName.HasSuffix(name, ".ps3") != isPs3
            && (OrdinalFileName.HasSuffix(name, ".ps3") || OrdinalFileName.HasSuffix(name, ".xen")))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Next-Gen TEX",
                "The filename platform suffix does not match the dictionary platform");
        }

        var vramPayload = isPs3
            ? Formats.Texture.NextGen.NextGenVramTwinLocator.TryLoad(filePath, data)
            : null;
        var parsed = Formats.Texture.NextGen.NextGenTexFile.Parse(data, vramPayload);
        if (!parsed.Success)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                isPs3 ? "Next-Gen TEX (PS3)" : "Next-Gen TEX (Xbox 360)",
                parsed.ErrorMessage ?? "Texture dictionary did not decode completely");
        }

        if (isPs3 && vramPayload == null)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.PartiallySupported,
                "Next-Gen TEX (PS3)",
                "Pixel data lives in a .tvx VRAM twin that was not found beside this file");
        }

        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Supported,
            isPs3 ? "Next-Gen TEX (PS3)" : "Next-Gen TEX (Xbox 360)");
    }

    private static FormatProbe.FormatProbeResult ProbeN64TexFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadAllBytes(filePath, out var data))
            return HeaderReadFailure();

        return Formats.Texture.N64.N64TexFile.IsN64Texture(data)
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "N64 Texture")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "N64 Texture",
                "Unrecognized N64 texture record");
    }

    private static FormatProbe.FormatProbeResult ProbePs2TexFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 32, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 4)
            return FileTooSmall();

        var version = BinaryProbeReader.ReadUInt32(header);

        if (Formats.Texture.Psp.PspImgFile.IsPspImg(header.AsSpan(0, bytesRead)))
        {
            var formatName = version == 4 ? "PSP IMG (Project 8)" : "PSP IMG (Remix)";
            if (!BinaryProbeReader.TryReadAllBytes(filePath, out var pspData))
                return HeaderReadFailure();

            var parsed = Formats.Texture.Psp.PspImgFile.Parse(pspData);
            return parsed.Success
                   && parsed.Textures.Count == 1
                   && parsed.Textures[0].Pixels != null
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, formatName)
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    formatName,
                    parsed.ErrorMessage ?? "PSP IMG contains no decodable texture");
        }

        // The PSP builds ship deliberate 4-byte all-zero placeholder files.
        if (version == 0 && bytesRead == 4)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "PSP TEX (empty stub)",
                "Authored 4-byte placeholder with no texture data");
        }

        var version16 = (ushort)(version & 0xFFFF);
        if (version16 == 6 && bytesRead >= 12)
        {
            var numTex = BinaryProbeReader.ReadUInt32(header, 4);
            if (numTex > 0 && numTex <= 100)
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "THAW Scene TEX (v6)");
        }

        if (version is 2)
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PS2 IMG (v2)");

        if (version is 3 or 4 or 5)
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, $"PS2 TEX (v{version})");

        if (version == 0x0016)
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "RenderWare TXD");

        if (version == 256)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "THAW QB Data",
                "THAW .tex.ps2 files contain script data, not textures");
        }

        if (!BinaryProbeReader.TryReadAllBytes(filePath, out var data))
            return HeaderReadFailure();

        if (ThawZoneTexFile.IsThawZoneTex(data))
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "THAW Zone TEX");

        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Unsupported,
            $"PS2 TEX (v{version})",
            $"Unsupported TEX version {version} (supported: 2-5)");
    }

    private static FormatProbe.FormatProbeResult ProbeXbxTexFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 4, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 4)
            return FileTooSmall();

        var version = BinaryProbeReader.ReadUInt32(header);
        if (version == 1)
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Xbox TEX");

        if (version == 0xABADD00D)
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "THAW PC TEX");

        if (BinaryProbeReader.TryReadAllBytes(filePath, out var data))
        {
            if (ThawTexFile.TryFindEmbeddedDictionaryOffset(data, out var offset))
            {
                var formatName = offset == 0 ? "THAW PC TEX" : "THAW PC TEX (embedded)";
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, formatName);
            }

            // THAW PS2 .stex zone textures share the extension with the PC DXT containers
            if (ThawZoneTexFile.IsThawZoneTex(data))
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "THAW Zone TEX");
        }

        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Unsupported,
            $"Xbox TEX (v{version})",
            $"Unsupported Xbox TEX version {version} (expected 1)");
    }

    private static FormatProbe.FormatProbeResult ProbeXbxImgFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 8, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 4)
            return FileTooSmall();

        var version = BinaryProbeReader.ReadUInt32(header);
        if (version == 2)
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Xbox IMG");

        if (version == 0xABADD00D)
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "THAW PC IMG");

        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Unsupported,
            $"Xbox IMG (v{version})",
            $"Unsupported Xbox/PC IMG version {version} (expected 2 or 0xABADD00D)");
    }

    private static FormatProbe.FormatProbeResult ProbeNgcTexFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadAllBytes(filePath, out var data))
            return HeaderReadFailure();

        var isBareRecord = NgcTexFile.IsBareRecord(data);
        var formatName = isBareRecord ? "NGC IMG" : "NGC TEX";
        var parsed = NgcTexFile.Parse(data);
        if (!parsed.Success)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                formatName,
                parsed.ErrorMessage ?? $"{formatName} did not decode completely");
        }

        if ((isBareRecord && parsed.Textures.Count != 1)
            || parsed.Textures.Any(static texture => texture.Pixels == null))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                formatName,
                $"{formatName} contains no complete decodable texture set");
        }

        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Supported,
            formatName);
    }

    private static FormatProbe.FormatProbeResult FileTooSmall()
    {
        return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Unsupported, "Unknown", "File too small");
    }

    private static FormatProbe.FormatProbeResult HeaderReadFailure()
    {
        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Unsupported,
            "Unknown",
            "Failed to read file header");
    }
}
