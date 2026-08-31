using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.CLI;

public sealed class PsxAnimDumpCommandTests
{
    [Fact]
    public void Execute_MissingAndInvalidBracketedInputs_ReturnFailure()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing].psx");
        var invalid = Path.Combine(temp.Path, "[bad].psx");
        File.WriteAllBytes(invalid, "NOPE"u8.ToArray());

        Assert.Equal(1, Execute(missing));
        Assert.Equal(1, Execute(invalid));
    }

    [Fact]
    public void Execute_PreCancelled_Propagates()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "cancelled.psx");
        File.WriteAllBytes(input, "NOPE"u8.ToArray());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Execute(input, cancellation.Token));
    }

    [Fact]
    public void Walker_BracketedPshBoneName_RendersLiterally()
    {
        var psh = PshFile.Parse(Encoding.UTF8.GetBytes(
            "#define TESTPART_[bone] 0\n"));
        Assert.NotNull(psh);
        var data = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 1);

        var result = PsxAnimDumpWalker.TryWalkHierarchy(
            data,
            startOffset: 0,
            psh,
            verbose: true);

        Assert.NotNull(result);
        Assert.Equal(1, result.NumStreams);
    }

    [Fact]
    public void Execute_OutOfRangeAnimationIndex_ReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "one-clip.psx");
        File.WriteAllBytes(input, BuildMinimalDirectMatrixPsx());

        Assert.Equal(0, Execute(input, animIndex: 0));
        Assert.Equal(1, Execute(input, animIndex: 1));
    }

    private static int Execute(
        string input,
        CancellationToken? cancellationToken = null,
        int animIndex = 0)
    {
        return PsxAnimDumpCommand.Execute(
            input,
            hexBytes: 256,
            animIndex,
            boneIndex: 0,
            rankBoneIndex: null,
            rankTop: 12,
            verbose: true,
            cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private static byte[] BuildMinimalDirectMatrixPsx()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(0x00020004u);
        writer.Write(56u);
        writer.Write(1u);

        writer.Write(0u);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write(0u);
        writer.Write(0u);

        writer.Write(1u);
        writer.Write(124u);

        writer.Write(0x52454948u); // HIER
        writer.Write(4u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);

        writer.Write(PsxMeshFile.HierChunkV1Tag);
        writer.Write(0x24u);
        writer.Write(1u);
        writer.Write(0x0Cu);
        writer.Write((ushort)1);
        writer.Write((ushort)0);

        Span<short> matrix =
        [
            4096, 0, 0,
            0, 4096, 0,
            0, 0, 4096
        ];
        foreach (var value in matrix)
            writer.Write(value);
        writer.Write((short)36);
        writer.Write((short)0);
        writer.Write((short)0);

        writer.Write(uint.MaxValue);
        writer.Write(0x12345678u);
        writer.Write(0u);

        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(new byte[12]);
        writer.Write(short.MaxValue);
        writer.Write(ushort.MaxValue);

        // The command requires a post-mesh region before entering its
        // hierarchy diagnostic layers.
        writer.Write(new byte[16]);
        return stream.ToArray();
    }

    [Theory]
    [InlineData(new byte[] { 0x53, 0x48, 0x41, 0x44, 0x4F, 0x57, 0, 0 }, "SHADOW")]
    [InlineData(new byte[] { 0x46, 0x4F, 0x4E, 0x54, 0x53, 0x4D, 0x4C, 0x4C }, "FONTSMLL")]
    [InlineData(new byte[] { 0x53, 0x70, 0, 0, 0, 0, 0, 0 }, "Sp")]
    public void ReadPackedName_DecodesTheEightByteGroupName(byte[] raw, string expected)
    {
        Assert.Equal(expected, PsxAnimDumpWalker.ReadPackedName(raw, 0));
    }

    [Theory]
    // Non-printable bytes, and a name with content after its terminator: neither is a name, so
    // both must fall back to the raw words rather than silently decoding to something short.
    [InlineData(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 })]
    [InlineData(new byte[] { 0x41, 0x42, 0, 0x43, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void ReadPackedName_FallsBackToRawWordsWhenTheBytesAreNotAName(byte[] raw)
    {
        Assert.StartsWith("0x", PsxAnimDumpWalker.ReadPackedName(raw, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void SpriteAnimChunk_IsFoundInAMeshlessFileAndCarriesNames()
    {
        // The 0x45 table is the one place the PSX format stores animation names, and it ships in
        // mesh-less files, so the post-mesh walk can never reach it. Synthetic equivalent of
        // bits.psx: header, metaTop, one 0x45 chunk of two named groups.
        var stream = new MemoryStream();
        var writer = new BinaryWriter(stream);
        writer.Write((ushort)4);       // version
        writer.Write((ushort)2);       // magic
        writer.Write(16);              // metaTop
        writer.Write(new byte[8]);     // padding to 0x10
        writer.Write(PsxMeshFile.SpriteAnimChunkTag);
        writer.Write(4 + 12 + 8 + 12); // chunk size
        writer.Write(2);               // groupCount
        writer.Write(Encoding.ASCII.GetBytes("SHADOW\0\0"));
        writer.Write(1);               // animCount
        writer.Write(0L);              // one 8-byte anim entry
        writer.Write(Encoding.ASCII.GetBytes("SMOKE\0\0\0"));
        writer.Write(0);               // animCount
        writer.Flush();
        var data = stream.ToArray();

        Assert.True(PsxMeshFile.TryGetChunk(
            data, PsxMeshFile.SpriteAnimChunkTag, out var offset, out var size));
        Assert.Equal(0x18, offset);
        Assert.Equal(36, size);
        Assert.Equal("SHADOW", PsxAnimDumpWalker.ReadPackedName(data, offset + 4));
        Assert.Equal("SMOKE", PsxAnimDumpWalker.ReadPackedName(data, offset + 4 + 12 + 8));
    }

    [CorpusFact]
    public void EveryCorpusSpriteAnimTableDecodesToPrintableNames()
    {
        var paths = new TestPaths();
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = 0;
        var groups = 0;
        var anims = 0;
        foreach (var file in Directory
                     .EnumerateFiles(paths.SampleBuildsDir!, "*", SearchOption.AllDirectories)
                     .Where(static f => Path.GetExtension(f)
                         .Equals(".psx", StringComparison.OrdinalIgnoreCase)))
        {
            var data = File.ReadAllBytes(file);
            if (!PsxMeshFile.TryGetChunk(
                    data, PsxMeshFile.SpriteAnimChunkTag, out var offset, out _))
                continue;

            files++;
            var count = BitConverter.ToUInt32(data, offset);
            Assert.InRange(count, 1u, 64u);
            var pos = offset + 4;
            for (var g = 0; g < count; g++)
            {
                var name = PsxAnimDumpWalker.ReadPackedName(data, pos);
                // A raw-word fallback here would mean the grammar is mis-framed.
                Assert.DoesNotContain("0x", name, StringComparison.Ordinal);
                groups++;
                var animCount = (int)BitConverter.ToUInt32(data, pos + 8);
                anims += animCount;
                pos += 12 + animCount * 8;
            }
        }

        // Named sprite/effect tables across Apocalypse, THPS and the Spider-Man protos —
        // e.g. FONTSMLL, SHADOW, SMOKE, ribbon, Buttons, WebKnot, RhinoBol, Compass, Slime.
        Assert.Equal(139, files);
        Assert.Equal(482, groups);
        Assert.Equal(2_746, anims);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-psx-anim-dump-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
