using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Replay;

public sealed class ThawPs2ReplayDiagnosticTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    [Fact]
    public void Diagnostic_SkaterLasek_KickAnalysis()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(paths.TestOutputDir is null, "TestOutput not available");
        var file = paths.FindSampleFile(BuildName, "skater_lasek.skin.ps2");
        Assert.SkipWhen(file is null, "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var kicks = ThawPs2SkinFile.ReplayExtractKicks(data);

        var lines = new List<string>
        {
            "=== THAW PS2 Kick Diagnostic: skater_lasek ===",
            $"Total batches: {batches.Count}",
            $"Total kicks: {kicks.Count}",
            $"Total triangles: {kicks.Sum(kick => kick.TriangleCount)}",
            ""
        };

        var addr280Kicks = kicks.Where(kick => kick.KickPacket.Address == 280).ToList();
        var addr652Kicks = kicks.Where(kick => kick.KickPacket.Address == 652).ToList();
        lines.Add($"ADDR=280 kicks: {addr280Kicks.Count}, triangles: {addr280Kicks.Sum(kick => kick.TriangleCount)}");
        lines.Add($"ADDR=652 kicks: {addr652Kicks.Count}, triangles: {addr652Kicks.Sum(kick => kick.TriangleCount)}");
        lines.Add("");

        foreach (var kick in kicks)
        {
            var batch = batches.FirstOrDefault(candidate => candidate.FirstCommandOffset == kick.FirstCommandOffset);
            lines.Add($"--- Kick {kick.KickIndex} (batch {kick.BatchIndex}, setup {kick.SetupIndex}) ---");
            lines.Add(
                $"  ADDR={kick.KickPacket.Address} NLOOP={kick.KickPacket.Nloop} EOP={kick.KickPacket.Eop} Kind={kick.KickPacket.Kind}");
            lines.Add($"  Offset=0x{kick.FirstCommandOffset:X6}");
            lines.Add($"  Triangles={kick.TriangleCount} Meshes={kick.Meshes.Length}");
            lines.Add(
                $"  OutputWindow: [{kick.FullOutputWindow[0]}..{kick.FullOutputWindow[^1]}] (len={kick.FullOutputWindow.Length})");

            if (batch != null)
            {
                lines.Add($"  BatchVertexCount={batch.VertexCount} OutputVertexCount={batch.OutputVertexCount}");
                lines.Add($"  PostBatchElements={batch.PostBatchElements.Length}");

                for (var i = 0; i < batch.PostBatchElements.Length; i++)
                {
                    var pbe = batch.PostBatchElements[i];
                    lines.Add($"    PBE[{i}]: C0=0x{pbe.C0:X4} C1=0x{pbe.C1:X4} C2=0x{pbe.C2:X4} C3=0x{pbe.C3:X4}");
                }

                lines.Add($"  ContextWrites={batch.ContextWrites.Length}");
                foreach (var ctx in batch.ContextWrites)
                {
                    var fmt = $"V{ctx.Unpack.Vn + 1}_{32 >> ctx.Unpack.Vl}";
                    lines.Add(
                        $"    {(ctx.Unpack.Usn ? "U" : "")}{fmt} NUM={ctx.Unpack.Num} -> [{string.Join(",", ctx.WriteAddresses)}]");
                }

                var snap = batch.Snapshot;
                lines.Add(
                    $"  Snapshot: Xtop={snap.Xtop} PreTops={snap.PreTops} PostTops={snap.PostTops} Dbf={snap.Dbf}");
                lines.Add($"  MinWrite={snap.MinWrittenAddress} MaxWrite={snap.MaxWrittenAddress}");

                if (snap.KickBaseWindow.Length > 0)
                {
                    lines.Add($"  KickBaseWindow: start={snap.KickBaseWindowStart} len={snap.KickBaseWindow.Length}");
                    for (var i = 0; i < snap.KickBaseWindow.Length; i++)
                    {
                        var addr = (snap.KickBaseWindowStart + i) % 1024;
                        var qw = snap.KickBaseWindow[i];
                        lines.Add($"    [{addr,4}] ({qw.X:X8}, {qw.Y:X8}, {qw.Z:X8}, {qw.W:X8})");
                    }
                }
            }

            var vertexEvents = kick.Events.Count(evt => evt.Kind == GsVertexEventKind.Vertex);
            var gapEvents = kick.Events.Count(evt => evt.Kind == GsVertexEventKind.Gap);
            var carryEvents = kick.Events.Count(evt => evt.IsBufferedCarry);
            var noKickEvents = kick.Events.Count(evt => evt.IsNoKick);
            lines.Add($"  Events: {vertexEvents} vertex, {gapEvents} gap, {carryEvents} carry, {noKickEvents} noKick");

            if (kick.KickIndex == 28)
            {
                lines.Add("  --- Per-event detail for kick_028 ---");
                for (var i = 0; i < kick.Events.Length; i++)
                {
                    var evt = kick.Events[i];
                    var srcInfo = evt.VertexSource is { } src
                        ? $"c2addr={src.OutputFullAddress} c3addr={src.DuplicateFullAddress} c2nk={src.OutputNoKick} c3nk={src.DuplicateNoKick} pos=({src.Position.X:F2},{src.Position.Y:F2},{src.Position.Z:F2})"
                        : "null";
                    lines.Add(
                        $"    [{i,3}] addr={evt.FullOutputAddress,4} {evt.Kind,-6} nk={evt.IsNoKick} carry={evt.IsBufferedCarry} {srcInfo}");
                }

                if (batch != null)
                {
                    lines.Add("  --- Vertex source address map ---");
                    foreach (var vs in batch.VertexSources)
                    {
                        var dupInfo = vs.DuplicateAddress != vs.OutputAddress
                            ? $" dup→{vs.DuplicateFullAddress}(nk={vs.DuplicateNoKick})"
                            : "";
                        lines.Add(
                            $"    c2→{vs.OutputFullAddress}(nk={vs.OutputNoKick}){dupInfo}  pos=({vs.Position.X:F2},{vs.Position.Y:F2},{vs.Position.Z:F2})");
                    }
                }
            }

            if (batch != null)
            {
                var windowSet = kick.FullOutputWindow.ToHashSet();
                var inWindow = 0;
                var outWindow = 0;
                foreach (var vs in batch.VertexSources)
                {
                    if (windowSet.Contains(vs.OutputFullAddress))
                        inWindow++;
                    else
                        outWindow++;
                }

                lines.Add($"  VertexSources: {inWindow} in-window, {outWindow} out-of-window");
                if (outWindow > 0)
                {
                    var outAddrs = batch.VertexSources
                        .Where(vs => !windowSet.Contains(vs.OutputFullAddress))
                        .Select(vs => vs.OutputFullAddress)
                        .Distinct()
                        .OrderBy(addr => addr)
                        .ToArray();
                    lines.Add($"    Out-of-window addresses: [{string.Join(",", outAddrs)}]");
                }
            }

            lines.Add("");
        }

        lines.Add("=== Batches with vertices but no kick ===");
        var kickBatchOffsets = kicks.Select(kick => kick.FirstCommandOffset).ToHashSet();
        var skippedBatches = batches
            .Where(batch => batch.VertexCount > 0 && !kickBatchOffsets.Contains(batch.FirstCommandOffset))
            .ToList();
        lines.Add($"Count: {skippedBatches.Count}");
        foreach (var batch in skippedBatches)
        {
            lines.Add($"  Offset=0x{batch.FirstCommandOffset:X6} Setup={batch.SetupIndex} Verts={batch.VertexCount}");
            lines.Add(
                $"    KickPacket: Kind={batch.OutputKickPacket.Kind} ADDR={batch.OutputKickPacket.Address} NLOOP={batch.OutputKickPacket.Nloop} EOP={batch.OutputKickPacket.Eop}");
        }

        var outputPath = Path.Combine(paths.TestOutputDir!, "skater_lasek_kick_diagnostic.txt");
        File.WriteAllLines(outputPath, lines);

        Assert.True(kicks.Count > 0);
        Assert.True(addr280Kicks.Count > 0, "Expected some ADDR=280 kicks");
        Assert.True(addr652Kicks.Count > 0, "Expected some ADDR=652 kicks");
    }

    [Theory]
    [InlineData("skater_lasek")]
    [InlineData("pro_vallely_head")]
    public void Diagnostic_GapTrace(string stem)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(paths.TestOutputDir is null, "TestOutput not available");

        var ps2File = paths.FindSampleFile(BuildName, $"{stem}.skin.ps2");
        Assert.SkipWhen(ps2File is null, $"PS2 file not found: {stem}");

        var ps2Data = File.ReadAllBytes(ps2File);
        var batches = ThawPs2SkinFile.ReplayBatches(ps2Data);
        var kicks = ThawPs2SkinFile.ReplayExtractKicks(ps2Data);

        var lines = new List<string>
        {
            $"=== Gap Trace Diagnostic: {stem} ===",
            $"Total batches: {batches.Count}",
            $"Total kicks: {kicks.Count}",
            ""
        };

        var allSourceAddresses = new Dictionary<int, List<(int BatchIdx, ReplayVertexSource Source)>>();
        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            foreach (var src in batches[batchIndex].VertexSources)
            {
                var addr = src.OutputFullAddress;
                if (!allSourceAddresses.TryGetValue(addr, out var list))
                {
                    list = [];
                    allSourceAddresses[addr] = list;
                }

                list.Add((batchIndex, src));

                if (src.DuplicateAddress == src.OutputAddress)
                    continue;

                var dupAddr = src.DuplicateFullAddress;
                if (!allSourceAddresses.TryGetValue(dupAddr, out var dupList))
                {
                    dupList = [];
                    allSourceAddresses[dupAddr] = dupList;
                }

                dupList.Add((batchIndex, src));
            }
        }

        foreach (var kick in kicks)
        {
            var gaps = kick.Events.Where(evt => evt.Kind == GsVertexEventKind.Gap).ToList();
            if (gaps.Count == 0)
                continue;

            lines.Add($"--- Kick {kick.KickIndex}: setup={kick.SetupIndex} entry={kick.EntryIndex} " +
                      $"batch={kick.BatchIndex} addr={kick.KickPacket.Address} nloop={kick.KickPacket.Nloop} " +
                      $"tris={kick.TriangleCount} gaps={gaps.Count} meshes={kick.Meshes.Length} " +
                      $"preamble={kick.IsPreambleBatch} ---");

            var batch = batches[kick.BatchIndex];
            lines.Add($"  Batch info: vtxCount={batch.VertexCount} outputVtxCount={batch.OutputVertexCount}");
            lines.Add(
                $"  Batch offsets: pos=0x{batch.PositionOffset:X} nrm=0x{batch.NormalOffset:X} uvadc=0x{batch.UvAdcOffset:X}");
            lines.Add($"  PostBatch elements: {batch.PostBatchElements.Length}");
            lines.Add(
                $"  ParserTag: {batch.Snapshot.ParserTag.Kind} nloop={batch.Snapshot.ParserTag.Nloop} addr={batch.Snapshot.ParserTag.Address}");
            lines.Add(
                $"  Snapshot: xtop={batch.Snapshot.Xtop} preTops={batch.Snapshot.PreTops} postTops={batch.Snapshot.PostTops} dbf={batch.Snapshot.Dbf}");
            lines.Add("");

            lines.Add($"  Vertex source output addresses ({batch.VertexSources.Length}):");
            foreach (var src in batch.VertexSources)
            {
                var dupInfo = src.DuplicateAddress != src.OutputAddress
                    ? $" dup={src.DuplicateFullAddress}(noKick={src.DuplicateNoKick})"
                    : "";
                lines.Add(
                    $"    addr={src.OutputFullAddress} noKick={src.OutputNoKick}{dupInfo} pos=({src.Position.X:F2},{src.Position.Y:F2},{src.Position.Z:F2})");
            }

            lines.Add("");
            if (batch.PostBatchElements.Length > 0)
            {
                lines.Add("  Post-batch copy pairs:");
                var firstC0Tag = (batch.PostBatchElements[0].C0 & 0x8000) != 0;
                var firstC2Tag = (batch.PostBatchElements[0].C2 & 0x8000) != 0;
                for (var i = 0; i < batch.PostBatchElements.Length; i++)
                {
                    var element = batch.PostBatchElements[i];
                    var c0Active = !(i == 0 && firstC0Tag);
                    var c2Active = !(i == 0 && firstC2Tag);
                    if (c0Active)
                        lines.Add(
                            $"    C0→C1: src={element.C0 & 0x3FF}(raw=0x{element.C0:X4}) → dst={element.C1 & 0x3FF}(raw=0x{element.C1:X4})");
                    else
                        lines.Add($"    C0→C1: [TAG] raw=0x{element.C0:X4},0x{element.C1:X4}");

                    if (c2Active)
                        lines.Add(
                            $"    C2→C3: src={element.C2 & 0x3FF}(raw=0x{element.C2:X4}) → dst={element.C3 & 0x3FF}(raw=0x{element.C3:X4})");
                    else
                        lines.Add($"    C2→C3: [TAG] raw=0x{element.C2:X4},0x{element.C3:X4}");
                }

                lines.Add("");
            }

            lines.Add($"  Output window ({kick.FullOutputWindow.Length} slots):");
            for (var i = 0; i < kick.Events.Length; i++)
            {
                var evt = kick.Events[i];
                string marker;
                if (evt.Kind == GsVertexEventKind.Gap)
                    marker = "GAP";
                else if (evt.IsNoKick)
                    marker = "NOK";
                else if (evt.IsBufferedCarry)
                    marker = "CAR";
                else
                    marker = "VTX";
                var posInfo = evt.VertexSource != null
                    ? $" pos=({evt.VertexSource.Value.Position.X:F2},{evt.VertexSource.Value.Position.Y:F2},{evt.VertexSource.Value.Position.Z:F2})"
                    : "";

                var anySource = allSourceAddresses.ContainsKey(evt.FullOutputAddress);
                var nearbyHits = new List<int>();
                for (var delta = -3; delta <= 3; delta++)
                {
                    var testAddr = (evt.FullOutputAddress + delta + 1024) % 1024;
                    if (allSourceAddresses.ContainsKey(testAddr))
                        nearbyHits.Add(testAddr);
                }

                var extra = evt.Kind == GsVertexEventKind.Gap
                    ? $" anyBatchWrote={anySource} nearby=[{string.Join(",", nearbyHits)}]"
                    : "";

                lines.Add($"    [{i:D3}] addr={evt.FullOutputAddress:D4} {marker}{posInfo}{extra}");
            }

            lines.Add("");
        }

        var outputPath = Path.Combine(paths.TestOutputDir!, $"{stem}_gap_trace.txt");
        File.WriteAllLines(outputPath, lines);

        Assert.True(kicks.Count > 0);
    }
}