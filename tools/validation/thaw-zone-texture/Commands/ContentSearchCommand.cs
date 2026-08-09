using System.CommandLine;
using System.Globalization;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ThawZoneTexAnalyzer.Commands;

/// <summary>
///     Phase-3 A1 content search: given a GS-replay dump PNG (runtime ground truth)
///     for a zone-TEX checksum, sweep every plausible pixel-data offset in the zone
///     tex payload, decode PSMT4 with the record's own CLUT, and rank offsets by MAE
///     against the ground truth. Localizes WHERE the true prepared pixels live so the
///     owner-blob relocation error becomes an arithmetic fact rather than a guess.
///     Optionally sweeps CLUT offsets at the best pixel offset.
/// </summary>
internal static class ContentSearchCommand
{
    public static Command Create()
    {
        var pakArgument = new Argument<string>("pak") { Description = "Worldzone pak." };
        var checksumOption = new Option<string>("--checksum") { Description = "Target checksum (hex)." };
        checksumOption.Required = true;
        var referenceOption = new Option<string>("--reference")
        {
            Description = "Ground-truth PNG (GS replay dump image)."
        };
        referenceOption.Required = true;
        var outputOption = new Option<string>("--output")
        {
            DefaultValueFactory = _ => Path.Combine("TestOutput", "zone_tex_content_search")
        };

        var command = new Command("content-search",
            "Locate a zone-TEX record's true pixel data by sweeping offsets against a replay dump PNG.");
        command.Arguments.Add(pakArgument);
        command.Options.Add(checksumOption);
        command.Options.Add(referenceOption);
        command.Options.Add(outputOption);
        command.SetAction(parseResult => Run(
            parseResult.GetValue(pakArgument)!,
            parseResult.GetValue(checksumOption)!,
            parseResult.GetValue(referenceOption)!,
            parseResult.GetValue(outputOption)!));
        return command;
    }

