using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.XbxScene;

namespace NeversoftMultitool.Core;

internal static class FormatProbeTexture
{
    private static readonly string[] XboxTexSuffixes = [".tex.xbx", ".tex.wpc", ".stex"];
    private static readonly string[] XboxImgSuffixes = [".img.xbx", ".img.wpc"];
    private static readonly string[] CrossPlatformTexSuffixes = [".tex.xen", ".tex.ngc", ".tex.ps3", ".tex.dat"];
    private static readonly string[] CrossPlatformImgSuffixes = [".img.xen", ".img.ps3"];
    private static readonly string[] Ps2TextureSuffixes = [".tex.ps2", ".img.ps2"];

    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var name = Path.GetFileName(filePath);

        if (OrdinalFileName.HasAnySuffix(name, XboxTexSuffixes))
            return ProbeXbxTexFile(filePath);

        if (OrdinalFileName.HasAnySuffix(name, XboxImgSuffixes))
            return ProbeXbxImgFile(filePath);

        if (OrdinalFileName.HasAnySuffix(name, CrossPlatformTexSuffixes))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Cross-Platform TEX",
                "GameCube/PS3 TEX textures are not yet supported");
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
        return ext switch
        {
            ".psx" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PSX Texture"),
            ".tex" or ".img" => ProbePs2TexFile(filePath),
            ".pvr" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PVR Texture"),
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

        if (BinaryProbeReader.TryReadAllBytes(filePath, out var data)
            && ThawTexFile.TryFindEmbeddedDictionaryOffset(data, out var offset))
        {
            var formatName = offset == 0 ? "THAW PC TEX" : "THAW PC TEX (embedded)";
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, formatName);
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
