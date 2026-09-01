using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins Visual Impact's separate Downhill Jam GBA rider format.  Model 19 is
///     also the live gameplay oracle: the frame-4800 RAM capture retained its
///     vertex and face ROM pointers at 0x08EB7A9C and 0x08EB7EEC.
/// </summary>
public sealed class GbaDhjModelTests(TestPaths paths)
{
    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)",
        "Tony Hawk's Downhill Jam (USA).gba");

    [Fact]
    public void FindsAllTwentyFourRiderVariantsByClosure()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);

        var models = GbaDhjModel.FindModels(rom);
        Assert.Equal(24, models.Count);
        Assert.Equal(Enumerable.Range(0, 24), models.Select(static model => model.Index));
        Assert.Equal(
            [125, 119, 128, 141, 121, 125, 135, 128, 124, 142, 141, 145,
                141, 145, 134, 138, 139, 143, 128, 138, 132, 142, 146, 148],
            models.Select(static model => model.VertexCount));
        Assert.Equal(
            [102, 104, 100, 104, 102, 102, 100, 104, 100, 124, 104, 104,
                104, 104, 112, 112, 108, 108, 110, 110, 112, 112, 116, 116],
            models.Select(static model => model.FaceCount));

        Assert.Equal(0xEABA20, models[0].HeaderOffset);
        Assert.Equal(0xEB7A18, models[19].HeaderOffset);
        Assert.Equal(0xEBA430, models[^1].HeaderOffset);
        Assert.All(models, model =>
        {
            Assert.Equal(GbaDhjModel.GroupCount, model.VertexCounts.Length);
            Assert.Equal(GbaDhjModel.GroupCount, model.FaceCounts.Length);
            Assert.Equal(model.HeaderOffset + GbaDhjModel.HeaderSize, model.VertexDataOffset);
            Assert.Equal(model.VertexDataOffset + model.VertexCount * GbaDhjModel.VertexRecordSize,
                model.FaceDataOffset);
        });
    }

    [Fact]
    public void LiveGameplayModelClosesItsVertexAndTriangleBanksExactly()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaDhjModel.FindModels(rom)[19];

        Assert.Equal(0xEB7A9C, model.VertexDataOffset);
        Assert.Equal(0xEB7EEC, model.FaceDataOffset);
        Assert.Equal(0xEB80A8, model.EndOffset);
        Assert.Equal([6, 9, 9, 7, 8, 10, 8, 10, 8, 8, 4, 4, 47], model.VertexCounts);
        Assert.Equal([4, 7, 6, 7, 7, 6, 5, 12, 4, 4, 6, 6, 36], model.FaceCounts);

        var vertices = GbaDhjModel.ReadVertices(rom, model);
        var faces = GbaDhjModel.ReadFaces(rom, model);
        Assert.Equal(new GbaDhjModel.Vertex(51, 10, 1, 0x1B1F), vertices[0]);
        Assert.Equal(new GbaDhjModel.Face(0, 2, 1, 0, 0x20), faces[0]);
        Assert.Equal(new GbaDhjModel.Face(12, 0x88, 0x59, 0x5A, 0x87), faces[^1]);
        Assert.Equal(-59, vertices.Min(static vertex => vertex.X));
        Assert.Equal(67, vertices.Max(static vertex => vertex.X));
        Assert.Equal(-23, vertices.Min(static vertex => vertex.Y));
        Assert.Equal(21, vertices.Max(static vertex => vertex.Y));
        Assert.Equal(-62, vertices.Min(static vertex => vertex.Z));
        Assert.Equal(70, vertices.Max(static vertex => vertex.Z));
        Assert.All(faces, face =>
        {
            Assert.InRange(face.V0, 0, vertices.Length - 1);
            Assert.InRange(face.V1, 0, vertices.Length - 1);
            Assert.InRange(face.V2, 0, vertices.Length - 1);
        });

        var exactGeometry = rom.AsSpan(model.VertexDataOffset, model.EndOffset - model.VertexDataOffset);
        Assert.Equal(
            "4316D4D75169EDAEFADA8630F6C591114E357066B29C913BF7528F651AE0F553",
            Convert.ToHexString(SHA256.HashData(exactGeometry)));
    }

    [Fact]
    public void FindsAndDecodesTheRuntimeVerifiedPoseClip()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);

        var library = Assert.Single(GbaDhjModel.FindPoseLibraries(rom));
        Assert.Equal(0xE71808, library.HeaderOffset);
        Assert.Equal(94, library.ClipCount);
        Assert.Equal(0xEA4520, library.ClipOffsets[79]);
        Assert.Equal(12, library.ClipFrameCounts[79]);
        Assert.Equal(0xEA99FC, library.ClipOffsets[90]);
        Assert.Equal(26, library.ClipFrameCounts[90]);
        Assert.Equal(-1, library.ClipFrameCounts[^1]);

        var pose = GbaDhjModel.ReadPoseFrame(rom, library, 79, 0);
        Assert.Equal(0xEA4520, pose.Offset);
        Assert.Equal(0, pose.Header);
        Assert.Equal(GbaDhjModel.GroupCount, pose.Parts.Length);
        Assert.Equal(new GbaDhjModel.PartPose(0, 0, 9, 0, 1, 192), pose.Parts[0]);
        Assert.Equal(new GbaDhjModel.PartPose(-10, -18, 29, 11, 1, 37), pose.Parts[1]);
        Assert.Equal(new GbaDhjModel.PartPose(-8, -1, 145, 15, 250, 42), pose.Parts[12]);
    }

    [CorpusFact]
    public void AssemblesTheLiveModelIntoAnAnatomicallyOrderedUprightGlb()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaDhjModel.FindModels(rom)[19];
        var library = Assert.Single(GbaDhjModel.FindPoseLibraries(rom));
        var pose = GbaDhjModel.ReadPoseFrame(rom, library, 79, 0);

        var sourceVertices = GbaDhjModel.ReadVertices(rom, model);
        var posedVertices = GbaDhjModelGeometryWriter.ApplyPose(
            sourceVertices, model.VertexCounts, pose);
        Assert.InRange(posedVertices[0].X, 9.999f, 10.001f);
        Assert.InRange(posedVertices[0].Y, -51.011f, -51.008f);
        Assert.InRange(posedVertices[0].Z, -8.750f, -8.747f);

        // This is a structural silhouette oracle, not an eyeballed screenshot:
        // both leg chains and the torso rise monotonically away from the board.
        // Omitting the 13 pose records collapses all these rigid-part origins
        // around zero and produces the formerly exported starburst.
        var heightCentres = GroupCentres(posedVertices, model.VertexCounts)
            .Select(static centre => -centre.Z)
            .ToArray();
        Assert.True(heightCentres[0] < Math.Min(heightCentres[1], heightCentres[2]));
        Assert.True(heightCentres[1] < heightCentres[4]);
        Assert.True(heightCentres[2] < heightCentres[3]);
        Assert.True(heightCentres[4] < heightCentres[6]);
        Assert.True(heightCentres[3] < heightCentres[5]);
        Assert.True(Math.Max(heightCentres[5], heightCentres[6]) < heightCentres[7]);
        Assert.True(heightCentres[7] < heightCentres[12]);

        var document = GbaDhjModelGeometryWriter.Build(rom, model, pose, "rider_19");
        // The two records that collapsed when all raw part-local vertices were
        // overlaid become valid connector triangles after pose assembly.
        Assert.Equal(110, document.TriangleCount);
        Assert.Equal(13, document.Materials.Count);
        Assert.Single(document.Meshes);
        Assert.Equal(13, document.Meshes[0].Primitives.Count);
        Assert.All(document.Materials, material => Assert.EndsWith("_debug", material.Name));

        var allPositions = document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .Select(static vertex => vertex.Position)
            .ToArray();
        var boundsHeight = allPositions.Max(static position => position.Y)
                           - allPositions.Min(static position => position.Y);
        Assert.InRange(boundsHeight, 402f, 405f);

        var (glb, triangles) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.NotNull(glb);
        Assert.True(glb.Length > 1024);
        Assert.Equal(110, triangles);
    }

    private static System.Numerics.Vector3[] GroupCentres(
        IReadOnlyList<System.Numerics.Vector3> vertices,
        IReadOnlyList<ushort> counts)
    {
        var result = new System.Numerics.Vector3[counts.Count];
        var first = 0;
        for (var group = 0; group < counts.Count; group++)
        {
            for (var i = 0; i < counts[group]; i++)
                result[group] += vertices[first + i];
            result[group] /= counts[group];
            first += counts[group];
        }

        return result;
    }

    [Fact]
    public void RejectsLookalikesAndOtherGbaTonyHawkEngines()
    {
        var fake = new byte[0x200];
        "BXSE"u8.CopyTo(fake.AsSpan(0xAC));
        fake[0x100] = 0x80;
        fake[0x102] = GbaDhjModel.GroupCount;
        Assert.Empty(GbaDhjModel.FindModels(fake));

        var thps2Path = paths.FindSampleFile(
            "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)",
            "Tony Hawk's Pro Skater 2 (USA, Europe).gba");
        Assert.SkipWhen(thps2Path == null, "THPS2 GBA ROM sample not available");
        Assert.Empty(GbaDhjModel.FindModels(File.ReadAllBytes(thps2Path!)));
    }
}