    private static int Run(string pakPath, string checksumText, string referencePath, string outputDir)
    {
        var target = uint.Parse(
            checksumText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? checksumText[2..] : checksumText,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        var reference = LoadRgb(referencePath, out var refWidth, out var refHeight);
        var pakBytes = File.ReadAllBytes(pakPath);
        Directory.CreateDirectory(outputDir);

        foreach (var entry in PakArchive.GetTypedEntries(pakBytes))
        {
            if (entry.TypeHash is not (0x2B0A3095u or 0x8BFA5E8Eu))
                continue;
            var off = entry.Entry.Offset;
            var size = entry.Entry.Size;
            if (off < 0 || size <= 0 || off + size > pakBytes.Length)
                continue;
            var texBytes = new byte[size];
            Array.Copy(pakBytes, off, texBytes, 0, (int)size);
            if (!ThawZoneTexFile.IsThawZoneTex(texBytes))
                continue;

            var hit = ThawZoneTexFile.ParseHeaderEntries(texBytes)
                .Where(header => header.Checksum == target)
                .Cast<ThawZoneTexFile.ZoneTexHeaderEntry?>()
                .FirstOrDefault();
            if (hit == null)
                continue;

            Search(texBytes, hit.Value, reference, refWidth, refHeight, outputDir);
            StructuralSweep(texBytes, refWidth * refHeight / 2, refWidth, refHeight,
                reference, "texfile", outputDir, target);
            StructuralSweep(pakBytes, refWidth * refHeight / 2, refWidth, refHeight,
                reference, "pak", outputDir, target);
            return 0;
        }

        Console.WriteLine($"0x{target:X8} not found in any zone tex payload.");
        return 1;
    }

    private static void Search(
        byte[] texBytes,
        ThawZoneTexFile.ZoneTexHeaderEntry entry,
        byte[] reference,
        int refWidth,
        int refHeight,
        string outputDir)
    {
        var tex0 = entry.Tex0;
        var psm = (uint)((tex0 >> 20) & 0x3F);
        var tw = 1 << (int)((tex0 >> 26) & 0xF);
        var th = 1 << (int)((tex0 >> 30) & 0xF);
        if (psm != Ps2TexPixelDecoder.PSMT4 || tw != refWidth || th != refHeight)
        {
            Console.WriteLine($"Unsupported: psm=0x{psm:X2} {tw}x{th} vs reference {refWidth}x{refHeight}");
            return;
        }

        ThawZoneTexOwnerBlobDecoder.TryFindOwnerBlobHeader(
            texBytes, out var headerBase, out _, out _, out var baseA, out var baseB, out _);
        var pixelAbs = headerBase + (int)entry.CumulativeOffset + baseB;
        var clutAbs = headerBase + (int)entry.DataOffset + baseB;
        var pixelSize = tw * th / 2;

        var palette = ReadPsmct32Palette(texBytes, clutAbs);
        Console.WriteLine($"record: pixelAbs=0x{pixelAbs:X} clutAbs=0x{clutAbs:X} pixelSize=0x{pixelSize:X} " +
                          $"fileLen=0x{texBytes.Length:X} baseA=0x{baseA:X} baseB=0x{baseB:X}");
        Console.WriteLine("palette: " + string.Join(" ", Enumerable.Range(0, 16)
            .Select(i => $"{palette[i * 4]:X2}{palette[i * 4 + 1]:X2}{palette[i * 4 + 2]:X2}")));

        // Baseline: the shipping ownerblob read.
        var baselineMae = ScoreOffset(texBytes, pixelAbs, pixelSize, palette, tw, th, reference, unswizzle: true);
        Console.WriteLine($"MAE at shipping pixelAbs (unswizzled): {baselineMae:F2}");

        // Sweep all 16-byte-aligned offsets, both unswizzled and linear.
        var results = new List<(int Offset, bool Unswizzled, double Mae)>();
        for (var offset = 0; offset + pixelSize <= texBytes.Length; offset += 16)
        {
            results.Add((offset, true, ScoreOffset(texBytes, offset, pixelSize, palette, tw, th, reference, true)));
            results.Add((offset, false, ScoreOffset(texBytes, offset, pixelSize, palette, tw, th, reference, false)));
        }

        Console.WriteLine("\nTop 12 offsets by MAE:");
        foreach (var (offset, unswizzled, mae) in results.OrderBy(static r => r.Mae).Take(12))
        {
            var delta = offset - pixelAbs;
            Console.WriteLine($"  0x{offset:X8} {(unswizzled ? "unswz" : "linear")} mae={mae:F2} " +
                              $"delta-from-shipping={(delta >= 0 ? "+" : "-")}0x{Math.Abs(delta):X}");
        }

        // Render the best candidate for eyeballing.
        var best = results.OrderBy(static r => r.Mae).First();
        var bestRgba = DecodeOffset(texBytes, best.Offset, pixelSize, palette, tw, th, best.Unswizzled);
        var pngPath = Path.Combine(outputDir, $"{entry.Checksum:X8}_best_{best.Offset:X8}.png");
        File.WriteAllBytes(pngPath, ImageWriter.WritePngToMemory(tw, th, bestRgba));
        Console.WriteLine($"\nBest candidate rendered: {pngPath}");

        // Index-structure probe: render the shipping pixel read with a synthetic
        // grey ramp (index i -> grey i*17). Shows WHAT the indices depict,
        // independent of any CLUT error.
        var ramp = new byte[16 * 4];
        for (var i = 0; i < 16; i++)
        {
            ramp[i * 4] = ramp[i * 4 + 1] = ramp[i * 4 + 2] = (byte)(i * 17);
            ramp[i * 4 + 3] = 255;
        }

        foreach (var unswizzled in new[] { true, false })
        {
            var rampRgba = DecodeOffset(texBytes, pixelAbs, pixelSize, ramp, tw, th, unswizzled);
            var rampPath = Path.Combine(outputDir,
                $"{entry.Checksum:X8}_ramp_{(unswizzled ? "unswz" : "linear")}.png");
            File.WriteAllBytes(rampPath, ImageWriter.WritePngToMemory(tw, th, rampRgba));
            Console.WriteLine($"Index-ramp render ({(unswizzled ? "unswz" : "linear")}): {rampPath}");
        }

        // CLUT sweep: pixels fixed at the shipping offset, palette swept across
        // every 16-byte-aligned 64-byte window in the file.
        var clutResults = new List<(int Offset, bool Unswizzled, double Mae)>();
        for (var offset = 0; offset + 64 <= texBytes.Length; offset += 16)
        {
            var candidate = ReadPsmct32Palette(texBytes, offset);
            clutResults.Add((offset, true,
                ScoreOffsetWithPalette(texBytes, pixelAbs, pixelSize, candidate, tw, th, reference, true)));
            clutResults.Add((offset, false,
                ScoreOffsetWithPalette(texBytes, pixelAbs, pixelSize, candidate, tw, th, reference, false)));
        }

        Console.WriteLine("\nTop 12 CLUT offsets by MAE (pixels at shipping offset):");
        foreach (var (offset, unswizzled, mae) in clutResults.OrderBy(static r => r.Mae).Take(12))
        {
            var delta = offset - clutAbs;
            Console.WriteLine($"  0x{offset:X8} {(unswizzled ? "unswz" : "linear")} mae={mae:F2} " +
                              $"delta-from-shipping-clut={(delta >= 0 ? "+" : "-")}0x{Math.Abs(delta):X}");
        }

        var bestClut = clutResults.OrderBy(static r => r.Mae).First();
        var bestClutRgba = DecodeOffset(texBytes, pixelAbs, pixelSize,
            ReadPsmct32Palette(texBytes, bestClut.Offset), tw, th, bestClut.Unswizzled);
        var clutPngPath = Path.Combine(outputDir, $"{entry.Checksum:X8}_bestclut_{bestClut.Offset:X8}.png");
        File.WriteAllBytes(clutPngPath, ImageWriter.WritePngToMemory(tw, th, bestClutRgba));
        Console.WriteLine($"Best-CLUT candidate rendered: {clutPngPath}");
    }

    /// <summary>
    ///     Palette-free structural sweep over an arbitrary byte buffer (whole pak):
    ///     for each candidate offset and layout, build the index image and score how
    ///     much of the reference luminance variance the 16 index classes explain
    ///     (ANOVA R-squared). A true content match scores near 1.0 regardless of CLUT.
    /// </summary>
    internal static void StructuralSweep(
        byte[] haystack,
        int pixelSize,
        int tw,
        int th,
        byte[] reference,
        string label,
        string outputDir,
        uint checksum)
    {
        // Reference luminance, sampled every 2nd pixel each way.
        var refLuma = new double[(th / 2) * (tw / 2)];
        double totalMean = 0;
        var sampleCount = 0;
        for (var y = 0; y < th; y += 2)
        {
            for (var x = 0; x < tw; x += 2)
            {
                var i = (y * tw + x) * 4;
                var luma = 0.299 * reference[i] + 0.587 * reference[i + 1] + 0.114 * reference[i + 2];
                refLuma[sampleCount++] = luma;
                totalMean += luma;
            }
        }

        totalMean /= sampleCount;
        double totalVariance = 0;
        foreach (var luma in refLuma)
            totalVariance += (luma - totalMean) * (luma - totalMean);
        if (totalVariance < 1e-6)
        {
            Console.WriteLine("Reference has no luminance variance; structural sweep meaningless.");
            return;
        }

        var results = new List<(int Offset, bool Unswizzled, double R2)>();
        Span<double> classSum = stackalloc double[16];
        Span<int> classCount = stackalloc int[16];
        Span<double> classMean = stackalloc double[16];
        for (var offset = 0; offset + pixelSize <= haystack.Length; offset += 16)
        {
            foreach (var unswizzled in new[] { true, false })
            {
                var slice = new ReadOnlySpan<byte>(haystack, offset, pixelSize);
                var indices = unswizzled ? Ps2TexSwizzle.UnswizzlePsmt4(slice, tw, th) : slice.ToArray();

                classSum.Clear();
                classCount.Clear();
                var sample = 0;
                for (var y = 0; y < th; y += 2)
                {
                    var srcRow = th - 1 - y;
                    for (var x = 0; x < tw; x += 2)
                    {
                        var nibblePos = srcRow * tw + x;
                        var byteIdx = nibblePos >> 1;
                        var index = byteIdx < indices.Length
                            ? (nibblePos & 1) == 0 ? indices[byteIdx] & 0xF : indices[byteIdx] >> 4
                            : 0;
                        classSum[index] += refLuma[sample];
                        classCount[index]++;
                        sample++;
                    }
                }

                for (var i = 0; i < 16; i++)
                    classMean[i] = classCount[i] > 0 ? classSum[i] / classCount[i] : totalMean;

                double residual = 0;
                sample = 0;
                for (var y = 0; y < th; y += 2)
                {
                    var srcRow = th - 1 - y;
                    for (var x = 0; x < tw; x += 2)
                    {
                        var nibblePos = srcRow * tw + x;
                        var byteIdx = nibblePos >> 1;
                        var index = byteIdx < indices.Length
                            ? (nibblePos & 1) == 0 ? indices[byteIdx] & 0xF : indices[byteIdx] >> 4
                            : 0;
                        var diff = refLuma[sample] - classMean[index];
                        residual += diff * diff;
                        sample++;
                    }
                }

                results.Add((offset, unswizzled, 1.0 - residual / totalVariance));
            }
        }

        Console.WriteLine($"\n[{label}] Top 12 offsets by structural R2:");
        foreach (var (offset, unswizzled, r2) in results.OrderByDescending(static r => r.R2).Take(12))
            Console.WriteLine($"  0x{offset:X8} {(unswizzled ? "unswz" : "linear")} R2={r2:F4}");

        // Render the winner with per-class mean-luma greys so structure is visible.
        var best = results.OrderByDescending(static r => r.R2).First();
        var bestSlice = new ReadOnlySpan<byte>(haystack, best.Offset, pixelSize);
        var bestIndices = best.Unswizzled ? Ps2TexSwizzle.UnswizzlePsmt4(bestSlice, tw, th) : bestSlice.ToArray();
        var rgba = new byte[tw * th * 4];
        for (var y = 0; y < th; y++)
        {
            var srcRow = th - 1 - y;
            for (var x = 0; x < tw; x++)
            {
                var nibblePos = srcRow * tw + x;
                var byteIdx = nibblePos >> 1;
                var index = byteIdx < bestIndices.Length
                    ? (nibblePos & 1) == 0 ? bestIndices[byteIdx] & 0xF : bestIndices[byteIdx] >> 4
                    : 0;
                var dst = (y * tw + x) * 4;
                rgba[dst] = rgba[dst + 1] = rgba[dst + 2] = (byte)(index * 17);
                rgba[dst + 3] = 255;
            }
        }

        var structPath = Path.Combine(outputDir, $"{checksum:X8}_struct_{label}_{best.Offset:X8}.png");
        File.WriteAllBytes(structPath, ImageWriter.WritePngToMemory(tw, th, rgba));
        Console.WriteLine($"[{label}] Best structural candidate (index ramp): {structPath}");
    }

    private static double ScoreOffsetWithPalette(
        byte[] data, int offset, int pixelSize, byte[] palette, int tw, int th,
        byte[] reference, bool unswizzle)
    {
        var rgba = DecodeOffset(data, offset, pixelSize, palette, tw, th, unswizzle);
        double sum = 0;
        var samples = 0;
        for (var y = 0; y < th; y += 4)
        {
            for (var x = 0; x < tw; x += 4)
            {
                var i = (y * tw + x) * 4;
                sum += Math.Abs(rgba[i] - reference[i])
                       + Math.Abs(rgba[i + 1] - reference[i + 1])
                       + Math.Abs(rgba[i + 2] - reference[i + 2]);
                samples += 3;
            }
        }

        return sum / samples;
    }

    private static byte[] ReadPsmct32Palette(byte[] data, int clutAbs)
    {
        var palette = new byte[16 * 4];
        Array.Copy(data, clutAbs, palette, 0, Math.Min(64, data.Length - clutAbs));
        return palette;
    }

    private static double ScoreOffset(
        byte[] data, int offset, int pixelSize, byte[] palette, int tw, int th,
        byte[] reference, bool unswizzle)
    {
        var rgba = DecodeOffset(data, offset, pixelSize, palette, tw, th, unswizzle);
        double sum = 0;
        var samples = 0;
        // Sample every 4th pixel in each dimension for speed.
        for (var y = 0; y < th; y += 4)
        {
            for (var x = 0; x < tw; x += 4)
            {
                var i = (y * tw + x) * 4;
                sum += Math.Abs(rgba[i] - reference[i])
                       + Math.Abs(rgba[i + 1] - reference[i + 1])
                       + Math.Abs(rgba[i + 2] - reference[i + 2]);
                samples += 3;
            }
        }

        return sum / samples;
    }

    private static byte[] DecodeOffset(
        byte[] data, int offset, int pixelSize, byte[] palette, int tw, int th, bool unswizzle)
    {
        var slice = new ReadOnlySpan<byte>(data, offset, pixelSize);
        var indices = unswizzle ? Ps2TexSwizzle.UnswizzlePsmt4(slice, tw, th) : slice.ToArray();
        var rgba = new byte[tw * th * 4];
        // Bottom-up nibble walk, matching ThawZoneTexOwnerBlobDecoder.RenderPalettedLinear.
        for (var y = 0; y < th; y++)
        {
            var sourceRow = th - 1 - y;
            for (var x = 0; x < tw; x++)
            {
                var nibbleIndex = sourceRow * tw + x;
                var byteIndex = nibbleIndex >> 1;
                if (byteIndex >= indices.Length)
                    continue;
                var index = (nibbleIndex & 1) == 0 ? indices[byteIndex] & 0xF : indices[byteIndex] >> 4;
                var dst = (y * tw + x) * 4;
                var src = index * 4;
                rgba[dst] = palette[src];
                rgba[dst + 1] = palette[src + 1];
                rgba[dst + 2] = palette[src + 2];
                rgba[dst + 3] = 255;
            }
        }

        return rgba;
    }

    private static byte[] LoadRgb(string path, out int width, out int height)
    {
        using var image = Image.Load<Rgba32>(path);
        width = image.Width;
        height = image.Height;
        var rgba = new byte[width * height * 4];
        image.CopyPixelDataTo(rgba);
        return rgba;
    }
}
