using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static partial class PsxAnimDumpCommand
{

    // ─── Layer 1 helpers ────────────────────────────────────────────────

    private static void DumpHex(byte[] data, long offset, int length)
    {
        const int bytesPerLine = 16;
        for (var i = 0; i < length; i += bytesPerLine)
        {
            var lineLen = Math.Min(bytesPerLine, length - i);
            var hex = string.Join(" ", Enumerable.Range(0, lineLen)
                .Select(j => data[offset + i + j].ToString("X2")));
            var ascii = string.Concat(Enumerable.Range(0, lineLen)
                .Select(j =>
                {
                    var b = data[offset + i + j];
                    return b is >= 0x20 and < 0x7F ? (char)b : '.';
                }));
            AnsiConsole.MarkupLine($"  [grey]0x{offset + i:X6}[/] {hex,-47}  [dim]{Markup.Escape(ascii)}[/]");
        }
    }

    private static void DumpFirstU32s(byte[] data, long offset, int count)
    {
        AnsiConsole.MarkupLine("[grey]  First u32 values (LE):[/]");
        for (var i = 0; i < count; i++)
        {
            var off = offset + i * 4;
            var v = BitConverter.ToUInt32(data, (int)off);
            var sv = (int)v;
            var marker = "[grey]?[/]";
            if (v < 1024)
            {
                marker = "[green]small[/]";
            }
            else if (v > 0xFF000000)
            {
                marker = "[yellow]neg/ptr[/]";
            }
            AnsiConsole.MarkupLine($"  [grey]+0x{i * 4:X2}[/] u32=0x{v:X8} ({sv,12:N0})  {marker}");
        }
    }

    // ─── Layer 2: anim packet walk ──────────────────────────────────────

    /// <summary>
    ///     Tentatively walk the <c>PreProcessAnimPacket</c> structure starting
    ///     at <paramref name="offset" />. Returns the byte offset just past the
    ///     packet, or <paramref name="offset" /> unchanged if the structure
    ///     doesn't validate against <paramref name="meshCount" />.
    /// </summary>
    private static long TryWalkAnimPacket(byte[] data, long offset, int meshCount, bool verbose)
    {
        var pos = (int)offset;
        if (pos + 4 > data.Length) return offset;

        var groupCount = BitConverter.ToUInt32(data, pos);
        pos += 4;

        if (groupCount > 64)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]groupCount=0x{groupCount:X8} — too large; structure interpretation rejected.[/]");
            return offset;
        }

        AnsiConsole.MarkupLine($"  groupCount = {groupCount}");

        var totalAnims = 0;
        var maxAnimsPerGroup = 0;
        var meshIdxOutOfRange = 0;

        for (var g = 0; g < groupCount; g++)
        {
            if (pos + 12 > data.Length)
            {
                AnsiConsole.MarkupLine("  [yellow]Truncated mid-group.[/]");
                return offset;
            }

            // Per PreProcessAnimPacket: 2 words of group header, then animCount.
            var hdr0 = BitConverter.ToUInt32(data, pos);
            var hdr1 = BitConverter.ToUInt32(data, pos + 4);
            var animCount = BitConverter.ToUInt32(data, pos + 8);
            pos += 12;

            if (animCount > 256)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]group {g}: animCount={animCount} too large; bailing.[/]");
                return offset;
            }

            if (verbose || g < 4)
                AnsiConsole.MarkupLine(
                    $"  group {g}: hdr=0x{hdr0:X8} 0x{hdr1:X8} animCount={animCount}");

            for (var a = 0; a < animCount; a++)
            {
                if (pos + 8 > data.Length) return offset;
                var meshIdx = BitConverter.ToUInt32(data, pos);
                var aux = BitConverter.ToUInt32(data, pos + 4);
                pos += 8;

                if (meshIdx >= (uint)meshCount) meshIdxOutOfRange++;
                if (verbose && a < 4)
                    AnsiConsole.MarkupLine($"    anim {a}: meshIdx={meshIdx} aux=0x{aux:X8}");
            }

            totalAnims += (int)animCount;
            if ((int)animCount > maxAnimsPerGroup) maxAnimsPerGroup = (int)animCount;
        }

        AnsiConsole.MarkupLine(
            $"  [bold]→[/] {totalAnims} anims total, max/group={maxAnimsPerGroup}, " +
            $"meshIdx out-of-range={meshIdxOutOfRange}/{totalAnims}, ends at 0x{pos:X}");

        if (totalAnims > 0 && meshIdxOutOfRange == totalAnims)
        {
            AnsiConsole.MarkupLine(
                "  [yellow]All meshIdx values out of range — packet interpretation likely wrong.[/]");
            return offset;
        }

        return pos;
    }

    /// <summary>
    ///     Walk the hierarchy/animation chunk data used by <see cref="PsxAnimFile" />:
    ///     <list type="bullet">
    ///         <item>
    ///             <c>+0x00: u32 numEntries</c>
    ///         </item>
    ///         <item>
    ///             <c>+0x04 + i*8: per-animation entry</c>:
    ///             <list type="bullet">
    ///                 <item><c>+0x00: u32 poolOffset</c> (relative to chunk-data start)</item>
    ///                 <item>
    ///                     <c>+0x04: u16 frameCount</c>
    ///                 </item>
    ///                 <item><c>+0x06: u16 tweenFlag</c></item>
    ///             </list>
    ///         </item>
    ///         <item>Stream pool normally starts at <c>+0x04 + numEntries*8</c>.</item>
    ///     </list>
    /// </summary>
    private static HierLocation? TryWalkHierarchy(byte[] data, long startOffset, PshFile? psh, bool verbose)
    {
        if (startOffset + 4 > data.Length) return null;

        var pos = (int)startOffset;
        var numEntries = BitConverter.ToUInt32(data, pos);

        if (numEntries is 0 or > 4096)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]numEntries=0x{numEntries:X8} at 0x{pos:X} — implausible; structure mismatch.[/]");
            return null;
        }

        var entriesStart = pos + 4;
        var firstDataOffset = 4 + (int)numEntries * 8;
        var tableEnd = pos + firstDataOffset;
        if (tableEnd > data.Length)
        {
            AnsiConsole.MarkupLine(
                "  [yellow]Entry table would extend past EOF.[/]");
            return null;
        }

        var poolOffsets = new int[numEntries];
        var frameCounts = new int[numEntries];
        var tweenFlags = new int[numEntries];
        for (var i = 0; i < numEntries; i++)
        {
            poolOffsets[i] = (int)BitConverter.ToUInt32(data, entriesStart + i * 8);
            frameCounts[i] = BitConverter.ToUInt16(data, entriesStart + i * 8 + 4);
            tweenFlags[i] = BitConverter.ToUInt16(data, entriesStart + i * 8 + 6);
        }

        AnsiConsole.MarkupLine(
            $"  hierarchy data=0x{pos:X}  entries={numEntries}  tableEnd=0x{tableEnd:X}  " +
            (psh != null ? $"(psh has {psh.Bones.Count} bones)" : "(no .psh)"));

        // Sanity stats
        var maxFrames = frameCounts.Max();
        var minOffset = poolOffsets.Min();
        var maxOffset = poolOffsets.Max();
        var maxChunkRelativeOffset = data.Length - pos;
        var inRange = poolOffsets.Count(o => o >= firstDataOffset && o < maxChunkRelativeOffset);
        AnsiConsole.MarkupLine(
            $"  frameCounts: max={maxFrames:N0}  poolOffsets: min={minOffset:N0} max={maxOffset:N0}  " +
            $"in-range={inRange}/{numEntries}  chunk-relative span={maxChunkRelativeOffset:N0}");

        var firstFew = verbose ? (int)numEntries : Math.Min(8, (int)numEntries);
        for (var i = 0; i < firstFew; i++)
        {
            var name = psh?.GetBoneName(i) ?? $"anim_{i}";
            AnsiConsole.MarkupLine(
                $"  [grey]anim {i,3}[/] {name,-24}  poolOff=+0x{poolOffsets[i]:X6} ({poolOffsets[i],8:N0})  " +
                $"frames={frameCounts[i],4} tween={tweenFlags[i],3}");
        }

        return new HierLocation(pos, (int)numEntries, frameCounts, poolOffsets);
    }
}
