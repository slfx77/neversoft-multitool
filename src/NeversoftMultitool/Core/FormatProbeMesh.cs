using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.XbxScene;

namespace NeversoftMultitool.Core;

internal static class FormatProbeMesh
{
    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var lower = name.ToLowerInvariant();

        if (lower.EndsWith(".skin.xbx") || lower.EndsWith(".mdl.xbx") ||
            lower.EndsWith(".skin.wpc") || lower.EndsWith(".mdl.wpc"))
        {
            return ProbeXbxSceneFile(filePath);
        }

        if (lower.EndsWith(".scn.xbx"))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Xbox Scene (SCN)",
                "Xbox SCN scene files are not yet supported");
        }

        if (lower.EndsWith(".scn.wpc"))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "PC Scene (SCN)",
                "PC SCN scene files are not yet supported");
        }

        if (lower.EndsWith(".skin.ps2") || lower.EndsWith(".mdl.ps2") || lower.EndsWith(".iskin.ps2"))
            return ProbePs2SceneFile(filePath);

        if (lower.EndsWith(".geom.ps2"))
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PS2 GEOM");

        if (lower.EndsWith(".col.xbx") || lower.EndsWith(".col.wpc") ||
            lower.EndsWith(".col.ps2") || lower.EndsWith(".col.psp"))
        {
            return ProbeColFile(filePath);
        }

        if (lower.EndsWith(".skin") && !lower.EndsWith(".ps2") && !lower.EndsWith(".xbx") && !lower.EndsWith(".wpc"))
            return ProbePakSkinFile(filePath);

        if (lower.EndsWith(".mdl") && !lower.EndsWith(".ps2") && !lower.EndsWith(".xbx") && !lower.EndsWith(".wpc"))
            return ProbePakMdlFile(filePath);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".ddm" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "DDM Mesh"),
            ".psx" => ProbePs1MeshFile(filePath),
            ".skn" => ProbeRwDffFile(filePath),
            ".bsp" => ProbeRwBspFile(filePath),
            _ => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                $"Unrecognized mesh format: {ext}")
        };
    }

    private static FormatProbe.FormatProbeResult ProbeXbxSceneFile(string filePath)
    {
        try
        {
            var data = new byte[48];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, Math.Min(data.Length, (int)fs.Length));
            if (bytesRead < 12)
                return FileTooSmall();

            if (ThawSceneFile.IsThawScene(data))
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "THAW Scene");

            if (XbxSceneFile.IsXbxScene(data))
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Xbox Scene");

            var v0 = BitConverter.ToUInt32(data, 0);
            var v1 = BitConverter.ToUInt32(data, 4);
            var v2 = BitConverter.ToUInt32(data, 8);
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Xbox Scene",
                $"Unsupported version ({v0},{v1},{v2}), expected (1,1,1) or THAW");
        }
        catch
        {
            return HeaderReadFailure();
        }
    }

    private static FormatProbe.FormatProbeResult ProbePs2SceneFile(string filePath)
    {
        try
        {
            var data = new byte[32];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < 12)
                return FileTooSmall();

            var matVer = BitConverter.ToUInt32(data, 0);
            var meshVer = BitConverter.ToUInt32(data, 4);
            var vertVer = BitConverter.ToUInt32(data, 8);

            if (matVer is 3 or 5 or 6 && meshVer is 4 or 6 && vertVer == 1)
            {
                var game = matVer switch
                {
                    3 => "THPS4",
                    5 => "THUG",
                    6 => "THUG2",
                    _ => "Unknown"
                };
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, $"PS2 Scene ({game})");
            }

            var fullData = bytesRead < data.Length ? data[..bytesRead] : data;
            if (ThawPs2SkinFile.IsThawPs2Skin(fullData, fs.Length))
            {
                return new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Supported,
                    "THAW/Pre-compiled PS2 Scene");
            }

            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown PS2 Scene",
                $"Unrecognized version triple ({matVer},{meshVer},{vertVer})");
        }
        catch
        {
            return HeaderReadFailure();
        }
    }

    private static FormatProbe.FormatProbeResult ProbePakSkinFile(string filePath)
    {
        try
        {
            var data = new byte[8192];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, Math.Min(data.Length, (int)fs.Length));
            if (bytesRead < 256)
                return FileTooSmall();

            var actual = data[..bytesRead];
            return ThawPs2SkinFile.IsPakSkin(actual)
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PAK Skin (THAW PS2)")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "Unknown .skin",
                    "Not a recognized PAK-extracted skin file");
        }
        catch
        {
            return HeaderReadFailure();
        }
    }

    private static FormatProbe.FormatProbeResult ProbePakMdlFile(string filePath)
    {
        try
        {
            const int probeBytes = 256 * 1024;
            var data = new byte[probeBytes];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, Math.Min(data.Length, (int)fs.Length));
            if (bytesRead < 256)
                return FileTooSmall();

            var actual = data[..bytesRead];
            return Ps2GeomFile.IsPakMdl(actual)
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PAK MDL (THAW PS2)")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "Unknown .mdl",
                    "Not a recognized PAK-extracted MDL file");
        }
        catch
        {
            return HeaderReadFailure();
        }
    }

    private static FormatProbe.FormatProbeResult ProbePs1MeshFile(string filePath)
    {
        try
        {
            var data = new byte[4];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < 4)
                return FileTooSmall();

            var version = BitConverter.ToUInt16(data, 0);
            var magic = BitConverter.ToUInt16(data, 2);
            if (magic == 0x0002 && version is 0x03 or 0x04 or 0x06)
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PSX Mesh");

            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                $"Not a valid PSX mesh (header: 0x{version:X4} 0x{magic:X4})");
        }
        catch
        {
            return HeaderReadFailure();
        }
    }

    private static FormatProbe.FormatProbeResult ProbeRwDffFile(string filePath)
    {
        try
        {
            var data = new byte[4];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < 4)
                return FileTooSmall();

            var chunkType = BitConverter.ToUInt32(data, 0);
            return chunkType == 0x0010
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "RW DFF Mesh")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "Unknown",
                    $"Not a valid RenderWare DFF file (header: 0x{chunkType:X8})");
        }
        catch
        {
            return HeaderReadFailure();
        }
    }

    private static FormatProbe.FormatProbeResult ProbeRwBspFile(string filePath)
    {
        try
        {
            var data = new byte[4];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < 4)
                return FileTooSmall();

            var chunkType = BitConverter.ToUInt32(data, 0);
            return chunkType == 0x000B
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "RW BSP Level")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "Unknown",
                    $"Not a valid RenderWare BSP file (header: 0x{chunkType:X8})");
        }
        catch
        {
            return HeaderReadFailure();
        }
    }

    private static FormatProbe.FormatProbeResult ProbeColFile(string filePath)
    {
        try
        {
            var data = new byte[4];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < 4)
                return FileTooSmall();

            var version = BitConverter.ToInt32(data, 0);
            return version is 9 or 10
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, $"COL Collision (v{version})")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    $"COL (v{version})",
                    $"Unsupported COL version {version} (supported: 9, 10)");
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
