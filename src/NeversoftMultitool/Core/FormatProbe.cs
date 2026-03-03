using NeversoftMultitool.Core.Formats.XbxScene;

namespace NeversoftMultitool.Core;

/// <summary>
///     Probes files to determine format support before processing.
///     Returns human-readable diagnostics for unsupported or partially supported formats.
/// </summary>
public static class FormatProbe
{
    public enum FormatSupport { Supported, Unsupported, PartiallySupported }

    public record FormatProbeResult(
        FormatSupport Support,
        string FormatName,
        string? UnsupportedReason = null);

    // --- Texture probing ---

    public static FormatProbeResult ProbeTexture(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var lower = name.ToLowerInvariant();

        // Multi-part extensions first
        if (lower.EndsWith(".tex.xbx") || lower.EndsWith(".tex.wpc"))
            return ProbeXbxTexFile(filePath);

        if (lower.EndsWith(".img.xbx") || lower.EndsWith(".img.wpc"))
            return ProbeXbxImgFile(filePath);

        if (lower.EndsWith(".tex.xen") || lower.EndsWith(".tex.ngc") ||
            lower.EndsWith(".tex.ps3") || lower.EndsWith(".tex.dat"))
            return new(FormatSupport.Unsupported, "Cross-Platform TEX",
                "GameCube/PS3 TEX textures are not yet supported");

        if (lower.EndsWith(".img.xen") || lower.EndsWith(".img.ps3"))
            return new(FormatSupport.Unsupported, "Cross-Platform IMG",
                "Xenon/PS3 IMG single textures are not yet supported");

        // PS2 compound extensions
        if (lower.EndsWith(".tex.ps2") || lower.EndsWith(".img.ps2"))
            return ProbePs2TexFile(filePath);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".psx" => new(FormatSupport.Supported, "PSX Texture"),
            ".tex" or ".img" => ProbePs2TexFile(filePath),
            ".pvr" => new(FormatSupport.Supported, "PVR Texture"),
            ".rle" or ".bmr" => new(FormatSupport.Supported, "RLE Bitmap"),
            ".tdx" or ".txx" => new(FormatSupport.Unsupported, "TDX Texture",
                "RenderWare TDX textures (THPS3) are not yet supported"),
            _ => new(FormatSupport.Unsupported, "Unknown",
                $"Unrecognized texture format: {ext}")
        };
    }

    private static FormatProbeResult ProbePs2TexFile(string filePath)
    {
        try
        {
            var data = new byte[12];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 12);
            if (bytesRead < 4)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            var version = BitConverter.ToUInt32(data, 0);
            return version switch
            {
                2 => new(FormatSupport.Supported, "PS2 IMG (v2)"),
                3 or 4 or 5 => new(FormatSupport.Supported, "PS2 TEX (v" + version + ")"),
                0x0016 => new(FormatSupport.Supported, "RenderWare TXD"),
                // THAW .tex.ps2 files have QB script data, not textures
                256 => new(FormatSupport.Unsupported, "THAW QB Data",
                    "THAW .tex.ps2 files contain script data, not textures"),
                _ => new(FormatSupport.Unsupported, $"PS2 TEX (v{version})",
                    $"Unsupported TEX version {version} (supported: 2-5)")
            };
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file header");
        }
    }

    private static FormatProbeResult ProbeXbxTexFile(string filePath)
    {
        try
        {
            var data = new byte[4];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 4);
            if (bytesRead < 4)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            var version = BitConverter.ToUInt32(data, 0);
            if (version == 1)
                return new(FormatSupport.Supported, "Xbox TEX");

            return new(FormatSupport.Unsupported, $"Xbox TEX (v{version})",
                $"Unsupported Xbox TEX version {version} (expected 1)");
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file header");
        }
    }

    private static FormatProbeResult ProbeXbxImgFile(string filePath)
    {
        try
        {
            var data = new byte[4];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 4);
            if (bytesRead < 4)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            var version = BitConverter.ToUInt32(data, 0);
            if (version == 2)
                return new(FormatSupport.Supported, "Xbox IMG");

            return new(FormatSupport.Unsupported, $"Xbox IMG (v{version})",
                $"Unsupported Xbox IMG version {version} (expected 2)");
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file header");
        }
    }

    private static FormatProbeResult ProbeXbxSceneFile(string filePath)
    {
        try
        {
            var data = new byte[12];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 12);
            if (bytesRead < 12)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            if (XbxSceneFile.IsXbxScene(data))
                return new(FormatSupport.Supported, "Xbox Scene");

            var v0 = BitConverter.ToUInt32(data, 0);
            var v1 = BitConverter.ToUInt32(data, 4);
            var v2 = BitConverter.ToUInt32(data, 8);
            return new(FormatSupport.Unsupported, "Xbox Scene",
                $"Unsupported version ({v0},{v1},{v2}), expected (1,1,1)");
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file header");
        }
    }

    // --- Mesh probing ---

    public static FormatProbeResult ProbeMesh(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var lower = name.ToLowerInvariant();

        // Cross-platform scene extensions (Xbox/PC)
        if (lower.EndsWith(".skin.xbx") || lower.EndsWith(".mdl.xbx"))
            return ProbeXbxSceneFile(filePath);

        if (lower.EndsWith(".scn.xbx"))
            return new(FormatSupport.Unsupported, "Xbox Scene (SCN)",
                "Xbox SCN scene files are not yet supported");

        if (lower.EndsWith(".skin.wpc") || lower.EndsWith(".mdl.wpc"))
            return ProbeXbxSceneFile(filePath);

        if (lower.EndsWith(".scn.wpc"))
            return new(FormatSupport.Unsupported, "PC Scene (SCN)",
                "PC SCN scene files are not yet supported");

        // PS2 scene extensions
        if (lower.EndsWith(".skin.ps2") || lower.EndsWith(".mdl.ps2") || lower.EndsWith(".iskin.ps2"))
            return ProbePs2SceneFile(filePath);

        // PS2 GEOM
        if (lower.EndsWith(".geom.ps2"))
            return new(FormatSupport.Supported, "PS2 GEOM");

        // COL collision files
        if (lower.EndsWith(".col.xbx") || lower.EndsWith(".col.wpc") ||
            lower.EndsWith(".col.ps2") || lower.EndsWith(".col.psp"))
            return ProbeColFile(filePath);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".ddm" => new(FormatSupport.Supported, "DDM Mesh"),
            ".psx" => ProbePs1MeshFile(filePath),
            ".skn" => ProbeRwDffFile(filePath),
            ".bsp" => ProbeRwBspFile(filePath),
            _ => new(FormatSupport.Unsupported, "Unknown",
                $"Unrecognized mesh format: {ext}")
        };
    }

    private static FormatProbeResult ProbePs2SceneFile(string filePath)
    {
        try
        {
            var data = new byte[12];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 12);
            if (bytesRead < 12)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            var matVer = BitConverter.ToUInt32(data, 0);
            var meshVer = BitConverter.ToUInt32(data, 4);
            var vertVer = BitConverter.ToUInt32(data, 8);

            // Valid version triples: (3,4,1), (5,6,1), (6,6,1)
            if (matVer is 3 or 5 or 6 && meshVer is 4 or 6 && vertVer == 1)
            {
                var game = matVer switch
                {
                    3 => "THPS4",
                    5 => "THUG",
                    6 => "THUG2",
                    _ => "Unknown"
                };
                return new(FormatSupport.Supported, $"PS2 Scene ({game})");
            }

            // THUG2 pre-compiled VIF/DMA format
            if (matVer == 1)
                return new(FormatSupport.PartiallySupported, "THUG2 Pre-compiled",
                    "Pre-compiled VIF/DMA format — use matching .iskin.ps2 file instead");

            // THAW .skin.ps2 — version triples read as garbage
            return new(FormatSupport.Unsupported, "THAW Scene",
                $"THAW .skin.ps2 format not yet supported (version {matVer},{meshVer},{vertVer})");
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file header");
        }
    }

    private static FormatProbeResult ProbePs1MeshFile(string filePath)
    {
        try
        {
            var data = new byte[4];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 4);
            if (bytesRead < 4)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            var version = BitConverter.ToUInt16(data, 0);
            var magic = BitConverter.ToUInt16(data, 2);

            if (magic == 0x0002 && version is 0x03 or 0x04 or 0x06)
                return new(FormatSupport.Supported, "PSX Mesh");

            return new(FormatSupport.Unsupported, "Unknown",
                $"Not a valid PSX mesh (header: 0x{version:X4} 0x{magic:X4})");
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file header");
        }
    }

    private static FormatProbeResult ProbeRwDffFile(string filePath)
    {
        try
        {
            var data = new byte[4];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 4);
            if (bytesRead < 4)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            var chunkType = BitConverter.ToUInt32(data, 0);
            if (chunkType == 0x0010) // RW_CLUMP
                return new(FormatSupport.Supported, "RW DFF Mesh");

            return new(FormatSupport.Unsupported, "Unknown",
                $"Not a valid RenderWare DFF file (header: 0x{chunkType:X8})");
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file header");
        }
    }

    private static FormatProbeResult ProbeRwBspFile(string filePath)
    {
        try
        {
            var data = new byte[4];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 4);
            if (bytesRead < 4)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            var chunkType = BitConverter.ToUInt32(data, 0);
            if (chunkType == 0x000B) // RW_WORLD
                return new(FormatSupport.Supported, "RW BSP Level");

            return new(FormatSupport.Unsupported, "Unknown",
                $"Not a valid RenderWare BSP file (header: 0x{chunkType:X8})");
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file header");
        }
    }

    private static FormatProbeResult ProbeColFile(string filePath)
    {
        try
        {
            var data = new byte[4];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 4);
            if (bytesRead < 4)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            var version = BitConverter.ToInt32(data, 0);
            if (version is 9 or 10)
                return new(FormatSupport.Supported, $"COL Collision (v{version})");

            return new(FormatSupport.Unsupported, $"COL (v{version})",
                $"Unsupported COL version {version} (supported: 9, 10)");
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file header");
        }
    }

    // --- Archive probing ---

    public static FormatProbeResult ProbeArchive(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".wad" => new(FormatSupport.Supported, "WAD Archive"),
            ".pkr" => new(FormatSupport.Supported, "PKR3 Archive"),
            ".prx" => new(FormatSupport.Supported, "Compressed PRE"),
            ".pre" => ProbePreArchive(filePath),
            ".ddx" => new(FormatSupport.Supported, "DDX Archive"),
            ".bon" => ProbeBonArchive(filePath),
            ".pak" => new(FormatSupport.Unsupported, "PAK Bundle",
                "PAK files are opaque level bundles, not traditional archives"),
            _ => new(FormatSupport.Unsupported, "Unknown",
                $"Unrecognized archive format: {ext}")
        };
    }

    private static FormatProbeResult ProbePreArchive(string filePath)
    {
        try
        {
            var data = new byte[8];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 8);
            if (bytesRead < 8)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            var version = BitConverter.ToUInt32(data, 4);
            return version switch
            {
                0xABCD0002 or 0xABCD0003 => new(FormatSupport.Supported, "Compressed PRE"),
                _ => new(FormatSupport.Supported, "PRE Archive")
            };
        }
        catch
        {
            return new(FormatSupport.Supported, "PRE Archive");
        }
    }

    private static FormatProbeResult ProbeBonArchive(string filePath)
    {
        try
        {
            var data = new byte[8];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 8);
            if (bytesRead < 8)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            // Check for "Bon\0" magic
            if (data[0] != (byte)'B' || data[1] != (byte)'o' || data[2] != (byte)'n' || data[3] != 0)
                return new(FormatSupport.Unsupported, "Unknown", "Invalid BON magic");

            var version = BitConverter.ToUInt32(data, 4);
            return version switch
            {
                1 or 3 or 4 => new(FormatSupport.Supported, $"BON Archive (v{version})"),
                _ => new(FormatSupport.Unsupported, $"BON (v{version})",
                    $"Unsupported BON version {version} (supported: 1, 3, 4)")
            };
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file header");
        }
    }

    // --- Audio probing ---

    public static FormatProbeResult ProbeAudio(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".adx" => ProbeAdxFile(filePath),
            ".xa" => new(FormatSupport.Supported, "XA Audio"),
            ".vab" => new(FormatSupport.Supported, "VAB Sound Bank"),
            ".vag" => new(FormatSupport.Supported, "VAG Audio"),
            ".kat" => new(FormatSupport.Supported, "KAT Sound Bank"),
            ".pss" => new(FormatSupport.Supported, "PSS Audio"),
            _ => ProbeHeaderlessAudio(filePath)
        };
    }

    private static FormatProbeResult ProbeAdxFile(string filePath)
    {
        try
        {
            var data = new byte[8];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 8);
            if (bytesRead < 6)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            // ADX magic: 0x8000 (big-endian)
            if (data[0] == 0x80 && data[1] == 0x00)
            {
                var encoding = data[4];
                if (encoding != 3)
                    return new(FormatSupport.Unsupported, "ADX Audio",
                        $"Unsupported ADX encoding type {encoding} (only type 3 supported)");
                return new(FormatSupport.Supported, "ADX Audio");
            }

            return new(FormatSupport.Unsupported, "Unknown",
                "Not a valid ADX file (missing 0x8000 magic)");
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file header");
        }
    }

    private static FormatProbeResult ProbeHeaderlessAudio(string filePath)
    {
        try
        {
            // Check for headerless SPU-ADPCM (VAG without header)
            var info = new FileInfo(filePath);
            if (info.Length > 0 && info.Length % 16 == 0)
                return new(FormatSupport.Supported, "Headerless SPU-ADPCM");

            return new(FormatSupport.Unsupported, "Unknown",
                $"Unrecognized audio format: {Path.GetExtension(filePath)}");
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file");
        }
    }

    // --- Video probing ---

    public static FormatProbeResult ProbeVideo(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".sfd" => new(FormatSupport.Supported, "SFD Video"),
            ".str" => ProbeStrFile(filePath),
            _ => new(FormatSupport.Unsupported, "Unknown",
                $"Unrecognized video format: {ext}")
        };
    }

    private static FormatProbeResult ProbeStrFile(string filePath)
    {
        try
        {
            // STR files must be multiples of 2336 bytes (PS1 CD-ROM sector size)
            var info = new FileInfo(filePath);
            if (info.Length == 0 || info.Length % 2336 != 0)
                return new(FormatSupport.Unsupported, "Unknown",
                    "Not a valid STR video (file size not a multiple of 2336-byte sectors)");

            // Read first sector subheader to verify audio flag
            var data = new byte[8];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, 8);
            if (bytesRead < 8)
                return new(FormatSupport.Unsupported, "Unknown", "File too small");

            return new(FormatSupport.Supported, "STR Video");
        }
        catch
        {
            return new(FormatSupport.Unsupported, "Unknown", "Failed to read file");
        }
    }

    // --- CLI helper ---

    /// <summary>
    ///     Probes a list of files, partitions into supported/unsupported, and returns counts.
    ///     The unsupported list contains (fileName, reason) pairs for warning output.
    /// </summary>
    public static (List<string> Supported, List<(string FileName, string Reason)> Unsupported)
        PartitionFiles(IEnumerable<string> files, Func<string, FormatProbeResult> probe)
    {
        var supported = new List<string>();
        var unsupported = new List<(string, string)>();

        foreach (var file in files)
        {
            var result = probe(file);
            if (result.Support == FormatSupport.Unsupported)
                unsupported.Add((Path.GetFileName(file), result.UnsupportedReason ?? "Unknown format"));
            else
                supported.Add(file);
        }

        return (supported, unsupported);
    }
}
