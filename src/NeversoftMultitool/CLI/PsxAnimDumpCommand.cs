using System.CommandLine;
using System.Text;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Research-grade diagnostic that probes a PS1 character <c>.psx</c> file
///     for animation data. Walks the file in four progressively deeper layers:
///     hex dump after the mesh boundary → anim packet walk → hierarchy walk →
///     decompressed bone-stream dump. Designed to be re-run on multiple samples
///     while the format is being locked down.
/// </summary>
public static class PsxAnimDumpCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a PSX character file"
        };
        var bytesOption = new Option<int>("--bytes")
        {
            Description = "Hex-dump window size after the mesh boundary",
            DefaultValueFactory = _ => 256
        };
        var animOption = new Option<int>("--anim")
        {
            Description = "Animation index to fully decompress in layer 4",
            DefaultValueFactory = _ => 0
        };
        var boneOption = new Option<int>("--bone")
        {
            Description = "Bone index within the animation to print frame-by-frame",
            DefaultValueFactory = _ => 0
        };
        var rankBoneOption = new Option<int?>("--rank-bone")
        {
            Description =
                "Diagnostic: decode all animation slots and rank them by this bone's translation span"
        };
        var rankTopOption = new Option<int>("--rank-top")
        {
            Description = "Number of ranked animation slots to print with --rank-bone",
            DefaultValueFactory = _ => 12
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Print every layer in full"
        };

        var command = new Command("psxanim",
            "Probe a PS1 character .psx file for animation data (research diagnostic)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(bytesOption);
        command.Options.Add(animOption);
        command.Options.Add(boneOption);
        command.Options.Add(rankBoneOption);
        command.Options.Add(rankTopOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            _ = cancellationToken;
            var input = parseResult.GetValue(inputArgument)!;
            var bytes = parseResult.GetValue(bytesOption);
            var anim = parseResult.GetValue(animOption);
            var bone = parseResult.GetValue(boneOption);
            var rankBone = parseResult.GetValue(rankBoneOption);
            var rankTop = parseResult.GetValue(rankTopOption);
            var verbose = parseResult.GetValue(verboseOption);
            return Task.FromResult(Execute(input, bytes, anim, bone, rankBone, rankTop, verbose));
        });

        return command;
    }

    private static int Execute(
        string input,
        int hexBytes,
        int animIndex,
        int boneIndex,
        int? rankBoneIndex,
        int rankTop,
        bool verbose)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {input}");
            return 1;
        }

        var data = File.ReadAllBytes(input);
        var fileName = Path.GetFileName(input);

        AnsiConsole.MarkupLine($"[bold cyan]File:[/] {fileName} ({data.Length:N0} bytes)");

        // ─── Mesh layer ────────────────────────────────────────────────
        var meshFile = PsxMeshFile.Parse(data);
        if (meshFile == null)
        {
            AnsiConsole.MarkupLine("[red]No mesh data — cannot locate post-mesh region.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[bold]Mesh layer:[/] version=0x{meshFile.Version:X2} hierarchy={meshFile.HasHierarchy} " +
            $"revision={meshFile.FormatRevision} meshes={meshFile.Meshes.Count} objects={meshFile.Objects.Count}");

        var parsedAnimFile = PsxAnimFile.Parse(data, meshFile.Objects.Count);
        if (parsedAnimFile != null)
        {
            AnsiConsole.MarkupLine(
                $"[bold]Anim layer:[/] layout={parsedAnimFile.Layout} " +
                $"revision={parsedAnimFile.FormatRevision} runtime={parsedAnimFile.MinimumRuntimeRevision} " +
                $"chunk=0x{parsedAnimFile.ChunkTag:X2} entries={parsedAnimFile.Entries.Count}/{parsedAnimFile.NumStreamsDeclared}");
        }

        if (rankBoneIndex is { } rankedBone)
            return DumpRankedBoneMotion(parsedAnimFile, meshFile.Objects.Count, rankedBone, rankTop);

        var boundary = PsxMeshFile.GetMeshBlockEnd(data);
        if (boundary <= 0 || boundary >= data.Length)
        {
            AnsiConsole.MarkupLine($"[red]Boundary detection failed (= {boundary}).[/]");
            return 1;
        }

        var trailing = data.Length - boundary;
        AnsiConsole.MarkupLine(
            $"[bold]Mesh-block end:[/] 0x{boundary:X} ({boundary:N0}) — trailing {trailing:N0} bytes after meshes");

        if (trailing < 16)
        {
            AnsiConsole.MarkupLine("[yellow]Less than 16 trailing bytes — likely no anim block in this file.[/]");
            return 0;
        }

        // ─── Layer 1: hex dump + u32 interpretation ─────────────────────
        AnsiConsole.MarkupLine("\n[bold underline]Layer 1[/] [grey]— hex dump after boundary[/]");
        DumpHex(data, boundary, (int)Math.Min(hexBytes, trailing));
        DumpFirstU32s(data, boundary, (int)Math.Min(16, trailing / 4));

        // ─── Layer 2: speculative anim-packet walk ──────────────────────
        AnsiConsole.MarkupLine("\n[bold underline]Layer 2[/] [grey]— anim packet walk (PreProcessAnimPacket)[/]");
        var afterAnimPacket = TryWalkAnimPacket(data, boundary, meshFile.Meshes.Count, verbose);

        var hierarchyStart = afterAnimPacket;
        if (PsxMeshFile.TryGetAnimChunkTag(data, out var animChunkTag, out var chunkDataOffset))
        {
            hierarchyStart = chunkDataOffset;
            AnsiConsole.MarkupLine(
                $"  [grey]Tagged anim chunk found: tag=0x{animChunkTag:X} data=0x{chunkDataOffset:X}[/]");
        }

        // ─── Layer 3: speculative hierarchy walk ────────────────────────
        AnsiConsole.MarkupLine("\n[bold underline]Layer 3[/] [grey]— per-bone hierarchy walk[/]");
        var psh = TryLoadPshCompanion(input);
        var hierResult = TryWalkHierarchy(data, hierarchyStart, psh, verbose);

        // ─── Layer 4: decompress one whole animation (all bones, 6 channels each) ───
        if (hierResult is not null)
        {
            AnsiConsole.MarkupLine(
                $"\n[bold underline]Layer 4[/] [grey]— decompress animation {animIndex} (all bones)[/]");
            DumpAnimationSlot(data, hierResult, animIndex, boneIndex, meshFile.Objects.Count, verbose);
        }
        else
        {
            AnsiConsole.MarkupLine("\n[yellow]Layer 4 skipped: hierarchy not located.[/]");
        }

        AnsiConsole.MarkupLine("\n[grey]Done. Iterate the heuristic if any layer looks wrong.[/]");
        return 0;
    }

    private static int DumpRankedBoneMotion(
        PsxAnimFile? animFile,
        int boneCount,
        int boneIndex,
        int rankTop)
    {
        if (animFile == null)
        {
            AnsiConsole.MarkupLine("[red]No recognizable animation table to rank.[/]");
            return 1;
        }

        if (boneIndex < 0 || boneIndex >= boneCount)
        {
            AnsiConsole.MarkupLine(
                $"[red]Bone index {boneIndex} out of range for {boneCount} bone(s).[/]");
            return 1;
        }

        rankTop = Math.Clamp(rankTop, 1, animFile.Entries.Count);
        var rows = new List<BoneMotionRankRow>(animFile.Entries.Count);
        for (var i = 0; i < animFile.Entries.Count; i++)
        {
            var entry = animFile.Entries[i];
            try
            {
                var slice = animFile.Pool.Span[entry.PoolOffset..];
                PsxAnimation animation;
                if (animFile.IsDirectMatrix)
                {
                    animation = PsxAnimDecoder.DecodeDirectMatrix(
                        slice, boneCount, entry.FrameCount);
                }
                else
                {
                    animation = PsxAnimDecoder.Decode(
                        slice, boneCount, entry.FrameCount, out _);
                }

                var tx = ChannelSpan(animation, boneIndex, 3);
                var ty = ChannelSpan(animation, boneIndex, 4);
                var tz = ChannelSpan(animation, boneIndex, 5);
                var rx = ChannelSpan(animation, boneIndex, 0);
                var ry = ChannelSpan(animation, boneIndex, 1);
                var rz = ChannelSpan(animation, boneIndex, 2);
                var translationLength = MathF.Sqrt(
                    tx.Span * tx.Span + ty.Span * ty.Span + tz.Span * tz.Span);
                rows.Add(new BoneMotionRankRow(
                    i,
                    entry.FrameCount,
                    tx.Span,
                    ty.Span,
                    tz.Span,
                    translationLength,
                    rx.Span,
                    ry.Span,
                    rz.Span,
                    null));
            }
            catch (Exception ex)
            {
                rows.Add(new BoneMotionRankRow(
                    i, entry.FrameCount, 0, 0, 0, 0f, 0, 0, 0, ex.Message));
            }
        }

        var table = new Table()
            .AddColumn(new TableColumn("Anim").RightAligned())
            .AddColumn(new TableColumn("Frames").RightAligned())
            .AddColumn(new TableColumn("TxSpan").RightAligned())
            .AddColumn(new TableColumn("TySpan").RightAligned())
            .AddColumn(new TableColumn("TzSpan").RightAligned())
            .AddColumn(new TableColumn("TLen").RightAligned())
            .AddColumn(new TableColumn("RxSpan").RightAligned())
            .AddColumn(new TableColumn("RySpan").RightAligned())
            .AddColumn(new TableColumn("RzSpan").RightAligned());

        foreach (var row in rows
                     .Where(static r => r.Error == null)
                     .OrderByDescending(static r => r.TranslationLength)
                     .ThenBy(static r => r.AnimIndex)
                     .Take(rankTop))
        {
            table.AddRow(
                row.AnimIndex.ToString(),
                row.FrameCount.ToString(),
                row.TxSpan.ToString(),
                row.TySpan.ToString(),
                row.TzSpan.ToString(),
                row.TranslationLength.ToString("0.0"),
                row.RxSpan.ToString(),
                row.RySpan.ToString(),
                row.RzSpan.ToString());
        }

        AnsiConsole.MarkupLine(
            $"[bold]Top {rankTop} animation slots by bone {boneIndex} translation span[/]");
        AnsiConsole.Write(table);

        var failures = rows.Count(static r => r.Error != null);
        if (failures > 0)
            AnsiConsole.MarkupLine($"[yellow]{failures} slot(s) failed to decode.[/]");

        return failures == rows.Count ? 1 : 0;
    }

    private static (int Min, int Max, int Span) ChannelSpan(
        PsxAnimation animation,
        int boneIndex,
        int channelIndex)
    {
        var min = short.MaxValue;
        var max = short.MinValue;
        for (var f = 0; f < animation.FrameCount; f++)
        {
            var value = animation.Channels[boneIndex, channelIndex, f];
            if (value < min) min = value;
            if (value > max) max = value;
        }

        return (min, max, max - min);
    }

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

    // ─── Layer 4: decompress one bone ───────────────────────────────────

    /// <summary>
    ///     Decompress one whole animation slot. Per the corrected reading of
    ///     <c>Decomp_GetAnimTransform</c> (DECOMP.cpp:454-481), each entry in
    ///     the hierarchy table is ONE ANIMATION; its compressed data block holds
    ///     <c>numBones × 6</c> channels concatenated as
    ///     <c>[bone0_ch0..ch5][bone1_ch0..ch5][…][boneN_ch5]</c>. Walking that
    ///     produces the full per-bone (Rx, Ry, Rz, Tx, Ty, Tz) trajectory for
    ///     all <paramref name="numBones" /> bones across <c>frameCount</c> frames.
    /// </summary>
    private static void DumpAnimationSlot(
        byte[] data, HierLocation hier, int animIndex, int boneIndex, int numBones, bool verbose)
    {
        if (animIndex < 0 || animIndex >= hier.NumStreams)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]Animation index {animIndex} out of range (0..{hier.NumStreams - 1}).[/]");
            return;
        }

        var poolOffset = hier.PoolOffsets[animIndex];
        var frameCount = hier.FrameCounts[animIndex];
        var streamStart = (int)hier.Base + poolOffset;

        // Note: per the THPS2 release source layout, anim entries are NOT
        // sorted by pool offset — animIdx 0 may live at the END of the pool.
        // So "next entry" doesn't give a meaningful byte budget. Instead we
        // compute a soft budget from "the next pool offset that's larger than
        // ours" (or pool span if none) and let the codec stop naturally at
        // streamLen frames per channel.
        var nextHigherOffset = hier.PoolOffsets
            .Where(o => o > poolOffset)
            .DefaultIfEmpty(data.Length - (int)hier.Base)
            .Min();
        var byteBudget = nextHigherOffset - poolOffset;

        if (streamStart >= data.Length || frameCount <= 0)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]Stream start 0x{streamStart:X} or frame count {frameCount} invalid — skipping.[/]");
            return;
        }

        AnsiConsole.MarkupLine(
            $"  anim {animIndex}  streamStart=0x{streamStart:X}  frames={frameCount}  numBones={numBones}  byteBudget={byteBudget}");

        // Preview first 32 bytes for manual structure inspection.
        var preview = new StringBuilder("  first 32 bytes:");
        for (var i = 0; i < Math.Min(32, data.Length - streamStart); i++)
            preview.Append($" {data[streamStart + i]:X2}");
        AnsiConsole.MarkupLine($"[grey]{preview}[/]");

        // Decompress all bones × 6 channels. Each channel uses its own flat
        // buffer (no stride interleaving — research mode prefers clarity).
        const int channelsPerBone = 6;
        var bufSize = Math.Max(frameCount * 4, 64);
        var allBoneChannels = new short[numBones, channelsPerBone][];
        for (var b = 0; b < numBones; b++)
        for (var c = 0; c < channelsPerBone; c++)
            allBoneChannels[b, c] = new short[bufSize];

        var src = data.AsSpan(streamStart);
        var consumed = 0;
        var perBoneBytes = new int[numBones];
        var perBoneChannelHeaders = new byte[numBones, channelsPerBone];
        var perBoneChannelBytes = new int[numBones, channelsPerBone];

        // Stop reading once we hit the byte budget — going past it would drift
        // into the next animation's data and produce garbage values for trailing
        // bones. Flag the bone where the cap kicks in so we know which bones got
        // valid data vs which were truncated.
        var bonesActuallyDecoded = 0;
        for (var b = 0; b < numBones; b++)
        {
            if (consumed >= byteBudget)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]→ Stopped at bone {b}: consumed {consumed} ≥ budget {byteBudget}. " +
                    $"Encoder may have used fewer than {numBones} bones for this anim.[/]");
                break;
            }

            var boneStart = consumed;
            for (var ch = 0; ch < channelsPerBone; ch++)
            {
                if (consumed >= src.Length)
                {
                    AnsiConsole.MarkupLine(
                        $"  [red]Ran out of source bytes at bone {b} channel {ch} (consumed={consumed}, available={src.Length}).[/]");
                    return;
                }

                perBoneChannelHeaders[b, ch] = src[consumed];

                try
                {
                    var bytes = PsxAnimDecompressor.Decompress(
                        src[consumed..], allBoneChannels[b, ch], 1, frameCount);
                    perBoneChannelBytes[b, ch] = bytes;
                    consumed += bytes;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine(
                        $"  [red]bone {b} channel {ch} decode failed at +{consumed}: {ex.Message}[/]");
                    return;
                }
            }

            perBoneBytes[b] = consumed - boneStart;
            bonesActuallyDecoded = b + 1;
        }

        numBones = bonesActuallyDecoded;

        if (verbose)
        {
            AnsiConsole.MarkupLine("  [grey]bone | hdr0 hdr1 hdr2 hdr3 hdr4 hdr5 | b0 b1 b2 b3 b4 b5 | total[/]");
            for (var b = 0; b < numBones; b++)
            {
                var hdrs = string.Join(" ", Enumerable.Range(0, channelsPerBone)
                    .Select(c => $"0x{perBoneChannelHeaders[b, c]:X2}"));
                var bytes = string.Join(" ", Enumerable.Range(0, channelsPerBone)
                    .Select(c => perBoneChannelBytes[b, c].ToString("D2")));
                AnsiConsole.MarkupLine($"  [grey]{b,4}[/] | {hdrs} | {bytes} |   {perBoneBytes[b],3}");
            }
        }

        var match = consumed <= byteBudget && byteBudget - consumed < 16;
        AnsiConsole.MarkupLine(
            $"  total bytes consumed = {consumed} / budget {byteBudget}  " +
            (match
                ? "[green](matches: leftover is alignment padding)[/]"
                : "[yellow](mismatch — layout interpretation may need tweaking)[/]"));

        AnsiConsole.MarkupLine(
            $"  per-bone byte counts: avg {perBoneBytes.Average():0.0}, " +
            $"min {perBoneBytes.Min()}, max {perBoneBytes.Max()}");

        // Per-bone channel range summary (one line per bone) so we can see which
        // bones are static (placeholder anims) vs which actually move.
        AnsiConsole.MarkupLine("  [grey]bone  | ch0_span ch1_span ch2_span ch3_span ch4_span ch5_span[/]");
        for (var b = 0; b < numBones; b++)
        {
            var spans = new int[channelsPerBone];
            for (var c = 0; c < channelsPerBone; c++)
            {
                var (mn, mx) = MinMaxChannel(allBoneChannels[b, c], frameCount);
                spans[c] = mx - mn;
            }

            AnsiConsole.MarkupLine(
                $"  [grey]{b,4}[/]  | {spans[0],8} {spans[1],8} {spans[2],8} {spans[3],8} {spans[4],8} {spans[5],8}");
        }

        // Per-frame table for the chosen bone.
        if (boneIndex < 0 || boneIndex >= numBones)
        {
            AnsiConsole.MarkupLine($"  [yellow]Bone {boneIndex} out of range; skipping per-frame dump.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"\n  [bold]Per-frame dump for bone {boneIndex}[/] (channels: Rx Ry Rz | Tx Ty Tz):");
        AnsiConsole.MarkupLine(
            "  [grey]frame  Rx     Ry     Rz       | Tx      Ty      Tz       | Rx°     Ry°     Rz°     | Tx/4096 Ty/4096 Tz/4096[/]");

        var framesToShow = verbose ? frameCount : Math.Min(8, frameCount);
        for (var f = 0; f < framesToShow; f++)
        {
            var rx = allBoneChannels[boneIndex, 0][f];
            var ry = allBoneChannels[boneIndex, 1][f];
            var rz = allBoneChannels[boneIndex, 2][f];
            var tx = allBoneChannels[boneIndex, 3][f];
            var ty = allBoneChannels[boneIndex, 4][f];
            var tz = allBoneChannels[boneIndex, 5][f];

            var rxDeg = AngleUnitsToDegrees(rx);
            var ryDeg = AngleUnitsToDegrees(ry);
            var rzDeg = AngleUnitsToDegrees(rz);
            var txU = tx / 4096.0;
            var tyU = ty / 4096.0;
            var tzU = tz / 4096.0;

            AnsiConsole.MarkupLine(
                $"  [grey]{f,4}[/]  {rx,6} {ry,6} {rz,6} | {tx,6} {ty,6} {tz,6} | "
                + $"{rxDeg,7:0.00} {ryDeg,7:0.00} {rzDeg,7:0.00} | "
                + $"{txU,7:0.000} {tyU,7:0.000} {tzU,7:0.000}");
        }

        if (!verbose && frameCount > framesToShow)
            AnsiConsole.MarkupLine(
                $"  [grey](… {frameCount - framesToShow} more frames suppressed; pass -v for full dump)[/]");
    }

    private static double AngleUnitsToDegrees(short rawAngle)
    {
        return (rawAngle & 0x0fff) * (360.0 / PsxAnimation.PsyqAngleUnitsPerRevolution);
    }

    private static (short Min, short Max) MinMaxChannel(short[] buf, int frameCount)
    {
        var min = short.MaxValue;
        var max = short.MinValue;
        for (var f = 0; f < frameCount; f++)
        {
            if (buf[f] < min) min = buf[f];
            if (buf[f] > max) max = buf[f];
        }

        return (min, max);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static PshFile? TryLoadPshCompanion(string psxPath)
    {
        var stem = Path.Combine(
            Path.GetDirectoryName(psxPath) ?? "",
            Path.GetFileNameWithoutExtension(psxPath) + ".psh");
        return File.Exists(stem) ? PshFile.Parse(stem) : null;
    }

    // ─── Layer 3: hierarchy walk ────────────────────────────────────────

    private sealed record HierLocation(
        long Base,
        int NumStreams,
        int[] FrameCounts,
        int[] PoolOffsets);

    private sealed record BoneMotionRankRow(
        int AnimIndex,
        int FrameCount,
        int TxSpan,
        int TySpan,
        int TzSpan,
        float TranslationLength,
        int RxSpan,
        int RySpan,
        int RzSpan,
        string? Error);
}
