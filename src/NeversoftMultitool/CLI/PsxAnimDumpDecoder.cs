using System.Text;
using NeversoftMultitool.Core.Formats.Animation;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Layer 4 of the psxanim probe: full DecompressStream decode of an
///     animation slot plus the per-bone motion-span rank diagnostic.
/// </summary>
internal static class PsxAnimDumpDecoder
{

    internal static int DumpRankedBoneMotion(
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
                        slice, boneCount, entry.FrameCount, entry.TweenFlag);
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
    internal static void DumpAnimationSlot(
        byte[] data, PsxAnimHierLocation hier, int animIndex, int boneIndex, int numBones, bool verbose)
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
        {
            for (var c = 0; c < channelsPerBone; c++)
                allBoneChannels[b, c] = new short[bufSize];
        }

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
