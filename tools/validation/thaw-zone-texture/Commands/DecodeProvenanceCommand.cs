using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace ThawZoneTexAnalyzer.Commands;

/// <summary>
///     Phase-3 A1 decode-provenance probe: for a set of target checksums, find the
///     zone .tex/.stex payloads inside a worldzone pak that carry them, then run each
///     of the three decode tiers (owner-blob / header-data slots / upload snapshots)
///     INDEPENDENTLY on the record. Reports which tier the shipping first-wins merge
///     selects, whether the tiers disagree (pixel hash), and writes one PNG per tier
///     so a wrong-CLUT tier can be identified against the GS-oracle dump image.
/// </summary>
internal static class DecodeProvenanceCommand
{
    public static Command Create()
    {
        var pakArgument = new Argument<string>("pak")
        {
            Description = "Worldzone pak (e.g. z_bh.pak.ps2)."
        };
        var checksumsOption = new Option<string[]>("--checksum")
        {
            Description = "Target texture checksums (hex, e.g. 0x0935DD38).",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = true
        };
        checksumsOption.Required = true;
        var outputOption = new Option<string>("--output")
        {
            Description = "Directory for per-tier PNGs.",
            DefaultValueFactory = _ => Path.Combine("TestOutput", "zone_tex_provenance")
        };

        var command = new Command("decode-provenance",
            "Per-tier decode provenance for specific zone-TEX checksums.");
        command.Arguments.Add(pakArgument);
        command.Options.Add(checksumsOption);
        command.Options.Add(outputOption);
        command.SetAction(parseResult => Run(
            parseResult.GetValue(pakArgument)!,
            parseResult.GetValue(checksumsOption)!,
            parseResult.GetValue(outputOption)!));
        return command;
    }

    private static int Run(string pakPath, string[] checksumTexts, string outputDir)
    {
        var targets = checksumTexts
            .Select(static text => uint.Parse(
                text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
                NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ToHashSet();

        var pakBytes = File.ReadAllBytes(pakPath);
        if (!PakArchive.IsPakArchive(pakBytes))
        {
            Console.WriteLine($"Not a pak archive: {pakPath}");
            return 1;
        }

        Directory.CreateDirectory(outputDir);
        var found = new HashSet<uint>();

        foreach (var entry in PakArchive.GetTypedEntries(pakBytes))
        {
            if (entry.TypeHash is not (0x2B0A3095u /* .stex */ or 0x8BFA5E8Eu /* .tex */))
                continue;

            var off = entry.Entry.Offset;
            var size = entry.Entry.Size;
            if (off < 0 || size <= 0 || off + size > pakBytes.Length)
                continue;

            var texBytes = new byte[size];
            Array.Copy(pakBytes, off, texBytes, 0, (int)size);
            if (!ThawZoneTexFile.IsThawZoneTex(texBytes))
                continue;

            var headerEntries = ThawZoneTexFile.ParseHeaderEntries(texBytes);
            var hits = headerEntries.Where(header => targets.Contains(header.Checksum)).ToList();
            if (hits.Count == 0)
                continue;

            Console.WriteLine($"\n=== {Path.GetFileName(pakPath)}::{off:X8} ({size} bytes, {headerEntries.Count} records) ===");
            ProbeRecords(texBytes, hits, off, outputDir);
            foreach (var hit in hits)
                found.Add(hit.Checksum);
        }

        foreach (var missing in targets.Where(target => !found.Contains(target)))
            Console.WriteLine($"\nNOT FOUND in any zone tex payload: 0x{missing:X8}");
        return 0;
    }

    private static void ProbeRecords(
        byte[] texBytes,
        List<ThawZoneTexFile.ZoneTexHeaderEntry> hits,
        long payloadOffset,
        string outputDir)
    {
        var uploads = ThawZoneTexVramSupport.ParseVramUploads(texBytes);
        var hasOwnerBlob = ThawZoneTexOwnerBlobDecoder.TryFindOwnerBlobHeader(
            texBytes, out var headerOffset, out var primaryCount, out var secondaryCount,
            out _, out _, out _);
        var hasSlotLayout = ThawZoneTexFile.TryGetHeaderDataLayout(texBytes, out _, out _);
        Console.WriteLine(
            $"ownerBlob={(hasOwnerBlob ? $"@0x{headerOffset:X} prim={primaryCount} sec={secondaryCount}" : "NO")} " +
            $"slotLayout={(hasSlotLayout ? "yes" : "NO")} uploads={uploads.Count}");

        foreach (var hit in hits)
        {
            var psm = (hit.Tex0 >> 20) & 0x3F;
            var tw = 1 << (int)((hit.Tex0 >> 26) & 0xF);
            var th = 1 << (int)((hit.Tex0 >> 30) & 0xF);
            var cpsm = (hit.Tex0 >> 51) & 0xF;
            Console.WriteLine(
                $"\n0x{hit.Checksum:X8}: {tw}x{th} psm=0x{psm:X2} cpsm=0x{cpsm:X} " +
                $"layoutMode={hit.LayoutMode} mips={hit.MipLevelCount} group=0x{hit.GroupChecksum:X8}");
            Console.WriteLine(
                $"  dataOff=0x{hit.DataOffset:X} dataSize=0x{hit.DataSize:X} palBytes=0x{hit.PaletteBytes:X} " +
                $"uploadOff=0x{hit.UploadOffset:X} cumulOff=0x{hit.CumulativeOffset:X} basePix=0x{hit.BasePixelBytes:X}");

            var record = new List<ThawZoneTexFile.ZoneTexHeaderEntry> { hit };
            var tiers = new (string Name, List<Ps2Texture> Result)[]
            {
                ("ownerblob", ThawZoneTexOwnerBlobDecoder.DecodeAllRecords(texBytes, record)),
                ("slots", ThawZoneTexFile.DecodeFromHeaderDataSlots(texBytes, uploads, record)),
                ("uploads", ThawZoneTexCoreDecoder.DecodeEntriesFromUploadSnapshots(uploads, record))
            };

            string? shippingTier = null;
            foreach (var (name, result) in tiers)
            {
                var texture = result.FirstOrDefault(candidate => candidate.Checksum == hit.Checksum);
                if (texture?.Pixels == null)
                {
                    Console.WriteLine($"  {name,-10} -> (no decode)");
                    continue;
                }

                shippingTier ??= name;
                var pixelHash = Convert.ToHexString(SHA1.HashData(texture.Pixels))[..12];
                var opaque = CountOpaqueNearWhite(texture.Pixels);
                Console.WriteLine(
                    $"  {name,-10} -> {texture.Width}x{texture.Height} pixhash={pixelHash} " +
                    $"nearWhite={opaque.NearWhite}/{opaque.Total} ({100.0 * opaque.NearWhite / Math.Max(1, opaque.Total):F1}%)");

                var pngPath = Path.Combine(outputDir,
                    $"{hit.Checksum:X8}_{payloadOffset:X8}_{name}.png");
                File.WriteAllBytes(pngPath,
                    ImageWriter.WritePngToMemory(texture.Width, texture.Height, texture.Pixels));
            }

            Console.WriteLine($"  SHIPPING TIER: {shippingTier ?? "(none decodes this record!)"}");
        }
    }

    /// <summary>Washed-out-decode fingerprint: fraction of pixels that are near-white.</summary>
    private static (int NearWhite, int Total) CountOpaqueNearWhite(byte[] rgba)
    {
        var nearWhite = 0;
        var total = rgba.Length / 4;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i] >= 235 && rgba[i + 1] >= 235 && rgba[i + 2] >= 235)
                nearWhite++;
        }

        return (nearWhite, total);
    }
}
