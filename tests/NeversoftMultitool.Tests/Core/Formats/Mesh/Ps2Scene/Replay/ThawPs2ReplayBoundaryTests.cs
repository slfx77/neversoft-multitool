using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Replay;

public sealed class ThawPs2ReplayBoundaryTests
{
    [Fact]
    public void ReplayBatches_CommandHeaderCrossesChainEnd_ReturnsNoBatch()
    {
        byte[] data = [0, 0, 0, 0x14];

        var batches = ThawPs2ReplayEngine.ReplayBatches(data, 0, 2, []);

        Assert.Empty(batches);
    }

    [Fact]
    public void ReplayBatches_CommandHeaderEndsAtChainEnd_RemainsAccepted()
    {
        byte[] data = [0, 0, 0, 0x14];

        var batch = Assert.Single(ThawPs2ReplayEngine.ReplayBatches(data, 0, 4, []));

        var command = Assert.Single(batch.CommandTrace);
        Assert.Equal(VifReplayCommandKind.Mscal, command.Kind);
        Assert.Equal(0, command.CommandOffset);
    }

    [Fact]
    public void ReplayBatches_CommandBodyCrossesChainEnd_ReturnsNoBatch()
    {
        byte[] data = [0, 0, 0, 0x20, 0x78, 0x56, 0x34, 0x12];

        var batches = ThawPs2ReplayEngine.ReplayBatches(data, 0, 4, []);

        Assert.Empty(batches);
    }

    [Fact]
    public void ReplayBatches_CommandBodyEndsAtChainEnd_RemainsAccepted()
    {
        byte[] data = [0, 0, 0, 0x20, 0x78, 0x56, 0x34, 0x12];

        var batch = Assert.Single(ThawPs2ReplayEngine.ReplayBatches(data, 0, 8, []));

        var command = Assert.Single(batch.CommandTrace);
        Assert.Equal(VifReplayCommandKind.Stmask, command.Kind);
        Assert.Equal(0x12345678u, command.After.Stmask);
    }
}
