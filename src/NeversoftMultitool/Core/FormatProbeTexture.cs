using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.XbxScene;

namespace NeversoftMultitool.Core;

internal static class FormatProbeTexture
{
    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var lower = name.ToLowerInvariant();

        if (lower.EndsWith(".tex.xbx") || lower.EndsWith(".tex.wpc") || lower.EndsWith(".stex"))
            return ProbeXbxTexFile(filePath);

        if (lower.EndsWith(".img.xbx") || lower.EndsWith(".img.wpc"))
            return ProbeXbxImgFile(filePath);

        if (lower.EndsWith(".tex.xen") || lower.EndsWith(".tex.ngc") ||
            lower.EndsWith(".tex.ps3") || lower.EndsWith(".tex.dat"))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Cross-Platform TEX",
                "GameCube/PS3 TEX textures are not yet supported");
        }

        if (lower.EndsWith(".img.xen") || lower.EndsWith(".img.ps3"))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Cross-Platform IMG",
                "Xenon/PS3 IMG single textures are not yet supported");
        }

        if (lower.EndsWith(".tex.ps2") || lower.EndsWith(".img.ps2"))
            return ProbePs2TexFile(filePath);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
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
        try
        {
            var data = new byte[12];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < 4)
                return FileTooSmall();

            var version = BitConverter.ToUInt32(data, 0);
            var version16 = (ushort)(version & 0xFFFF);
            if (version16 == 6 && bytesRead >= 12)
            {
                var numTex = BitConverter.ToUInt32(data, 4);
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

            if (ThawZoneTexFile.IsThawZoneTex(File.ReadAllBytes(filePath)))
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "THAW Zone TEX");

            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                $"PS2 TEX (v{version})",
                $"Unsupported TEX version {version} (supported: 2-5)");
        }
        catch
        {
            return HeaderReadFailure();
        }
    }

    private static FormatProbe.FormatProbeResult ProbeXbxTexFile(string filePath)
    {
        try
        {
            var data = new byte[4];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < 4)
                return FileTooSmall();

            var version = BitConverter.ToUInt32(data, 0);
            if (version == 1)
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Xbox TEX");

            if (version == 0xABADD00D)
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "THAW PC TEX");

            if (ThawTexFile.TryFindEmbeddedDictionaryOffset(File.ReadAllBytes(filePath), out var offset))
            {
                var formatName = offset == 0 ? "THAW PC TEX" : "THAW PC TEX (embedded)";
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, formatName);
            }

            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                $"Xbox TEX (v{version})",
                $"Unsupported Xbox TEX version {version} (expected 1)");
        }
        catch
        {
            return HeaderReadFailure();
        }
    }

    private static FormatProbe.FormatProbeResult ProbeXbxImgFile(string filePath)
    {
        try
        {
            var data = new byte[8];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < 4)
                return FileTooSmall();

            var version = BitConverter.ToUInt32(data, 0);
            if (version == 2)
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Xbox IMG");

            if (version == 0xABADD00D)
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "THAW PC IMG");

            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                $"Xbox IMG (v{version})",
                $"Unsupported Xbox/PC IMG version {version} (expected 2 or 0xABADD00D)");
        }
        catch
        {
            return HeaderReadFailure();
        }
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
