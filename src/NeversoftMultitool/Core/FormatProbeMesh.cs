using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.XbxScene;

namespace NeversoftMultitool.Core;

internal static class FormatProbeMesh
{
    private static readonly string[] XboxSceneSuffixes = [".skin.xbx", ".mdl.xbx", ".skin.wpc", ".mdl.wpc"];
    private static readonly string[] Ps2SceneSuffixes = [".skin.ps2", ".mdl.ps2", ".iskin.ps2"];
    private static readonly string[] CollisionSuffixes = [".col.xbx", ".col.wpc", ".col.ps2", ".col.psp"];

    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var name = Path.GetFileName(filePath);

        if (OrdinalFileName.HasAnySuffix(name, XboxSceneSuffixes))
            return ProbeXbxSceneFile(filePath);

        if (OrdinalFileName.HasSuffix(name, ".scn.xbx"))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Xbox Scene (SCN)",
                "Xbox SCN scene files are not yet supported");
        }

        if (OrdinalFileName.HasSuffix(name, ".scn.wpc"))
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "PC Scene (SCN)",
                "PC SCN scene files are not yet supported");
        }

        if (OrdinalFileName.HasAnySuffix(name, Ps2SceneSuffixes))
            return ProbePs2SceneFile(filePath);

        if (OrdinalFileName.HasSuffix(name, ".geom.ps2"))
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PS2 GEOM");

        if (OrdinalFileName.HasAnySuffix(name, CollisionSuffixes))
            return ProbeColFile(filePath);

        if (OrdinalFileName.HasSuffix(name, ".skin")
            && !OrdinalFileName.HasAnySuffix(name, Ps2SceneSuffixes)
            && !OrdinalFileName.HasAnySuffix(name, XboxSceneSuffixes))
        {
            return ProbePakSkinFile(filePath);
        }

        if (OrdinalFileName.HasSuffix(name, ".mdl")
            && !OrdinalFileName.HasAnySuffix(name, Ps2SceneSuffixes)
            && !OrdinalFileName.HasAnySuffix(name, XboxSceneSuffixes))
        {
            return ProbePakMdlFile(filePath);
        }

        var ext = Path.GetExtension(filePath);
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
        if (!BinaryProbeReader.TryReadHeader(filePath, 48, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 12)
            return FileTooSmall();

        if (ThawSceneFile.IsThawScene(header))
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "THAW Scene");

        if (XbxSceneFile.IsXbxScene(header))
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Xbox Scene");

        var v0 = BinaryProbeReader.ReadUInt32(header);
        var v1 = BinaryProbeReader.ReadUInt32(header, 4);
        var v2 = BinaryProbeReader.ReadUInt32(header, 8);
        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Unsupported,
            "Xbox Scene",
            $"Unsupported version ({v0},{v1},{v2}), expected (1,1,1) or THAW");
    }

    private static FormatProbe.FormatProbeResult ProbePs2SceneFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 32, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 12)
            return FileTooSmall();

        var matVer = BinaryProbeReader.ReadUInt32(header);
        var meshVer = BinaryProbeReader.ReadUInt32(header, 4);
        var vertVer = BinaryProbeReader.ReadUInt32(header, 8);

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

        var fullData = bytesRead < header.Length ? header[..bytesRead] : header;
        if (ThawPs2SkinFile.IsThawPs2Skin(fullData, new FileInfo(filePath).Length))
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

    private static FormatProbe.FormatProbeResult ProbePakSkinFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 8192, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 256)
            return FileTooSmall();

        var actual = header[..bytesRead];
        return ThawPs2SkinFile.IsPakSkin(actual)
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PAK Skin (THAW PS2)")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown .skin",
                "Not a recognized PAK-extracted skin file");
    }

    private static FormatProbe.FormatProbeResult ProbePakMdlFile(string filePath)
    {
        const int probeBytes = 256 * 1024;
        if (!BinaryProbeReader.TryReadHeader(filePath, probeBytes, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 256)
            return FileTooSmall();

        var actual = header[..bytesRead];
        return Ps2GeomFile.IsPakMdl(actual)
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PAK MDL (THAW PS2)")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown .mdl",
                "Not a recognized PAK-extracted MDL file");
    }

    private static FormatProbe.FormatProbeResult ProbePs1MeshFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 4, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 4)
            return FileTooSmall();

        var version = BinaryProbeReader.ReadUInt16(header);
        var magic = BinaryProbeReader.ReadUInt16(header, 2);
        if (magic == 0x0002 && version is 0x03 or 0x04 or 0x06)
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PSX Mesh");

        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Unsupported,
            "Unknown",
            $"Not a valid PSX mesh (header: 0x{version:X4} 0x{magic:X4})");
    }

    private static FormatProbe.FormatProbeResult ProbeRwDffFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 4, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 4)
            return FileTooSmall();

        var chunkType = BinaryProbeReader.ReadUInt32(header);
        return chunkType == 0x0010
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "RW DFF Mesh")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                $"Not a valid RenderWare DFF file (header: 0x{chunkType:X8})");
    }

    private static FormatProbe.FormatProbeResult ProbeRwBspFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 4, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 4)
            return FileTooSmall();

        var chunkType = BinaryProbeReader.ReadUInt32(header);
        return chunkType == 0x000B
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "RW BSP Level")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                $"Not a valid RenderWare BSP file (header: 0x{chunkType:X8})");
    }

    private static FormatProbe.FormatProbeResult ProbeColFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 4, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 4)
            return FileTooSmall();

        var version = BitConverter.ToInt32(header, 0);
        return version is 9 or 10
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, $"COL Collision (v{version})")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                $"COL (v{version})",
                $"Unsupported COL version {version} (supported: 9, 10)");
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
