using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class ThawPs2ReplayTests(TestPaths paths)
{
    private string ThawSkinDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "SKIN");

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

        Assert.Equal(2930, kicks.Sum(kick => kick.TriangleCount)); // PS2 VIF has 49 fewer vertices than PC (3070)
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
}
