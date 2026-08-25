using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core;

internal static class FormatProbeArchive
{
    private const int CompressedPreHeaderSize = 12;

    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var ext = RecursiveUnpacker.GetArchiveExtension(filePath);
        return ext switch
        {
            ".wad" => ProbeWadArchive(filePath),
            ".pkr" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PKR3 Archive"),
            ".prx" => ProbeCompressedPreArchive(filePath),
            ".pre" => ProbePreArchive(filePath),
            ".prd" or ".prg" => ProbeLocalizedPre(filePath, "German"),
            ".prf" => ProbeLocalizedPre(filePath, "French"),
            ".ddx" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "DDX Archive"),
            ".bon" => ProbeBonArchive(filePath),
            ".pak" => ProbePakArchive(filePath),
            ".apk" => ProbePakArchive(filePath),
            ".zip" => QZipArchive.IsZip(filePath)
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "QTex ZIP")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "ZIP",
                    "Not a PKZip file (no local file header magic)"),
            ".cut" => CutArchive.IsCut(filePath)
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "CUT Cutscene Container")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "CUT",
                    "Not a cutscene file library (bad header or non-contiguous blobs)"),
            ".sdat" => Formats.Nds.SdatArchive.IsSdat(filePath)
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "SDAT Sound Archive")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "SDAT",
                    "Not a Nitro SDAT sound archive (bad header or block table)"),
            ".gob" => Formats.Gob.GobArchive.IsGobArchive(filePath)
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "GOB Container")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "GOB",
                    "No companion .gfc index, or it does not describe this file"),
            ".gba" => Formats.Gba.GbaLevelCarver.IsVvLevelRom(filePath)
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "GBA ROM (VV levels)")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "GBA ROM",
                    "No Vicarious Visions level table found (only THPS2 GBA carves)"),
            _ => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                $"Unrecognized archive format: {ext}")
        };
    }

    private static FormatProbe.FormatProbeResult ProbeWadArchive(string filePath)
    {
        return File.Exists(WadArchive.GetHedPath(filePath))
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "WAD Archive")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "WAD Archive",
                "Companion HED file not found");
    }

    private static FormatProbe.FormatProbeResult ProbeCompressedPreArchive(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(
                filePath,
                CompressedPreHeaderSize,
                out var header,
                out var bytesRead))
        {
            return HeaderReadFailure();
        }

        if (bytesRead < CompressedPreHeaderSize)
            return FileTooSmall();

        var version = BinaryProbeReader.ReadUInt32(header, 4);
        return version switch
        {
            0xABCD0002 or 0xABCD0003 => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                "Compressed PRE"),
            _ => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Compressed PRE",
                $"Invalid compressed PRE header version 0x{version:X8} " +
                "(expected 0xABCD0002 or 0xABCD0003)")
        };
    }

    private static FormatProbe.FormatProbeResult ProbePakArchive(string filePath)
    {
        return PakArchive.IsPakArchive(filePath)
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PAK Archive")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "PAK Raw Data",
                "PAK file without entry table (raw data, not an archive)");
    }

    private static FormatProbe.FormatProbeResult ProbeLocalizedPre(string filePath, string language)
    {
        var result = ProbePreArchive(filePath);
        return result with { FormatName = $"{result.FormatName} ({language})" };
    }

    private static FormatProbe.FormatProbeResult ProbePreArchive(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 8, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 8)
            return FileTooSmall();

        var version = BinaryProbeReader.ReadUInt32(header, 4);
        return version switch
        {
            0xABCD0002 or 0xABCD0003 => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported,
                "Compressed PRE"),
            _ => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PRE Archive")
        };
    }

    private static FormatProbe.FormatProbeResult ProbeBonArchive(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 8, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 8)
            return FileTooSmall();

        if (header[0] != (byte)'B' || header[1] != (byte)'o' || header[2] != (byte)'n' || header[3] != 0)
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                "Invalid BON magic");
        }

        var version = BinaryProbeReader.ReadUInt32(header, 4);
        return version switch
        {
            1 or 3 or 4 => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported,
                $"BON Archive (v{version})"),
            _ => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                $"BON (v{version})",
                $"Unsupported BON version {version} (supported: 1, 3, 4)")
        };
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
