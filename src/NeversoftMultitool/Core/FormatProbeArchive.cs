using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core;

internal static class FormatProbeArchive
{
    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var ext = RecursiveUnpacker.GetArchiveExtension(filePath);
        return ext switch
        {
            ".wad" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "WAD Archive"),
            ".pkr" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PKR3 Archive"),
            ".prx" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Compressed PRE"),
            ".pre" => ProbePreArchive(filePath),
            ".ddx" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "DDX Archive"),
            ".bon" => ProbeBonArchive(filePath),
            ".pak" => ProbePakArchive(filePath),
            _ => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                $"Unrecognized archive format: {ext}")
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

    private static FormatProbe.FormatProbeResult ProbePreArchive(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 8, out var header, out var bytesRead))
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PRE Archive");

        if (bytesRead < 8)
            return FileTooSmall();

        var version = BinaryProbeReader.ReadUInt32(header, 4);
        return version switch
        {
            0xABCD0002 or 0xABCD0003 => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Compressed PRE"),
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
            1 or 3 or 4 => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, $"BON Archive (v{version})"),
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
