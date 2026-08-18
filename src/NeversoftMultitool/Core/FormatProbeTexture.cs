using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Font;
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
    private static readonly string[] NgcTexSuffixes = [".tex.ngc", ".img.ngc"];
    private static readonly string[] N64TexSuffixes = [".tex.n64", ".img.n64"];
    private static readonly string[] CrossPlatformTexSuffixes = [".tex.xen", ".tex.ps3", ".tex.dat"];
    private static readonly string[] CrossPlatformImgSuffixes = [".img.xen", ".img.ps3"];
    private static readonly string[] Ps2TextureSuffixes = [".tex.ps2", ".img.ps2"];

    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var name = Path.GetFileName(filePath);

        if (OrdinalFileName.HasAnySuffix(name, XboxTexSuffixes))
            return ProbeXbxTexFile(filePath);

        if (OrdinalFileName.HasAnySuffix(name, XboxImgSuffixes))
            return ProbeXbxImgFile(filePath);

        if (OrdinalFileName.HasAnySuffix(name, NgcTexSuffixes))
            return ProbeNgcTexFile(filePath);

        if (OrdinalFileName.HasAnySuffix(name, N64TexSuffixes))
            return ProbeN64TexFile(filePath);

        if (OrdinalFileName.HasAnySuffix(name, CrossPlatformTexSuffixes))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Cross-Platform TEX",
                "Xenon/PS3 cross-platform TEX textures are not yet supported");
        }

        if (OrdinalFileName.HasAnySuffix(name, CrossPlatformImgSuffixes))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Cross-Platform IMG",
                "Xenon/PS3 IMG single textures are not yet supported");
        }

        if (OrdinalFileName.HasAnySuffix(name, Ps2TextureSuffixes))
            return ProbePs2TexFile(filePath);

        var ext = Path.GetExtension(filePath);
        return ext.ToLowerInvariant() switch
        {
            ".psx" => ProbePsxFile(filePath),
            ".tex" or ".img" => ProbePs2TexFile(filePath),
            ".pvr" => ProbePvrFile(filePath),
            ".fnt" => ProbeFntFile(filePath),
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
        if (!BinaryProbeReader.TryReadHeader(filePath, 12, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 4)
            return FileTooSmall();

        var version = BinaryProbeReader.ReadUInt32(header);
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

        if (NgcTexFile.IsBareRecord(data))
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "NGC IMG");

        if (!NgcTexFile.TryReadHeader(data, out _, out var error))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "NGC TEX",
                error);
        }

        if (!NgcTexFile.HasSupportedFormatsOnly(data, out error))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "NGC TEX",
                error);
        }

        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Supported,
            "NGC TEX");
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
