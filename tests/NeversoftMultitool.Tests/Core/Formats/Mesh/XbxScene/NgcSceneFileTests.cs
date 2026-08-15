using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class NgcSceneFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";

    // ── IsNgcScene ──

    [CorpusFact]
    public void IsNgcScene_ValidFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "anl_pigeon.skin.ngc");
        Assert.SkipWhen(file is null, "anl_pigeon.skin.ngc not found");

        Assert.True(NgcSceneFile.IsNgcScene(File.ReadAllBytes(file)));
    }

    [Fact]
    public void IsNgcScene_EmptyData_ReturnsFalse()
    {
        Assert.False(NgcSceneFile.IsNgcScene([]));
    }

    [Theory]
    [InlineData(48)]
    [InlineData(63)]
    public void IsNgcScene_SentinelWithoutCompleteHeader_IsRejected(int length)
    {
        var data = CreateSyntheticScene(length);

        Assert.False(NgcSceneFile.IsNgcScene(data));
        Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));
    }

    [Fact]
    public void Parse_CompleteEmptyHeader_IsAccepted()
    {
        var data = CreateSyntheticScene(64);

        Assert.True(NgcSceneFile.IsNgcScene(data));
        var scene = NgcSceneFile.Parse(data);
        Assert.Empty(scene.Materials);
        Assert.Empty(scene.Sectors);
        Assert.Empty(scene.Links);
    }

    [Fact]
    public void Parse_CompleteEmptyObject_IsAccepted()
    {
        var data = CreateSyntheticScene(128);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x10), 1);

        var scene = NgcSceneFile.Parse(data);

        var sector = Assert.Single(scene.Sectors);
        Assert.Empty(sector.Meshes);
        Assert.Empty(scene.Links);
    }

    [Fact]
    public void Parse_PositionCountWhoseSerializedBytesWrapInt32_IsRejected()
    {
        var data = CreateSyntheticScene(64);
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x40000000);

        var error = Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));

        Assert.StartsWith("NGC scene fixed pool arrays overruns its containing region", error.Message);
    }

    [Fact]
    public void Parse_PoolSizeAboveSignedRange_IsRejected()
    {
        var data = CreateSyntheticScene(64);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C), uint.MaxValue);

        var error = Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));

        Assert.Equal("NGC scene pool size 4294967295 exceeds the 0 remaining bytes", error.Message);
    }

    [Fact]
    public void Parse_BlendDisplayListCountWithoutTable_IsRejected()
    {
        var data = CreateSyntheticScene(64);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x18), 1);

        var error = Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));

        Assert.StartsWith("NGC scene material display-list tables overruns its containing region", error.Message);
    }

    [Fact]
    public void Parse_BlendDisplayListPayloadSumAboveRemainingBytes_IsRejected()
    {
        var data = CreateSyntheticScene(96);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x18), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(64), uint.MaxValue);

        var error = Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));

        Assert.StartsWith("NGC scene material display-list data overruns its containing region", error.Message);
    }

    [Theory]
    [InlineData(-1, "NGC scene VC-wibble 0 has negative frame count -1")]
    [InlineData(int.MaxValue, "NGC scene VC-wibble 0 overruns its containing region")]
    public void Parse_InvalidVcWibbleFrameCount_IsRejected(int frameCount, string expectedMessage)
    {
        var data = CreateSyntheticScene(72);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C), 8);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x1A), 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(64), frameCount);

        var error = Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));

        Assert.StartsWith(expectedMessage, error.Message);
    }

    [Fact]
    public void Parse_ObjectSkinSizeAboveSignedRange_IsRejected()
    {
        var data = CreateSyntheticScene(128);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x10), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(68), uint.MaxValue);

        var error = Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));

        Assert.StartsWith("NGC scene object 0 skin data overruns its containing region", error.Message);
    }

    [Fact]
    public void Parse_EachInterleavedObjectNeedsItsOwnCompleteHeader()
    {
        var data = CreateSyntheticScene(192);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x10), 2);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(68), 1);

        var error = Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));

        Assert.StartsWith("NGC scene object 1 header overruns its containing region", error.Message);
    }

    [Fact]
    public void Parse_SkinListCannotBorrowBytesPastDeclaredSkinRegion()
    {
        var data = CreateSyntheticScene(148);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x10), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(68), 8);
        data[76] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(128), 1);

        var error = Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));

        Assert.StartsWith("NGC scene object 0 single-skin list 0 vertices overruns its containing region", error.Message);
    }

    [Fact]
    public void Parse_MeshDisplayListSizeAboveSignedRange_IsRejected()
    {
        var data = CreateSyntheticScene(192);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x10), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(64), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(128), uint.MaxValue);

        var error = Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));

        Assert.StartsWith("NGC scene object 0 mesh 0 display list overruns its containing region", error.Message);
    }

    [Fact]
    public void Parse_DisplayListCommandCannotBorrowBytesPastDeclaredPayload()
    {
        var data = CreateSyntheticScene(198);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x10), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(64), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(128), 1);
        data[192] = 0x08;

        var error = Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));

        Assert.StartsWith("NGC scene object 0 mesh 0 display list CP command overruns its containing region", error.Message);
    }

    [Fact]
    public void IsNgcScene_XboxSceneData_ReturnsFalse()
    {
        // Xbox version triple (1,1,1) header, no 0xAAFFEEFF sentinel
        var data = new byte[64];
        BitConverter.GetBytes(1u).CopyTo(data, 0);
        BitConverter.GetBytes(1u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);
        Assert.False(NgcSceneFile.IsNgcScene(data));
    }

    /// <summary>
    ///     26 of the 1,612 THAW GC .mdl.ngc files declare a pool that ends past
    ///     the physical file by 4-16 bytes of the region's own 32-byte
    ///     alignment, and every one of them declares zero objects — so nothing
    ///     ever addresses the overrun. Rejecting them refused real shipped
    ///     scenes.
    /// </summary>
    [Theory]
    [InlineData(4u)]
    [InlineData(16u)]
    [InlineData(31u)]
    public void Parse_ZeroObjectSceneWhoseDeclaredPoolOverrunsItsAlignment_IsAccepted(uint overrun)
    {
        var data = CreateSyntheticScene(64);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C), overrun);

        var scene = NgcSceneFile.Parse(data);

        Assert.Empty(scene.Sectors);
    }

    [Fact]
    public void Parse_ZeroObjectSceneOverrunningAWholeAlignmentUnit_IsStillRejected()
    {
        var data = CreateSyntheticScene(64);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C), 32);

        var error = Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));

        Assert.StartsWith("NGC scene pool size 32 exceeds", error.Message);
    }

    [Fact]
    public void Parse_SceneWithObjectsWhoseDeclaredPoolOverruns_IsRejected()
    {
        // The slack only exists because nothing reads past the pool. Declare an
        // object and the overrun becomes a real out-of-bounds read.
        var data = CreateSyntheticScene(64);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C), 16);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x10), 1);

        var error = Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));

        Assert.StartsWith("NGC scene object headers overruns its containing region", error.Message);
    }

    private static byte[] CreateSyntheticScene(int length)
    {
        var data = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x2C), 0xAAFFEEFF);
        return data;
    }

    // ── Parse known files (values validated against PC/PS2 Rosetta pairs) ──

    [CorpusFact]
    public void Parse_Pigeon_MatchesPcRosettaPair()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "anl_pigeon.skin.ngc");
        Assert.SkipWhen(file is null, "anl_pigeon.skin.ngc not found");

        var scene = NgcSceneFile.Parse(File.ReadAllBytes(file));

        var sector = Assert.Single(scene.Sectors);
        var mesh = Assert.Single(sector.Meshes);

        // 29 skin positions expand to 46 vertices at UV seams and yield 45 real
        // triangles — both exact matches with the PC pair (anl_pigeon.skin.wpc).
        Assert.Equal(46, mesh.Vertices.Length);
        Assert.Equal(29, mesh.Vertices.Select(v => v.Position).Distinct().Count());
        Assert.Equal(45, mesh.FaceIndices.Length / 3);
        Assert.True(mesh.IsPreTriangulated);

        // Wingtip at (±25.3125, 14.3125, 0.875): skin positions are s16/32
        Assert.Contains(mesh.Vertices, v =>
            Math.Abs(v.Position.X - -25.3125f) < 0.001f &&
            Math.Abs(v.Position.Y - 14.3125f) < 0.001f);

        // Bounding sphere from the object header is in model units
        Assert.Equal(25.3125f, sector.BsphereRadius, 3);

        // Skinned: all vertices carry bone weights that sum to 1
        Assert.All(mesh.Vertices, v =>
        {
            Assert.True(v.HasSkinData);
            var sum = v.BoneWeight0 + v.BoneWeight1 + v.BoneWeight2;
            Assert.True(Math.Abs(sum - 1f) < 0.01f, $"weight sum {sum}");
        });
    }

    [CorpusFact]
    public void Parse_Pigeon_MaterialReferencesTextureByIndex()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "anl_pigeon.skin.ngc");
        Assert.SkipWhen(file is null, "anl_pigeon.skin.ngc not found");

        var scene = NgcSceneFile.Parse(File.ReadAllBytes(file));

        var material = Assert.Single(scene.Materials);
        Assert.Equal(0x86402E3Du, material.Checksum);
        var pass = Assert.Single(material.Passes);
        // Texture INDEX 0 into the companion .tex.ngc, stored as index+1
        Assert.Equal(1u, pass.TextureChecksum);
    }

    // ── Batch parse ──

    [CorpusFact]
    public void BatchParse_AllSkinFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.skin.ngc").ToArray();
        Assert.SkipWhen(files.Length == 0, "No .skin.ngc files found");

        var failures = new List<string>();
        var totalTris = 0;

        foreach (var f in files)
        {
            try
            {
                var scene = NgcSceneFile.Parse(File.ReadAllBytes(f));
                totalTris += scene.TotalTriangles;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} SKIN files failed:\n  " +
            string.Join("\n  ", failures.Take(20)));
        Assert.True(totalTris > 0, "Expected total triangles > 0");
    }

    [CorpusFact]
    public void BatchParse_AllMdlFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.mdl.ngc").ToArray();
        Assert.SkipWhen(files.Length == 0, "No .mdl.ngc files found");

        var failures = new List<string>();
        var totalTris = 0;

        foreach (var f in files)
        {
            try
            {
                var scene = NgcSceneFile.Parse(File.ReadAllBytes(f));
                totalTris += scene.TotalTriangles;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} MDL files failed:\n  " +
            string.Join("\n  ", failures.Take(20)));
        Assert.True(totalTris > 0, "Expected total triangles > 0");
    }
}
