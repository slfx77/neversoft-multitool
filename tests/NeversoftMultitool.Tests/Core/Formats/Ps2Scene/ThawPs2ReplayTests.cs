using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class ThawPs2ReplayTests(TestPaths paths)
{
    private static readonly int[] ValidKickAddresses = [280, 652];

    private string ThawSkinDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "SKIN");

    [Fact]
    public void ReplayBatches_SkaterLasek_HasReplayBatchMetadata()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);

        Assert.True(batches.Count > 10, "Expected multiple replay batches");

        var first = batches[0];
        Assert.True(first.IsPreambleBatch);
        Assert.Equal(0, first.SetupIndex);
        Assert.Equal(0x0004BC, first.FirstCommandOffset);
        Assert.Equal(51, first.VertexCount);
        Assert.Equal(first.VertexCount, first.VertexSources.Length);
        Assert.Equal(first.VertexCount, first.RawVertexSources.Length);
        Assert.True(first.EmittedVertices.Length > first.VertexSources.Length);
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
    public void ReplayBatches_SkaterLasek_IncludesInitialMeshZeroPreambleBatch()
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
        Assert.Equal(51, first.VertexCount);
        Assert.Equal(0x0004BC, first.FirstCommandOffset);
        Assert.True(first.CommandTrace[^1].CommandOffset > first.FirstCommandOffset);
        Assert.Contains(first.CommandTrace, command => command.Kind == VifReplayCommandKind.Direct);
        Assert.Contains(first.CommandTrace, command => command.Kind == VifReplayCommandKind.Offset);
        Assert.Contains(first.CommandTrace, command => command.Kind == VifReplayCommandKind.Base);
        Assert.Equal(VifReplayCommandKind.Mscnt, first.CommandTrace[^1].Kind);
        Assert.True(first.ContextWrites.Length > 0);
        Assert.True(first.OutputKickPacket.Nloop > 0);
        Assert.Contains(first.OutputKickPacket.Address, ValidKickAddresses);
    }

    [Fact]
    public void ReplayBatches_SkaterLasek_TracksFirstMaterialKickSnapshot()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var firstMaterialKick = Assert.Single(batches, batch =>
            batch.FirstCommandOffset == 0x001F3C &&
            batch.CommandTrace.Length > 0 &&
            batch.CommandTrace[^1].CommandOffset == 0x0022EC);

        Assert.Equal(0x001F3C, firstMaterialKick.FirstCommandOffset);
        Assert.Contains(firstMaterialKick.CommandTrace, command => command.Kind == VifReplayCommandKind.Direct);
        Assert.Equal(652, firstMaterialKick.Snapshot.Xtop);
        Assert.Equal(0, firstMaterialKick.Snapshot.PostTops);
        Assert.Equal(firstMaterialKick.OutputKickPacket.Nloop, firstMaterialKick.OutputVertexCount);
        Assert.Contains(firstMaterialKick.OutputKickPacket.Address, ValidKickAddresses);
        Assert.True(firstMaterialKick.Snapshot.XtopWindow.Length > 0);
    }

    [Fact]
    public void ReplayBatches_SkaterLasek_FirstMaterialBatch_CapturesContextWrites()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var firstMaterialKick = Assert.Single(batches, batch => batch.FirstCommandOffset == 0x001F3C);

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
        var expectedVertexCounts = new[] { 43, 35, 42, 37, 27 };

        for (var i = 0; i < meshZeroBatches.Length; i++)
        {
            Assert.Equal(expectedCommandOffsets[i], meshZeroBatches[i].CommandTrace[^1].CommandOffset);
            Assert.Equal(expectedXtops[i], meshZeroBatches[i].Snapshot.Xtop);
            Assert.Equal(expectedVertexCounts[i], meshZeroBatches[i].VertexCount);
            Assert.True(meshZeroBatches[i].OutputKickPacket.Nloop > 0);
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
        var batch = Assert.Single(batches, candidate => candidate.FirstCommandOffset == 0x0038A0);

        Assert.Equal(0, batch.Snapshot.Xtop);
        Assert.Equal(892, batch.Snapshot.MinWrittenAddress);
        Assert.Equal(1023, batch.Snapshot.MaxWrittenAddress);
        Assert.Equal(890, batch.Snapshot.MinWriteWindowStart);
        Assert.Equal(6, batch.Snapshot.MinWriteWindow.Length);
        Assert.Contains(batch.Snapshot.MinWriteWindow,
            word => word == new Vu1Qword(0xFFFF804C, 0x0000028C, 0x00000000, 0x00000000));
        Assert.Contains(batch.Snapshot.MinWriteWindow, word =>
            word != new Vu1Qword(0x00000000, 0x00000000, 0x00000000, 0x00000000));
    }

    [Fact]
    public void ReplayBatches_SkaterLasek_FlaggedAddr652Batch0038A0_CapturesWrappedContextWrites()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);
        var batch = Assert.Single(batches, candidate => candidate.FirstCommandOffset == 0x0038A0);

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
        var batch = Assert.Single(batches, candidate => candidate.FirstCommandOffset == 0x004568);

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
        var setup02ShoeKick = Assert.Single(batches, candidate => candidate.FirstCommandOffset == 0x006004);
        var setup04ShoeKick = Assert.Single(batches, candidate => candidate.FirstCommandOffset == 0x0079A8);

        Assert.Equal(280, setup02ShoeKick.OutputKickPacket.Address);
        Assert.Equal(280, setup04ShoeKick.OutputKickPacket.Address);
        Assert.Equal(263, setup02ShoeKick.Snapshot.KickBaseWindowStart);
        Assert.Equal(263, setup04ShoeKick.Snapshot.KickBaseWindowStart);
        Assert.Equal(18, setup02ShoeKick.Snapshot.KickBaseWindow.Length);
        Assert.Equal(18, setup04ShoeKick.Snapshot.KickBaseWindow.Length);
        Assert.Equal(setup02ShoeKick.Snapshot.KickBaseWindow, setup04ShoeKick.Snapshot.KickBaseWindow);
        Assert.All(setup02ShoeKick.Snapshot.KickBaseWindow, word => Assert.Equal(new Vu1Qword(0, 0, 0, 0), word));
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
    public void ReplayBatches_ProVallelyHead_UsesEarlierRawBoundary()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "pro_vallely_head.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var batches = ThawPs2SkinFile.ReplayBatches(data);

        Assert.True(batches.Count > 1, "Expected replay batches");
        Assert.Equal(0x00015C, batches[0].FirstCommandOffset);
        Assert.True(batches[0].IsPreambleBatch);
        Assert.Equal(45, batches[0].VertexCount);
        Assert.Contains(batches, batch => batch.FirstCommandOffset == 0x00217C && batch.SetupIndex == 1);
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
            Assert.Contains(kick.KickPacket.Address, ValidKickAddresses);
            Assert.Equal(kick.KickPacket.Nloop, kick.OutputWindow.Length);
            Assert.Equal(kick.KickPacket.Nloop, kick.FullOutputWindow.Length);
            Assert.Equal(kick.OutputWindow.Length, kick.Events.Length);
        });

        Assert.All(kicks, kick => Assert.DoesNotContain(kick.Events, evt => evt.Kind == GsVertexEventKind.Gap));
        Assert.Equal(3070, kicks.Sum(kick => kick.TriangleCount));
    }

    [Fact]
    public void ReplayExtractKicks_ProVallelyHead_HasGapFreeKicks()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "pro_vallely_head.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var kicks = ThawPs2SkinFile.ReplayExtractKicks(data);

        Assert.True(kicks.Count > 0, "Expected replay kicks");
        Assert.All(kicks, kick => Assert.DoesNotContain(kick.Events, evt => evt.Kind == GsVertexEventKind.Gap));
        Assert.Equal(605, kicks.Sum(kick => kick.TriangleCount));
    }

    [Fact]
    public void ReplayExtractKicks_SkaterLasek_FirstMaterialKick_IsAnchoredToCurrentWindow()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        var kicks = ThawPs2SkinFile.ReplayExtractKicks(data);
        var firstMaterialKick = Assert.Single(kicks, kick => kick.FirstCommandOffset == 0x001F3C);

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
        var firstMaterialKick = Assert.Single(kicks, kick => kick.FirstCommandOffset == 0x001F3C);
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
}
