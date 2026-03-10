using System.Numerics;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.XbxScene;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class ThawPs2ReplayTests(TestPaths paths)
{
    private string ThawSkinDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "SKIN");

    private string ThawPcSkinDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "SKIN");

    [Fact(Skip = "Replay trace parity for skater_lasek is not locked yet; keep this pending until the semantic port reaches batch-reference parity.")]
    public void ReplayBatches_SkaterLasek_HasReplayBatchMetadata()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);

        Assert.True(batches.Count > 1, "Expected multiple replay batches");

        var first = batches[0];
        Assert.False(first.IsPreambleBatch);
        Assert.Equal(0, first.SetupIndex);
        Assert.True(first.VertexCount > 0);
        Assert.Equal(first.VertexCount, first.VertexSources.Length);
        Assert.NotEqual(GifKickPacketKind.None, first.OutputKickPacket.Kind);
        Assert.True(first.OutputKickPacket.Nloop > 0);
        Assert.True(first.OutputKickPacket.Address > 0);
        Assert.Equal(first.OutputKickPacket.Nloop, first.OutputVertexCount);
        Assert.True(first.Snapshot.Xtop >= 0);
        Assert.NotNull(first.Snapshot.XtopWindow);
        Assert.True(first.Snapshot.XtopWindow.Length > 0);

        var second = batches[1];
        Assert.True(second.Snapshot.Xtop >= 0);
    }

    [Fact]
    public void ReplayBatches_SkaterLasek_IncludesInitialPreambleStateBatch()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);

        Assert.True(batches.Count > 0, "Expected at least one replay batch");

        var first = batches[0];
        Assert.True(first.IsPreambleBatch);
        Assert.Equal(0, first.SetupIndex);
        Assert.Equal(0, first.VertexCount);
        Assert.Equal(0x000230, first.FirstCommandOffset);
        Assert.Equal(0x000BE4, first.CommandTrace[^1].CommandOffset);
        Assert.Contains(first.CommandTrace, command => command.Kind == VifReplayCommandKind.Offset);
        Assert.Contains(first.CommandTrace, command => command.Kind == VifReplayCommandKind.Base);
        Assert.Equal(VifReplayCommandKind.Mscnt, first.CommandTrace[^1].Kind);
        Assert.Equal(668, first.Snapshot.Xtop);
        Assert.Equal(321, first.Snapshot.PostTops);
    }

    [Fact]
    public void ReplayBatches_SkaterLasek_TracksFirstMaterialKickSnapshot()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var firstMaterialKick = Assert.Single(batches.Where(batch =>
            batch.FirstCommandOffset == 0x001F3C &&
            batch.CommandTrace.Length > 0 &&
            batch.CommandTrace[^1].CommandOffset == 0x0022EC));

        Assert.Equal(0x001F3C, firstMaterialKick.FirstCommandOffset);
        Assert.True(firstMaterialKick.CommandTrace.Any(command => command.Kind == VifReplayCommandKind.Direct));
        Assert.Equal(652, firstMaterialKick.Snapshot.Xtop);
        Assert.Equal(0, firstMaterialKick.Snapshot.PostTops);
        Assert.Equal(GifKickPacketKind.XtopWindow, firstMaterialKick.Snapshot.ParserTag.Kind);
        Assert.Equal(24, firstMaterialKick.Snapshot.ParserTag.Nloop);
        Assert.Equal(62, firstMaterialKick.Snapshot.ParserTag.Address);
        Assert.Equal(0, firstMaterialKick.Snapshot.ParserTag.Size);
    }

    [Fact]
    public void ReplayBatches_SkaterLasek_FirstMaterialBatch_CapturesContextWrites()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var firstMaterialKick = Assert.Single(batches.Where(batch => batch.FirstCommandOffset == 0x001F3C));

        Assert.Equal(2, firstMaterialKick.ContextWrites.Length);

        var firstContext = firstMaterialKick.ContextWrites[0];
        Assert.Equal(2, firstContext.Unpack.Vn);
        Assert.Equal(2, firstContext.Unpack.Vl);
        Assert.Equal(3, firstContext.Unpack.Num);
        Assert.Equal([645, 646, 647], firstContext.WriteAddresses);

        var secondContext = firstMaterialKick.ContextWrites[1];
        Assert.Equal(1, secondContext.Unpack.Vn);
        Assert.Equal(2, secondContext.Unpack.Vl);
        Assert.Equal(4, secondContext.Unpack.Num);
        Assert.Equal([648, 649, 650, 651], secondContext.WriteAddresses);
    }

    [Fact]
    public void ReplayBatches_SkaterLasek_FirstMaterialBatchSequenceMatchesReference()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var meshZeroBatches = batches
            .Where(batch => batch.VertexCount > 0 && batch.FirstCommandOffset >= 0x001F3C)
            .Take(5)
            .ToArray();

        Assert.Equal(5, meshZeroBatches.Length);

        var expectedCommandOffsets = new[] { 0x0022EC, 0x0025DC, 0x002920, 0x002C1C, 0x002E6C };
        var expectedXtops = new[] { 652, 0, 652, 0, 652 };
        var expectedParserNloops = new[] { 24, 0, 24, 0, 24 };
        var expectedParserAddresses = new[] { 62, 0, 62, 0, 62 };
        var expectedVertexCounts = new[] { 43, 35, 42, 37, 27 };

        for (var i = 0; i < meshZeroBatches.Length; i++)
        {
            Assert.Equal(expectedCommandOffsets[i], meshZeroBatches[i].CommandTrace[^1].CommandOffset);
            Assert.Equal(expectedXtops[i], meshZeroBatches[i].Snapshot.Xtop);
            Assert.Equal(expectedParserNloops[i], meshZeroBatches[i].Snapshot.ParserTag.Nloop);
            Assert.Equal(expectedParserAddresses[i], meshZeroBatches[i].Snapshot.ParserTag.Address);
            Assert.Equal(expectedVertexCounts[i], meshZeroBatches[i].VertexCount);
        }
    }

    [Fact]
    public void ReplayBatches_SkaterLasek_FlaggedAddr652Batch0038A0_MinWriteWindowMatchesPython()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var batch = Assert.Single(batches.Where(candidate => candidate.FirstCommandOffset == 0x0038A0));

        Assert.Equal(0, batch.Snapshot.Xtop);
        Assert.Equal(892, batch.Snapshot.MinWrittenAddress);
        Assert.Equal(1023, batch.Snapshot.MaxWrittenAddress);
        Assert.Equal(890, batch.Snapshot.MinWriteWindowStart);
        Assert.Equal(6, batch.Snapshot.MinWriteWindow.Length);
        Assert.Equal(new Vu1Qword(0x00000000, 0x00000000, 0x00000000, 0x00000000), batch.Snapshot.MinWriteWindow[0]);
        Assert.Equal(new Vu1Qword(0x00000000, 0x00000000, 0x00000000, 0x00000000), batch.Snapshot.MinWriteWindow[1]);
        Assert.Equal(new Vu1Qword(0xFFFF804C, 0x0000028C, 0x00000000, 0x00000000), batch.Snapshot.MinWriteWindow[2]);
        Assert.Equal(new Vu1Qword(0x000001F1, 0xFFFF82D8, 0x000001DC, 0x000002DE), batch.Snapshot.MinWriteWindow[3]);
        Assert.Equal(new Vu1Qword(0x000001DF, 0xFFFF82B4, 0x000001DC, 0xFFFF82B7), batch.Snapshot.MinWriteWindow[4]);
        Assert.Equal(new Vu1Qword(0x000001BB, 0x000002AE, 0x000001B8, 0x000002B1), batch.Snapshot.MinWriteWindow[5]);
    }

    [Fact]
    public void ReplayBatches_SkaterLasek_FlaggedAddr652Batch0038A0_CapturesWrappedContextWrites()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var batch = Assert.Single(batches.Where(candidate => candidate.FirstCommandOffset == 0x0038A0));

        var context = Assert.Single(batch.ContextWrites);
        Assert.Equal(2, context.Unpack.Vn);
        Assert.Equal(2, context.Unpack.Vl);
        Assert.Equal(6, context.Unpack.Num);
        Assert.Equal([1018, 1019, 1020, 1021, 1022, 1023], context.WriteAddresses);
    }

    [Fact]
    public void ReplayBatches_SkaterLasek_FlaggedAddr652Batch004568_MinWriteWindowMatchesPython()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var batch = Assert.Single(batches.Where(candidate => candidate.FirstCommandOffset == 0x004568));

        Assert.Equal(0, batch.Snapshot.Xtop);
        Assert.Equal(890, batch.Snapshot.MinWrittenAddress);
        Assert.Equal(1023, batch.Snapshot.MaxWrittenAddress);
        Assert.Equal(888, batch.Snapshot.MinWriteWindowStart);
        Assert.Equal(6, batch.Snapshot.MinWriteWindow.Length);
        Assert.Equal(new Vu1Qword(0x000009FF, 0x00000DE5, 0xFFFF82D5, 0xFFFF82D5), batch.Snapshot.MinWriteWindow[0]);
        Assert.Equal(new Vu1Qword(0xFFFFFFDE, 0xFFFFFF89, 0xFFFFFFE6, 0x00000000), batch.Snapshot.MinWriteWindow[1]);
        Assert.Equal(new Vu1Qword(0xFFFF804D, 0x0000028C, 0x00000000, 0x00000000), batch.Snapshot.MinWriteWindow[2]);
        Assert.Equal(new Vu1Qword(0x000001CD, 0x00000365, 0x000001E5, 0xFFFF8368), batch.Snapshot.MinWriteWindow[3]);
        Assert.Equal(new Vu1Qword(0x000001C4, 0xFFFF8344, 0x000001C1, 0x00000362), batch.Snapshot.MinWriteWindow[4]);
        Assert.Equal(new Vu1Qword(0x000001E5, 0xFFFF8290, 0x000001C1, 0xFFFF8341), batch.Snapshot.MinWriteWindow[5]);
    }

    [Fact]
    public void ReplayBatches_SkaterLasek_FlaggedAddr280ShoeKicks_ShareStableKickBaseWindow()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var setup02ShoeKick = Assert.Single(batches.Where(candidate => candidate.FirstCommandOffset == 0x006004));
        var setup04ShoeKick = Assert.Single(batches.Where(candidate => candidate.FirstCommandOffset == 0x0079A8));

        Assert.Equal(280, setup02ShoeKick.OutputKickPacket.Address);
        Assert.Equal(280, setup04ShoeKick.OutputKickPacket.Address);
        Assert.Equal(263, setup02ShoeKick.Snapshot.KickBaseWindowStart);
        Assert.Equal(263, setup04ShoeKick.Snapshot.KickBaseWindowStart);
        Assert.Equal(18, setup02ShoeKick.Snapshot.KickBaseWindow.Length);
        Assert.Equal(18, setup04ShoeKick.Snapshot.KickBaseWindow.Length);
        Assert.Equal(setup02ShoeKick.Snapshot.KickBaseWindow, setup04ShoeKick.Snapshot.KickBaseWindow);

        Assert.Equal(new Vu1Qword(0x00000073, 0x00000362, 0xFFFFFFC2, 0x00000000), setup02ShoeKick.Snapshot.KickBaseWindow[0]);
        Assert.Equal(new Vu1Qword(0x0000002D, 0xFFFFFFBD, 0xFFFFFF9F, 0x00000000), setup02ShoeKick.Snapshot.KickBaseWindow[^1]);
    }

    [Fact]
    public void ReplayBatches_BodyFTorso_FlagsPreambleBatches()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "body_f_torso.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);

        Assert.Contains(batches, batch => batch.IsPreambleBatch);
        // Preamble batches start before the first FLUSH+DIRECT boundary; most have SetupIndex=0,
        // but a batch whose first command is pre-boundary while its MSCNT fires post-boundary
        // may have SetupIndex=1 (the corrected boundary-to-entry mapping).
        Assert.Contains(batches.Where(batch => batch.IsPreambleBatch), batch => batch.SetupIndex == 0);
    }

    [Fact]
    public void ReplayBatches_SkaterHawk_UsesResolvedChainStartAndPreservesPreambleBatches()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_hawk.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);

        Assert.True(batches.Count > 0, "Expected replay batches");
        Assert.True(batches[0].IsPreambleBatch, "Expected the first replay batch to be marked as preamble");
        Assert.Contains(batches, batch => batch.VertexCount > 0 && batch.FirstCommandOffset == 0x000BEC);
    }

    [Fact]
    public void ReplayExtractKicks_SkaterLasek_EmitsValidKickWindows()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var kicks = ThawPs2SkinFile.ReplayExtractKicks(data);

        Assert.True(kicks.Count > 0, "Expected at least one replay kick");
        Assert.All(kicks, kick =>
        {
            Assert.True(kick.KickPacket.Eop);
            Assert.Contains(kick.KickPacket.Address, new[] { 280, 652 });
            Assert.Equal(kick.KickPacket.Nloop, kick.OutputWindow.Length);
            Assert.Equal(kick.KickPacket.Nloop, kick.FullOutputWindow.Length);
            Assert.Equal(kick.OutputWindow.Length, kick.Events.Length);
        });

        Assert.Equal(2930, kicks.Sum(kick => kick.TriangleCount)); // PC: 3070; gaps from cross-buffer post-batch copies
    }

    [Fact]
    public void ReplayExtractKicks_SkaterLasek_FirstMaterialKick_IsAnchoredToCurrentWindow()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var kicks = ThawPs2SkinFile.ReplayExtractKicks(data);
        var firstMaterialKick = Assert.Single(kicks.Where(kick => kick.FirstCommandOffset == 0x001F3C));

        Assert.Equal(280, firstMaterialKick.KickPacket.Address);
        Assert.Equal(75, firstMaterialKick.OutputWindow.Length);
        Assert.Equal(281, firstMaterialKick.FullOutputWindow[0]);
        Assert.Equal(503, firstMaterialKick.FullOutputWindow[^1]);
    }

    [Fact]
    public void ReplayKickDebugReport_SkaterLasek_ContainsFirstMaterialKickName()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var kicks = ThawPs2SkinFile.ReplayExtractKicks(data);
        var firstMaterialKick = Assert.Single(kicks.Where(kick => kick.FirstCommandOffset == 0x001F3C));
        var report = ThawReplayDebugGltfWriter.FormatKickReport(kicks);

        Assert.Contains("KickIndex\tBatchIndex\tSetupIndex", report);
        Assert.Contains(ThawReplayDebugGltfWriter.GetKickName(firstMaterialKick), report);
        Assert.Contains(ThawReplayDebugGltfWriter.GetKickColorHex(firstMaterialKick.KickIndex), report);
    }

    [Fact]
    public void ReplayTraceFormatter_BodyFTorso_EmitsPreambleTrace()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "body_f_torso.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var trace = ThawPs2ReplayTraceFormatter.FormatBatches(batches);

        Assert.Contains("PREAMBLE", trace);
        Assert.Contains("commands:", trace);
        Assert.Contains("STCYCL", trace);
        Assert.Contains("context:", trace);
        Assert.Contains("ctx[", trace);
        Assert.Contains("xtop[0]=(", trace);
        Assert.Contains("minWrite=", trace);
        Assert.Contains("minWindow[", trace);
        Assert.Contains("kickBase[", trace);
        Assert.Contains(batches, batch => batch.CommandTrace.Length > 0);
    }

    [Fact]
    public void Diagnostic_SkaterLasek_KickAnalysis()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(paths.TestOutputDir is null, "TestOutput not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var kicks = ThawPs2SkinFile.ReplayExtractKicks(data);

        var lines = new List<string>();
        lines.Add("=== THAW PS2 Kick Diagnostic: skater_lasek ===");
        lines.Add($"Total batches: {batches.Count}");
        lines.Add($"Total kicks: {kicks.Count}");
        lines.Add($"Total triangles: {kicks.Sum(k => k.TriangleCount)}");
        lines.Add("");

        // Summary by ADDR
        var addr280Kicks = kicks.Where(k => k.KickPacket.Address == 280).ToList();
        var addr652Kicks = kicks.Where(k => k.KickPacket.Address == 652).ToList();
        lines.Add($"ADDR=280 kicks: {addr280Kicks.Count}, triangles: {addr280Kicks.Sum(k => k.TriangleCount)}");
        lines.Add($"ADDR=652 kicks: {addr652Kicks.Count}, triangles: {addr652Kicks.Sum(k => k.TriangleCount)}");
        lines.Add("");

        // Per-kick details
        foreach (var kick in kicks)
        {
            var batch = batches.FirstOrDefault(b => b.FirstCommandOffset == kick.FirstCommandOffset);
            lines.Add($"--- Kick {kick.KickIndex} (batch {kick.BatchIndex}, setup {kick.SetupIndex}) ---");
            lines.Add($"  ADDR={kick.KickPacket.Address} NLOOP={kick.KickPacket.Nloop} EOP={kick.KickPacket.Eop} Kind={kick.KickPacket.Kind}");
            lines.Add($"  Offset=0x{kick.FirstCommandOffset:X6}");
            lines.Add($"  Triangles={kick.TriangleCount} Meshes={kick.Meshes.Length}");
            lines.Add($"  OutputWindow: [{kick.FullOutputWindow[0]}..{kick.FullOutputWindow[^1]}] (len={kick.FullOutputWindow.Length})");

            if (batch != null)
            {
                lines.Add($"  BatchVertexCount={batch.VertexCount} OutputVertexCount={batch.OutputVertexCount}");
                lines.Add($"  PostBatchElements={batch.PostBatchElements.Length}");

                // Post-batch element raw values (all)
                for (var i = 0; i < batch.PostBatchElements.Length; i++)
                {
                    var pbe = batch.PostBatchElements[i];
                    lines.Add($"    PBE[{i}]: C0=0x{pbe.C0:X4} C1=0x{pbe.C1:X4} C2=0x{pbe.C2:X4} C3=0x{pbe.C3:X4}");
                }

                // Context writes
                lines.Add($"  ContextWrites={batch.ContextWrites.Length}");
                foreach (var ctx in batch.ContextWrites)
                {
                    var fmt = $"V{ctx.Unpack.Vn + 1}_{32 >> ctx.Unpack.Vl}";
                    lines.Add($"    {(ctx.Unpack.Usn ? "U" : "")}{fmt} NUM={ctx.Unpack.Num} -> [{string.Join(",", ctx.WriteAddresses)}]");
                }

                // Snapshot info
                var snap = batch.Snapshot;
                lines.Add($"  Snapshot: Xtop={snap.Xtop} PreTops={snap.PreTops} PostTops={snap.PostTops} Dbf={snap.Dbf}");
                lines.Add($"  MinWrite={snap.MinWrittenAddress} MaxWrite={snap.MaxWrittenAddress}");

                // KickBaseWindow summary
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

            // Event analysis
            var vertexEvents = kick.Events.Count(e => e.Kind == GsVertexEventKind.Vertex);
            var gapEvents = kick.Events.Count(e => e.Kind == GsVertexEventKind.Gap);
            var carryEvents = kick.Events.Count(e => e.IsBufferedCarry);
            var noKickEvents = kick.Events.Count(e => e.IsNoKick);
            lines.Add($"  Events: {vertexEvents} vertex, {gapEvents} gap, {carryEvents} carry, {noKickEvents} noKick");

            // Detailed per-event dump for kick_028 (the problematic shoe kick)
            if (kick.KickIndex == 28)
            {
                lines.Add("  --- Per-event detail for kick_028 ---");
                for (var i = 0; i < kick.Events.Length; i++)
                {
                    var evt = kick.Events[i];
                    var srcInfo = evt.VertexSource is { } src
                        ? $"c2addr={src.OutputFullAddress} c3addr={src.DuplicateFullAddress} c2nk={src.OutputNoKick} c3nk={src.DuplicateNoKick} pos=({src.Position.X:F2},{src.Position.Y:F2},{src.Position.Z:F2})"
                        : "null";
                    lines.Add($"    [{i,3}] addr={evt.FullOutputAddress,4} {evt.Kind,-6} nk={evt.IsNoKick} carry={evt.IsBufferedCarry} {srcInfo}");
                }

                // Also dump which c2/c3 addresses each vertex source targets
                if (batch != null)
                {
                    lines.Add("  --- Vertex source address map ---");
                    foreach (var vs in batch.VertexSources)
                    {
                        var dupInfo = vs.DuplicateAddress != vs.OutputAddress
                            ? $" dup→{vs.DuplicateFullAddress}(nk={vs.DuplicateNoKick})"
                            : "";
                        lines.Add($"    c2→{vs.OutputFullAddress}(nk={vs.OutputNoKick}){dupInfo}  pos=({vs.Position.X:F2},{vs.Position.Y:F2},{vs.Position.Z:F2})");
                    }
                }
            }

            // Vertex source output addresses vs window
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
                        .OrderBy(a => a)
                        .ToArray();
                    lines.Add($"    Out-of-window addresses: [{string.Join(",", outAddrs)}]");
                }
            }
            lines.Add("");
        }

        // Batches with vertices but NO kick emitted (skipped by IsValidKick or resolvedBatchSlots=0)
        lines.Add("=== Batches with vertices but no kick ===");
        var kickBatchOffsets = kicks.Select(k => k.FirstCommandOffset).ToHashSet();
        var skippedBatches = batches
            .Where(b => b.VertexCount > 0 && !kickBatchOffsets.Contains(b.FirstCommandOffset))
            .ToList();
        lines.Add($"Count: {skippedBatches.Count}");
        foreach (var sb in skippedBatches)
        {
            lines.Add($"  Offset=0x{sb.FirstCommandOffset:X6} Setup={sb.SetupIndex} Verts={sb.VertexCount}");
            lines.Add($"    KickPacket: Kind={sb.OutputKickPacket.Kind} ADDR={sb.OutputKickPacket.Address} NLOOP={sb.OutputKickPacket.Nloop} EOP={sb.OutputKickPacket.Eop}");
        }

        var outputPath = Path.Combine(paths.TestOutputDir!, "skater_lasek_kick_diagnostic.txt");
        File.WriteAllLines(outputPath, lines);

        // Basic assertions to keep test meaningful
        Assert.True(kicks.Count > 0);
        Assert.True(addr280Kicks.Count > 0, "Expected some ADDR=280 kicks");
        Assert.True(addr652Kicks.Count > 0, "Expected some ADDR=652 kicks");
    }

    // ── Per-Triangle PS2 vs PC Diagnostic ──

    [Theory]
    [InlineData("skater_lasek", 3070)]
    [InlineData("pro_vallely_head", 710)]  // PC has 711 raw, 1 degenerate
    public void Diagnostic_Ps2VsPc_TriangleComparison(string stem, int expectedPcTriangles)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(paths.TestOutputDir is null, "TestOutput not available");

        var ps2File = Path.Combine(ThawSkinDir, $"{stem}.skin.ps2");
        var pcFile = Path.Combine(ThawPcSkinDir, $"{stem}.skin.wpc");
        Assert.SkipWhen(!File.Exists(ps2File), $"PS2 file not found: {stem}");
        Assert.SkipWhen(!File.Exists(pcFile), $"PC file not found: {stem}");

        // --- Parse PC ground truth ---
        var pcScene = ThawSceneFile.Parse(pcFile);
        var pcTriangles = new HashSet<(Vector3, Vector3, Vector3)>();
        var pcPositions = new HashSet<Vector3>();
        var pcTrisBySector = new Dictionary<int, int>();
        var pcVertsBySector = new Dictionary<int, int>();
        for (var si = 0; si < pcScene.Sectors.Length; si++)
        {
            var sector = pcScene.Sectors[si];
            var sectorTris = 0;
            var sectorVerts = new HashSet<Vector3>();
            foreach (var mesh in sector.Meshes)
            {
                for (var i = 0; i + 2 < mesh.FaceIndices.Length; i += 3)
                {
                    var p0 = mesh.Vertices[mesh.FaceIndices[i]].Position;
                    var p1 = mesh.Vertices[mesh.FaceIndices[i + 1]].Position;
                    var p2 = mesh.Vertices[mesh.FaceIndices[i + 2]].Position;
                    pcPositions.Add(p0); pcPositions.Add(p1); pcPositions.Add(p2);
                    sectorVerts.Add(p0); sectorVerts.Add(p1); sectorVerts.Add(p2);

                    if (p0 == p1 || p1 == p2 || p0 == p2) continue;
                    pcTriangles.Add(SortedTriKey(p0, p1, p2));
                    sectorTris++;
                }
            }
            pcTrisBySector[si] = sectorTris;
            pcVertsBySector[si] = sectorVerts.Count;
        }

        // --- Parse PS2 kicks ---
        var ps2Data = File.ReadAllBytes(ps2File);
        var kicks = ThawPs2SkinFile.ReplayExtractKicks(ps2Data);

        // Build PS2 triangle set from kick meshes
        var ps2Triangles = new HashSet<(Vector3, Vector3, Vector3)>();
        var ps2TriByKick = new Dictionary<int, List<(Vector3, Vector3, Vector3)>>();
        var ps2Positions = new HashSet<Vector3>();

        foreach (var kick in kicks)
        {
            var kickTris = new List<(Vector3, Vector3, Vector3)>();
            ps2TriByKick[kick.KickIndex] = kickTris;

            foreach (var mesh in kick.Meshes)
            {
                var verts = mesh.Vertices;
                var stripStart = 0;
                var parityBias = mesh.StartsOnOddOutputSlot ? 1 : 0;

                for (var i = 0; i < verts.Length; i++)
                {
                    ps2Positions.Add(verts[i].Position);
                    if (verts[i].IsStripRestart) continue;
                    if (i - stripStart < 2) continue;

                    Vector3 a, b, c;
                    if (((i - stripStart + parityBias) & 1) == 0)
                    { a = verts[i - 2].Position; b = verts[i - 1].Position; c = verts[i].Position; }
                    else
                    { a = verts[i - 1].Position; b = verts[i - 2].Position; c = verts[i].Position; }

                    if (a == b || b == c || a == c) continue;
                    var key = SortedTriKey(a, b, c);
                    ps2Triangles.Add(key);
                    kickTris.Add(key);
                }
            }
        }

        // --- Build position lookup: PS2 position → which kicks contain it ---
        var posToKicks = new Dictionary<Vector3, List<int>>();
        foreach (var kick in kicks)
        foreach (var mesh in kick.Meshes)
        foreach (var v in mesh.Vertices)
        {
            if (!posToKicks.TryGetValue(v.Position, out var kickList))
            {
                kickList = [];
                posToKicks[v.Position] = kickList;
            }
            if (!kickList.Contains(kick.KickIndex))
                kickList.Add(kick.KickIndex);
        }

        // --- Compare ---
        var matched = pcTriangles.Intersect(ps2Triangles).Count();
        var pcOnly = pcTriangles.Except(ps2Triangles).ToList();
        var ps2Only = ps2Triangles.Except(pcTriangles).ToList();

        var lines = new List<string>();
        lines.Add($"=== PS2 vs PC Triangle Diagnostic: {stem} ===");
        lines.Add($"PC triangles (non-degenerate): {pcTriangles.Count}");
        lines.Add($"PS2 triangles (non-degenerate): {ps2Triangles.Count}");
        lines.Add($"Matched: {matched}");
        lines.Add($"PC-only (missing from PS2): {pcOnly.Count}");
        lines.Add($"PS2-only (phantom): {ps2Only.Count}");
        lines.Add($"PC unique positions: {pcPositions.Count}");
        lines.Add($"PS2 unique positions: {ps2Positions.Count}");
        lines.Add("");

        // --- Analyze missing PC triangles ---
        lines.Add("=== Missing PC Triangles Analysis ===");
        var allVertsInPs2 = 0;
        var twoVertsInPs2 = 0;
        var oneVertInPs2 = 0;
        var noVertsInPs2 = 0;

        // Track which kicks are involved in missing triangles
        var kickGapCounts = new Dictionary<int, int>();

        foreach (var (p0, p1, p2) in pcOnly)
        {
            var has0 = ps2Positions.Contains(p0);
            var has1 = ps2Positions.Contains(p1);
            var has2 = ps2Positions.Contains(p2);
            var count = (has0 ? 1 : 0) + (has1 ? 1 : 0) + (has2 ? 1 : 0);

            switch (count)
            {
                case 3: allVertsInPs2++; break;
                case 2: twoVertsInPs2++; break;
                case 1: oneVertInPs2++; break;
                case 0: noVertsInPs2++; break;
            }

            // For triangles with all 3 verts in PS2: find which kicks contain them
            if (count == 3)
            {
                var k0 = posToKicks.GetValueOrDefault(p0, []);
                var k1 = posToKicks.GetValueOrDefault(p1, []);
                var k2 = posToKicks.GetValueOrDefault(p2, []);
                var sharedKicks = k0.Intersect(k1).Intersect(k2).ToList();
                foreach (var ki in sharedKicks)
                    kickGapCounts[ki] = kickGapCounts.GetValueOrDefault(ki) + 1;
            }
        }

        lines.Add($"Missing triangles with 3/3 verts in PS2: {allVertsInPs2} (topology diff)");
        lines.Add($"Missing triangles with 2/3 verts in PS2: {twoVertsInPs2} (1 vert missing)");
        lines.Add($"Missing triangles with 1/3 verts in PS2: {oneVertInPs2}");
        lines.Add($"Missing triangles with 0/3 verts in PS2: {noVertsInPs2}");
        lines.Add("");

        // --- Per-kick gap analysis ---
        lines.Add("=== Per-Kick Gap Analysis ===");
        foreach (var kick in kicks)
        {
            var gaps = kick.Events.Count(e => e.Kind == GsVertexEventKind.Gap);
            var carries = kick.Events.Count(e => e.IsBufferedCarry);
            var noKicks = kick.Events.Count(e => e.IsNoKick);
            var missingTrisInKick = kickGapCounts.GetValueOrDefault(kick.KickIndex);

            if (gaps == 0 && missingTrisInKick == 0) continue;

            lines.Add($"Kick {kick.KickIndex}: setup={kick.SetupIndex} addr={kick.KickPacket.Address} nloop={kick.KickPacket.Nloop}");
            lines.Add($"  gaps={gaps} carries={carries} noKicks={noKicks} tris={kick.TriangleCount} meshes={kick.Meshes.Length}");
            lines.Add($"  missingPcTris (all 3 verts in this kick): {missingTrisInKick}");

            // Show gap positions in the output window
            if (gaps > 0)
            {
                var gapAddrs = kick.Events
                    .Where(e => e.Kind == GsVertexEventKind.Gap)
                    .Select(e => e.FullOutputAddress)
                    .ToList();
                lines.Add($"  gap addresses: [{string.Join(",", gapAddrs)}]");

                // Classify gap runs (consecutive gaps vs isolated)
                var gapRuns = new List<(int Start, int Length)>();
                for (var g = 0; g < gapAddrs.Count; g++)
                {
                    if (g == 0 || gapAddrs[g] - gapAddrs[g - 1] != 3)
                        gapRuns.Add((gapAddrs[g], 1));
                    else
                        gapRuns[^1] = (gapRuns[^1].Start, gapRuns[^1].Length + 1);
                }
                lines.Add($"  gap runs: {gapRuns.Count} (sizes: {string.Join(",", gapRuns.Select(r => r.Length))})");
            }
            lines.Add("");
        }

        // --- Cross-kick missing triangles (verts in different kicks) ---
        lines.Add("=== Cross-Kick Missing Triangles ===");
        var crossKickCount = 0;
        var sameKickCount = 0;
        foreach (var (p0, p1, p2) in pcOnly)
        {
            if (!ps2Positions.Contains(p0) || !ps2Positions.Contains(p1) || !ps2Positions.Contains(p2))
                continue;

            var k0 = posToKicks.GetValueOrDefault(p0, []);
            var k1 = posToKicks.GetValueOrDefault(p1, []);
            var k2 = posToKicks.GetValueOrDefault(p2, []);
            var sharedKicks = k0.Intersect(k1).Intersect(k2).ToList();

            if (sharedKicks.Count > 0)
                sameKickCount++;
            else
                crossKickCount++;
        }
        lines.Add($"All 3 verts in same kick(s): {sameKickCount}");
        lines.Add($"Verts spread across kicks: {crossKickCount}");
        lines.Add("");

        // --- Phantom PS2 triangles ---
        if (ps2Only.Count > 0)
        {
            lines.Add($"=== Phantom PS2 Triangles (first 20) ===");
            foreach (var (p0, p1, p2) in ps2Only.Take(20))
            {
                var k0 = posToKicks.GetValueOrDefault(p0, []);
                lines.Add($"  ({p0.X:F2},{p0.Y:F2},{p0.Z:F2})-({p1.X:F2},{p1.Y:F2},{p1.Z:F2})-({p2.X:F2},{p2.Y:F2},{p2.Z:F2}) kicks=[{string.Join(",", k0)}]");
            }
            lines.Add("");
        }

        // --- Summary by kick address ---
        lines.Add("=== Summary by Kick Address ===");
        foreach (var addr in new[] { 280, 652 })
        {
            var addrKicks = kicks.Where(k => k.KickPacket.Address == addr).ToList();
            var totalGaps = addrKicks.Sum(k => k.Events.Count(e => e.Kind == GsVertexEventKind.Gap));
            var totalTris = addrKicks.Sum(k => k.TriangleCount);
            var totalMissing = addrKicks.Sum(k => kickGapCounts.GetValueOrDefault(k.KickIndex));
            lines.Add($"ADDR={addr}: {addrKicks.Count} kicks, {totalTris} tris, {totalGaps} gaps, {totalMissing} missing PC tris in same kick");
        }
        lines.Add("");

        // --- Raw VIF batch vertex count vs kicked vertex count ---
        var batches = ThawPs2SkinFile.ReplayBatches(ps2Data);
        var allBatchPositions = new HashSet<Vector3>();
        var totalBatchVerts = 0;
        foreach (var batch in batches)
        {
            totalBatchVerts += batch.VertexCount;
            foreach (var vs in batch.VertexSources)
                allBatchPositions.Add(vs.Position);
        }
        lines.Add("=== VIF Batch Vertex Analysis ===");
        lines.Add($"Total VIF batches: {batches.Count}");
        lines.Add($"Total VIF vertices (raw): {totalBatchVerts}");
        lines.Add($"Unique VIF positions (all batches): {allBatchPositions.Count}");
        lines.Add($"Unique kicked positions: {ps2Positions.Count}");
        lines.Add($"VIF positions not in kicks: {allBatchPositions.Except(ps2Positions).Count()}");
        lines.Add($"PC positions not in VIF: {pcPositions.Except(allBatchPositions).Count()}");
        lines.Add($"PC positions found in VIF: {pcPositions.Intersect(allBatchPositions).Count()}");
        lines.Add("");

        // --- PC sector breakdown ---
        lines.Add("=== PC Sector Breakdown ===");
        lines.Add($"PC sectors: {pcScene.Sectors.Length}");
        for (var si = 0; si < pcScene.Sectors.Length; si++)
        {
            var sector = pcScene.Sectors[si];
            var sectorPositions = new HashSet<Vector3>();
            foreach (var mesh in sector.Meshes)
            foreach (var v in mesh.Vertices)
                sectorPositions.Add(v.Position);
            var inPs2 = sectorPositions.Intersect(allBatchPositions).Count();
            lines.Add($"  Sector {si}: ck=0x{sector.Checksum:X8} meshes={sector.Meshes.Length} " +
                       $"tris={pcTrisBySector[si]} verts={pcVertsBySector[si]} " +
                       $"ps2_match={inPs2}/{sectorPositions.Count} ({100.0 * inPs2 / Math.Max(1, sectorPositions.Count):F0}%)");

            // Per-mesh breakdown
            for (var mi = 0; mi < sector.Meshes.Length; mi++)
            {
                var mesh = sector.Meshes[mi];
                var meshPositions = new HashSet<Vector3>();
                foreach (var v in mesh.Vertices)
                    meshPositions.Add(v.Position);
                var meshInPs2 = meshPositions.Intersect(allBatchPositions).Count();
                var meshTris = mesh.IsPreTriangulated ? mesh.FaceIndices.Length / 3 : mesh.TriangleCount;
                lines.Add($"    Mesh {mi}: mat=0x{mesh.MaterialChecksum:X8} tris={meshTris} verts={meshPositions.Count} " +
                           $"ps2_match={meshInPs2}/{meshPositions.Count} ({100.0 * meshInPs2 / Math.Max(1, meshPositions.Count):F0}%)");
            }
        }
        lines.Add("");

        // --- PS2 entry table breakdown ---
        lines.Add("=== PS2 Entry Table ===");
        lines.Add($"PS2 kicks: {kicks.Count}");
        var ps2Header = BitConverter.ToUInt32(ps2Data, 0);
        var ps2TotalMeshes2 = BitConverter.ToUInt32(ps2Data, 8);
        lines.Add($"PS2 numObjects: {ps2Header}");
        lines.Add($"PS2 totalMeshes2 (entry table entries): {ps2TotalMeshes2}");
        lines.Add($"PS2 setup indices used: [{string.Join(",", kicks.Select(k => k.SetupIndex).Distinct().OrderBy(x => x))}]");
        lines.Add("");

        // --- Missing PC positions detail ---
        var missingPcPositions = pcPositions.Except(allBatchPositions).ToList();
        lines.Add($"=== Missing PC Positions ({missingPcPositions.Count}) ===");
        // Show which PC sectors contain these missing positions
        for (var si = 0; si < pcScene.Sectors.Length; si++)
        {
            var sector = pcScene.Sectors[si];
            var sectorPositions = new HashSet<Vector3>();
            foreach (var mesh in sector.Meshes)
            foreach (var v in mesh.Vertices)
                sectorPositions.Add(v.Position);
            var sectorMissing = sectorPositions.Intersect(missingPcPositions.ToHashSet()).Count();
            if (sectorMissing > 0)
                lines.Add($"  Sector {si} contributes {sectorMissing} missing positions");
        }
        lines.Add("");
        foreach (var pos in missingPcPositions.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).Take(30))
        {
            var nearest = allBatchPositions
                .OrderBy(p => Vector3.DistanceSquared(p, pos))
                .First();
            var dist = Vector3.Distance(nearest, pos);
            lines.Add($"  ({pos.X:F2},{pos.Y:F2},{pos.Z:F2}) nearest PS2=({nearest.X:F2},{nearest.Y:F2},{nearest.Z:F2}) dist={dist:F4}");
        }

        var outputPath = Path.Combine(paths.TestOutputDir!, $"{stem}_tri_diagnostic.txt");
        File.WriteAllLines(outputPath, lines);

        Assert.Equal(expectedPcTriangles, pcTriangles.Count);
    }

    [Theory]
    [InlineData("skater_lasek")]
    [InlineData("pro_vallely_head")]
    public void Diagnostic_GapTrace(string stem)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(paths.TestOutputDir is null, "TestOutput not available");

        var ps2File = Path.Combine(ThawSkinDir, $"{stem}.skin.ps2");
        Assert.SkipWhen(!File.Exists(ps2File), $"PS2 file not found: {stem}");

        var ps2Data = File.ReadAllBytes(ps2File);
        var batches = ThawPs2SkinFile.ReplayBatches(ps2Data);
        var kicks = ThawPs2SkinFile.ReplayExtractKicks(ps2Data);

        var lines = new List<string>();
        lines.Add($"=== Gap Trace Diagnostic: {stem} ===");
        lines.Add($"Total batches: {batches.Count}");
        lines.Add($"Total kicks: {kicks.Count}");
        lines.Add("");

        // Build a map of ALL vertex source addresses across ALL batches (including preamble)
        var allSourceAddresses = new Dictionary<int, List<(int BatchIdx, ReplayVertexSource Source)>>();
        for (var bi = 0; bi < batches.Count; bi++)
        {
            foreach (var src in batches[bi].VertexSources)
            {
                var addr = src.OutputFullAddress;
                if (!allSourceAddresses.TryGetValue(addr, out var list))
                {
                    list = [];
                    allSourceAddresses[addr] = list;
                }
                list.Add((bi, src));

                if (src.DuplicateAddress != src.OutputAddress)
                {
                    var dupAddr = src.DuplicateFullAddress;
                    if (!allSourceAddresses.TryGetValue(dupAddr, out var dupList))
                    {
                        dupList = [];
                        allSourceAddresses[dupAddr] = dupList;
                    }
                    dupList.Add((bi, src));
                }
            }
        }

        // For each kick with gaps, trace each gap
        foreach (var kick in kicks)
        {
            var gaps = kick.Events.Where(e => e.Kind == GsVertexEventKind.Gap).ToList();
            if (gaps.Count == 0) continue;

            lines.Add($"--- Kick {kick.KickIndex}: setup={kick.SetupIndex} entry={kick.EntryIndex} " +
                       $"batch={kick.BatchIndex} addr={kick.KickPacket.Address} nloop={kick.KickPacket.Nloop} " +
                       $"tris={kick.TriangleCount} gaps={gaps.Count} meshes={kick.Meshes.Length} " +
                       $"preamble={kick.IsPreambleBatch} ---");

            var batch = batches[kick.BatchIndex];
            lines.Add($"  Batch info: vtxCount={batch.VertexCount} outputVtxCount={batch.OutputVertexCount}");
            lines.Add($"  Batch offsets: pos=0x{batch.PositionOffset:X} nrm=0x{batch.NormalOffset:X} uvadc=0x{batch.UvAdcOffset:X}");
            lines.Add($"  PostBatch elements: {batch.PostBatchElements.Length}");
            lines.Add($"  ParserTag: {batch.Snapshot.ParserTag.Kind} nloop={batch.Snapshot.ParserTag.Nloop} addr={batch.Snapshot.ParserTag.Address}");
            lines.Add($"  Snapshot: xtop={batch.Snapshot.Xtop} preTops={batch.Snapshot.PreTops} postTops={batch.Snapshot.PostTops} dbf={batch.Snapshot.Dbf}");
            lines.Add("");

            // Log all vertex source addresses in this batch
            lines.Add($"  Vertex source output addresses ({batch.VertexSources.Length}):");
            foreach (var src in batch.VertexSources)
            {
                var dupInfo = src.DuplicateAddress != src.OutputAddress
                    ? $" dup={src.DuplicateFullAddress}(noKick={src.DuplicateNoKick})"
                    : "";
                lines.Add($"    addr={src.OutputFullAddress} noKick={src.OutputNoKick}{dupInfo} pos=({src.Position.X:F2},{src.Position.Y:F2},{src.Position.Z:F2})");
            }
            lines.Add("");

            // Log post-batch copy pairs
            if (batch.PostBatchElements.Length > 0)
            {
                lines.Add($"  Post-batch copy pairs:");
                var firstC0Tag = (batch.PostBatchElements[0].C0 & 0x8000) != 0;
                var firstC2Tag = (batch.PostBatchElements[0].C2 & 0x8000) != 0;
                for (var i = 0; i < batch.PostBatchElements.Length; i++)
                {
                    var el = batch.PostBatchElements[i];
                    var c0Active = !(i == 0 && firstC0Tag);
                    var c2Active = !(i == 0 && firstC2Tag);
                    if (c0Active)
                        lines.Add($"    C0→C1: src={el.C0 & 0x3FF}(raw=0x{el.C0:X4}) → dst={el.C1 & 0x3FF}(raw=0x{el.C1:X4})");
                    else
                        lines.Add($"    C0→C1: [TAG] raw=0x{el.C0:X4},0x{el.C1:X4}");
                    if (c2Active)
                        lines.Add($"    C2→C3: src={el.C2 & 0x3FF}(raw=0x{el.C2:X4}) → dst={el.C3 & 0x3FF}(raw=0x{el.C3:X4})");
                    else
                        lines.Add($"    C2→C3: [TAG] raw=0x{el.C2:X4},0x{el.C3:X4}");
                }
                lines.Add("");
            }

            // Output window
            lines.Add($"  Output window ({kick.FullOutputWindow.Length} slots):");
            for (var i = 0; i < kick.Events.Length; i++)
            {
                var evt = kick.Events[i];
                var marker = evt.Kind == GsVertexEventKind.Gap ? "GAP" :
                             evt.IsNoKick ? "NOK" :
                             evt.IsBufferedCarry ? "CAR" : "VTX";
                var posInfo = evt.VertexSource != null
                    ? $" pos=({evt.VertexSource.Value.Position.X:F2},{evt.VertexSource.Value.Position.Y:F2},{evt.VertexSource.Value.Position.Z:F2})"
                    : "";

                // Check if any batch ever wrote to this address
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

    private static (Vector3, Vector3, Vector3) SortedTriKey(Vector3 a, Vector3 b, Vector3 c)
    {
        if (Compare(a, b) > 0) (a, b) = (b, a);
        if (Compare(b, c) > 0) (b, c) = (c, b);
        if (Compare(a, b) > 0) (a, b) = (b, a);
        return (a, b, c);

        static int Compare(Vector3 x, Vector3 y)
        {
            var cmp = x.X.CompareTo(y.X);
            if (cmp != 0) return cmp;
            cmp = x.Y.CompareTo(y.Y);
            return cmp != 0 ? cmp : x.Z.CompareTo(y.Z);
        }
    }
}
