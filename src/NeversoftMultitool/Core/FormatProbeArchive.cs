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
        try
        {
            var data = new byte[8];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < data.Length)
                return FileTooSmall();

            var version = BitConverter.ToUInt32(data, 4);
            return version switch
            {
                0xABCD0002 or 0xABCD0003 => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Compressed PRE"),
                _ => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PRE Archive")
            };
        }
        catch
        {
            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PRE Archive");
        }
    }

    private static FormatProbe.FormatProbeResult ProbeBonArchive(string filePath)
    {
        try
        {
            var data = new byte[8];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < data.Length)
                return FileTooSmall();

            if (data[0] != (byte)'B' || data[1] != (byte)'o' || data[2] != (byte)'n' || data[3] != 0)
            {
                return new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "Unknown",
                    "Invalid BON magic");
            }

            var version = BitConverter.ToUInt32(data, 4);
            return version switch
            {
                1 or 3 or 4 => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, $"BON Archive (v{version})"),
                _ => new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    $"BON (v{version})",
                    $"Unsupported BON version {version} (supported: 1, 3, 4)")
            };
        }
        catch
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                "Failed to read file header");
        }
    }

    private static FormatProbe.FormatProbeResult FileTooSmall()
    {
        return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Unsupported, "Unknown", "File too small");
    }
}
