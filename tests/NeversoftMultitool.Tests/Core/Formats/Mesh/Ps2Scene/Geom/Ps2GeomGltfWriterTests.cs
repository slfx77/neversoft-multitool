using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2GeomGltfWriterTests
{
    [Fact]
    public void Write_DeduplicatesIdenticalGeomLeavesIntoSingleMeshBucket()
    {
        var leaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0x12345678,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeVertex(0, 0),
                MakeVertex(1, 0),
                MakeVertex(0, 1),
                MakeVertex(1, 1)
            ],
            DmaTex0 = 0,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x0A,
            DmaTest1 = 0
        };

        var geom = new Ps2GeomScene
        {
            Leaves = [leaf, leaf]
        };

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_dedup.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile);

            Assert.Equal(2, triangles);
            Assert.True(File.Exists(outputFile), "GLB file was not created");

            var model = ModelRoot.Load(outputFile);
            Assert.Single(model.LogicalMeshes);
            Assert.Single(model.LogicalNodes);

            var primitive = model.LogicalMeshes.Single().Primitives.Single();
            Assert.Equal(6, primitive.IndexAccessor!.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Write_WorldZoneSkipsHugeOriginCenteredHelperLeaves()
    {
        var helperLeaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeHelperVertex(-600, 0, 600, true),
                MakeHelperVertex(600, 0, 600, true),
                MakeHelperVertex(-600, 0, -600, false),
                MakeHelperVertex(600, 0, -600, false)
            ],
            DmaTex0 = 0x2,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x44,
            DmaTest1 = 0x5001B
        };

        var translatedLeaf = new Ps2GeomLeaf
        {
            Checksum = 0,
            TextureChecksum = 0,
            GroupChecksum = 0,
            Colour = 0,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeHelperVertex(10, 0, 40, true),
                MakeHelperVertex(90, 0, 40, true),
                MakeHelperVertex(10, 0, -40, false),
                MakeHelperVertex(90, 0, -40, false)
            ],
            DmaTex0 = 0x3,
            DmaClamp1 = 0,
            DmaAlpha1 = 0x44,
            DmaTest1 = 0x5001B
        };

        var baseLeaves = Enumerable.Range(0, 500)
            .Select(i => new Ps2GeomLeaf
            {
                Checksum = 0,
                TextureChecksum = 0x12345678,
                GroupChecksum = 0,
                Colour = 0,
                BoundingSphere = Vector4.Zero,
                Vertices =
                [
                    MakeVertex(i * 2, 0),
                    MakeVertex(i * 2 + 1, 0),
                    MakeVertex(i * 2, 1),
                    MakeVertex(i * 2 + 1, 1)
                ],
                DmaTex0 = 0x1,
                DmaClamp1 = 0,
                DmaAlpha1 = 0x0A,
                DmaTest1 = 0
            });

        var leaves = baseLeaves
            .Concat([helperLeaf, translatedLeaf])
            .ToArray();

        var geom = new Ps2GeomScene { Leaves = [.. leaves] };

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Ps2Geom_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "geom_worldzone_filter.glb");

            var triangles = Ps2GeomGltfWriter.Write(geom, outputFile);

            Assert.Equal(1002, triangles);
            Assert.True(File.Exists(outputFile), "GLB file was not created");

            var model = ModelRoot.Load(outputFile);
            Assert.True(model.LogicalMeshes.Count >= 2, "Expected translated helper geometry to remain");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private static Ps2Vertex MakeVertex(float x, float y)
    {
        return new Ps2Vertex(
            new Vector3(x, y, 0),
            Vector3.UnitZ,
            128, 128, 128, 128,
            0f, 0f,
            true,
            false,
            false,
            false);
    }

    private static Ps2Vertex MakeHelperVertex(float x, float y, float z, bool restart)
    {
        return new Ps2Vertex(
            new Vector3(x, y, z),
            Vector3.UnitY,
            128, 128, 128, 128,
            0f, 0f,
            false,
            false,
            true,
            restart);
    }
}