using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Qb;
using NeversoftMultitool.Core.Formats.Script;

namespace NeversoftMultitool.Tests.Core.Formats.Script;

public sealed class ScriptAssetParserTests
{
    [Theory]
    [InlineData("level.qb", "Qb")]
    [InlineData("scripts/level.qb.ps2", "Qb")]
    [InlineData("sound\\menu.SQB.NGC", "Qb")]
    [InlineData("level.sqb.xen", "Qb")]
    [InlineData("level.trg", "Trg")]
    [InlineData("levels/warehouse.TRG.N64", "Trg")]
    [InlineData("level.trg.wpc", "Trg")]
    public void CandidatePolicy_ClassifiesBareAndPlatformQualifiedNames(
        string entryName,
        string expected)
    {
        Assert.True(ScriptAssetParser.IsCandidateEntryName(entryName));
        Assert.Equal(expected, ScriptAssetParser.ClassifyEntryName(entryName)?.ToString());
    }

    [Theory]
    [InlineData("level.q")]
    [InlineData("level.mqb.ps2")]
    [InlineData("level.qb.tmp")]
    [InlineData("level.trg.n64.bak")]
    [InlineData("level.sqb.ps2.extra")]
    public void CandidatePolicy_RejectsUnrelatedOrTrailingNames(string entryName)
    {
        Assert.False(ScriptAssetParser.IsCandidateEntryName(entryName));
        Assert.Null(ScriptAssetParser.ClassifyEntryName(entryName));
    }

    [Fact]
    public void Parsers_ReadAssetBytesAndPreserveEntryName()
    {
        var qbSource = new MemoryAssetSource("archive.prx::scripts/menu.qb.ps2", "menu.qb.ps2", [0]);
        var trgSource = new MemoryAssetSource(
            "archive.prx::levels/start.trg.n64",
            "start.trg.n64",
            BuildTerminatorTrg());

        var qb = ScriptAssetParser.ParseQb(qbSource);
        var trg = ScriptAssetParser.ParseTrg(trgSource);

        Assert.Equal("menu.qb.ps2", qb.FileName);
        Assert.Equal(QbTokenType.EndOfFile, Assert.Single(qb.Tokens).Type);
        Assert.Equal("start.trg.n64", trg.FileName);
        Assert.Equal(1, trg.NodeCount);
        Assert.Equal(255, Assert.Single(trg.Nodes).TypeId);
        Assert.Equal(1, qbSource.ReadCount);
        Assert.Equal(1, trgSource.ReadCount);
        Assert.Null(qbSource.FileSystemPath);
        Assert.Null(trgSource.FileSystemPath);
    }

    [Fact]
    public void Materialize_ReadsOnceAndRemainsUsableWithoutOriginalSource()
    {
        var original = new MemoryAssetSource(
            "archive.prx::scripts/menu.qb.ps2",
            "menu.qb.ps2",
            [0]);

        var buffered = ScriptAssetParser.Materialize(original);
        var first = ScriptAssetParser.ParseQb(buffered);
        var second = ScriptAssetParser.ParseQb(buffered);

        Assert.Equal(1, original.ReadCount);
        Assert.Equal(original.DisplayName, buffered.DisplayName);
        Assert.Equal(original.EntryName, buffered.EntryName);
        Assert.Equal(QbTokenType.EndOfFile, Assert.Single(first.Tokens).Type);
        Assert.Equal(QbTokenType.EndOfFile, Assert.Single(second.Tokens).Type);
        Assert.False(buffered.CompanionExists("menu.qb.ps2"));
        Assert.Null(buffered.TryReadCompanion("menu.qb.ps2"));
        Assert.Null(buffered.FileSystemPath);
    }

    private static byte[] BuildTerminatorTrg()
    {
        var data = new byte[18];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x4752545F);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x00010002);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), 255);
        return data;
    }

    private sealed class MemoryAssetSource(
        string displayName,
        string entryName,
        byte[] bytes) : AssetSource
    {
        public int ReadCount { get; private set; }
        public override string DisplayName => displayName;
        public override string EntryName => entryName;

        public override byte[] ReadBytes()
        {
            ReadCount++;
            return bytes;
        }

        public override bool CompanionExists(string nameWithExtension) => false;

        public override byte[]? TryReadCompanion(string nameWithExtension) => null;

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null) => null;
    }
}
