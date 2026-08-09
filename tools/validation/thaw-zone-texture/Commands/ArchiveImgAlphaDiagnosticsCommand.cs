using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ThawZoneTexAnalyzer.Commands;

/// <summary>
///     Compares ordinal-matched THAW PS2 and PC IMG files while reading both PAKs
///     through ArchiveFileSystem. This is useful for asset PAKs whose per-IMG PC
///     checksums are zero and therefore cannot be joined by checksum.
/// </summary>
internal static class ArchiveImgAlphaDiagnosticsCommand
{
    public static Command Create()
    {
        var wadOption = new Option<string>("--wad") { Description = "Path to THAW PS2 DATAP.WAD." };
        wadOption.Required = true;
        var pakOption = new Option<string>("--pak") { Description = "WAD-relative PS2 PAK path." };
        pakOption.Required = true;
        var referencePakOption = new Option<string>("--reference-pak")
        {
            Description = "Path to the corresponding PC PAK."
        };
        referencePakOption.Required = true;
        var outputOption = new Option<string>("--output")
        {
            Description = "Output directory for CSV and discrepancy PNGs.",
            DefaultValueFactory = _ => Path.Combine("TestOutput", "thaw_archive_img_alpha")
        };

        var command = new Command("archive-img-alpha-diagnostics",
            "Compare alpha in ordinal-matched PS2/PC IMG entries directly from PAKs.");
        command.Options.Add(wadOption);
        command.Options.Add(pakOption);
        command.Options.Add(referencePakOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, _) => Task.FromResult(Run(
            parseResult.GetRequiredValue(wadOption),
            parseResult.GetRequiredValue(pakOption),
            parseResult.GetRequiredValue(referencePakOption),
            parseResult.GetValue(outputOption) ?? Path.Combine("TestOutput", "thaw_archive_img_alpha"))));
        return command;
    }

    private static int Run(string wadPath, string pakPath, string referencePakPath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        using var wad = ArchiveFileSystem.TryOpen(Path.GetFullPath(wadPath));
        if (wad == null)
            return Fail($"Could not open WAD: {wadPath}");

        var pakEntry = wad.FindByPath(pakPath) ?? wad.Entries.FirstOrDefault(entry =>
            entry.FullName.EndsWith(pakPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (pakEntry == null)
            return Fail($"PAK not found in WAD: {pakPath}");

        using var ps2Pak = wad.TryOpenNested(pakEntry);
        using var pcPak = ArchiveFileSystem.TryOpen(Path.GetFullPath(referencePakPath));
        if (ps2Pak == null || pcPak == null)
            return Fail("Could not open one of the PAKs through ArchiveFileSystem.");

        var ps2Entries = GetImgEntries(ps2Pak);
        var pcEntries = GetImgEntries(pcPak);
        Console.WriteLine($"PS2 IMG={ps2Entries.Count}; PC IMG={pcEntries.Count}");
        if (ps2Entries.Count != pcEntries.Count)
            return Fail("IMG entry counts differ; ordinal comparison would be unsafe.");

        var rows = new List<string>
        {
            "index,ps2_entry,pc_entry,ps2_sha256,pc_sha256,checksum,width,height,psm,cpsm,ps2_alpha_min,ps2_alpha_max,ps2_alpha_distinct,ps2_alpha_zero,ps2_alpha_partial,ps2_alpha_opaque,pc_alpha_min,pc_alpha_max,pc_alpha_distinct,pc_alpha_zero,pc_alpha_partial,pc_alpha_opaque,raw_clut_alpha_min,raw_clut_alpha_max,raw_clut_alpha_distinct,rgb_mad,alpha_mad"
        };
        var lostAlpha = 0;
        var alphaMatches = 0;
        var rgbMadTotal = 0.0;
        var comparable = 0;

        for (var index = 0; index < ps2Entries.Count; index++)
        {
            var ps2Data = ps2Pak.ReadEntry(ps2Entries[index]);
            var pcData = pcPak.ReadEntry(pcEntries[index]);
            var ps2Result = Ps2TexFile.Parse(ps2Data);
            var pcResult = ThawImgFile.Parse(pcData);
            if (!ps2Result.Success || ps2Result.Textures.Count != 1 || !pcResult.Success || pcResult.Textures.Count != 1)
            {
                Console.Error.WriteLine(
                    $"Parse failure at ordinal {index}: PS2={ps2Result.ErrorMessage}; PC={pcResult.ErrorMessage}");
                continue;
            }

            var ps2 = ps2Result.Textures[0];
            var pc = pcResult.Textures[0];
            if (ps2.Pixels == null || pc.Pixels == null)
                continue;

            var ps2Alpha = MeasureAlpha(ps2.Pixels);
            var pcAlpha = MeasureAlpha(pc.Pixels);
            var rawClutAlpha = MeasureRawClutAlpha(ps2Data);
            var mad = ComputeMad(ps2, pc);
            if (mad != null)
            {
                comparable++;
                rgbMadTotal += mad.Value.Rgb;
            }

            var losesAlpha = ps2Alpha.Distinct == 1 && ps2Alpha.Opaque > 0
                             && (pcAlpha.Zero > 0 || pcAlpha.Partial > 0);
            if (losesAlpha)
            {
                lostAlpha++;
            }
            if (losesAlpha || index < 3)
                SavePair(outputDirectory, index, ps2Entries[index].Name, ps2, pc);
            if (ps2Alpha == pcAlpha)
                alphaMatches++;

            var tex0 = ps2Data.Length >= 0x38 ? BitConverter.ToUInt64(ps2Data, 0x30) : 0;
            var psm = (uint)((tex0 >> 20) & 0x3F);
            var cpsm = (uint)((tex0 >> 51) & 0xF);
            rows.Add(string.Join(',',
                index, ps2Entries[index].Name, pcEntries[index].Name,
                Convert.ToHexString(SHA256.HashData(ps2Data)), Convert.ToHexString(SHA256.HashData(pcData)),
                $"{ps2.Checksum:X8}", ps2.Width, ps2.Height, psm, cpsm,
                ps2Alpha.Min, ps2Alpha.Max, ps2Alpha.Distinct, ps2Alpha.Zero, ps2Alpha.Partial, ps2Alpha.Opaque,
                pcAlpha.Min, pcAlpha.Max, pcAlpha.Distinct, pcAlpha.Zero, pcAlpha.Partial, pcAlpha.Opaque,
                rawClutAlpha?.Min.ToString(CultureInfo.InvariantCulture) ?? "",
                rawClutAlpha?.Max.ToString(CultureInfo.InvariantCulture) ?? "",
                rawClutAlpha?.Distinct.ToString(CultureInfo.InvariantCulture) ?? "",
                mad?.Rgb.ToString("F6", CultureInfo.InvariantCulture) ?? "",
                mad?.Alpha.ToString("F6", CultureInfo.InvariantCulture) ?? ""));
        }

        var reportPath = Path.Combine(outputDirectory, "archive_img_alpha_metrics.csv");
        File.WriteAllLines(reportPath, rows);
        Console.WriteLine(
            $"Comparable={comparable}; mean RGB MAD={(comparable == 0 ? 0 : rgbMadTotal / comparable):F3}; " +
            $"alpha histogram matches={alphaMatches}; opaque-PS2/translucent-PC={lostAlpha}");
        Console.WriteLine($"Wrote {rows.Count - 1} rows to {reportPath}");
        return 0;
    }

    private static List<ArchiveEntry> GetImgEntries(IArchiveFileSystem pak)
    {
        return pak.Entries.Where(static entry => entry.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static entry => entry.Offset)
            .ToList();
    }

    private static AlphaMetrics MeasureAlpha(byte[] pixels)
    {
        Span<int> histogram = stackalloc int[256];
        for (var offset = 3; offset < pixels.Length; offset += 4)
            histogram[pixels[offset]]++;
        var min = 0;
        var max = 0;
        var distinct = 0;
        var partial = 0;
        for (var alpha = 0; alpha < histogram.Length; alpha++)
        {
            if (histogram[alpha] == 0) continue;
            if (distinct == 0) min = alpha;
            max = alpha;
            distinct++;
            if (alpha is > 0 and < 255) partial += histogram[alpha];
        }
        return new AlphaMetrics(min, max, distinct, histogram[0], partial, histogram[255]);
    }

    private static AlphaMetrics? MeasureRawClutAlpha(byte[] data)
    {
        if (data.Length < 0x60 || BitConverter.ToUInt32(data) != 4 || BitConverter.ToUInt32(data, 0x38) != 97)
            return null;
        var tex0 = BitConverter.ToUInt64(data, 0x30);
        var psm = (uint)((tex0 >> 20) & 0x3F);
        var cpsm = (uint)((tex0 >> 51) & 0xF);
        if (psm is not (0x13 or 0x14) || cpsm != 0)
            return null;
        var dataRegionSize = (int)BitConverter.ToUInt32(data, 0x14);
        var paletteBytes = dataRegionSize - 192;
        const int paletteOffset = 0xC0;
        if (paletteBytes <= 0 || paletteOffset + paletteBytes > data.Length)
            return null;
        var alphaBytes = new byte[paletteBytes / 4 * 4];
        for (int source = paletteOffset + 3, target = 3;
             source < paletteOffset + paletteBytes;
             source += 4, target += 4)
            alphaBytes[target] = data[source];
        return MeasureAlpha(alphaBytes);
    }

    private static (double Rgb, double Alpha)? ComputeMad(Ps2Texture ps2, Ps2Texture pc)
    {
        if (ps2.Width != pc.Width || ps2.Height != pc.Height || ps2.Pixels == null || pc.Pixels == null)
            return null;
        long rgb = 0;
        long alpha = 0;
        for (var offset = 0; offset < ps2.Pixels.Length; offset += 4)
        {
            rgb += Math.Abs(ps2.Pixels[offset] - pc.Pixels[offset]);
            rgb += Math.Abs(ps2.Pixels[offset + 1] - pc.Pixels[offset + 1]);
            rgb += Math.Abs(ps2.Pixels[offset + 2] - pc.Pixels[offset + 2]);
            alpha += Math.Abs(ps2.Pixels[offset + 3] - pc.Pixels[offset + 3]);
        }
        var pixelCount = ps2.Width * ps2.Height;
        return (rgb / (pixelCount * 3.0), alpha / (double)pixelCount);
    }

    private static void SavePair(string outputDirectory, int index, string entryName, Ps2Texture ps2, Ps2Texture pc)
    {
        var stem = $"{index:D4}_{Path.GetFileNameWithoutExtension(entryName)}_{ps2.Checksum:X8}";
        using var ps2Image = Image.LoadPixelData<Rgba32>(ps2.Pixels!, ps2.Width, ps2.Height);
        using var pcImage = Image.LoadPixelData<Rgba32>(pc.Pixels!, pc.Width, pc.Height);
        ps2Image.SaveAsPng(Path.Combine(outputDirectory, stem + "_ps2.png"));
        pcImage.SaveAsPng(Path.Combine(outputDirectory, stem + "_pc.png"));
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private readonly record struct AlphaMetrics(int Min, int Max, int Distinct, int Zero, int Partial, int Opaque);
}
