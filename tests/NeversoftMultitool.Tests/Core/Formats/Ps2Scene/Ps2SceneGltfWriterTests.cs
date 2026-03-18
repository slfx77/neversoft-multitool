using System.Numerics;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;
using ParsedPs2Scene = NeversoftMultitool.Core.Formats.Ps2Scene.Ps2Scene;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class Ps2SceneGltfWriterTests
{
    [Fact]
    public void Write_RigidMesh_UsesOddOutputSlotParity()
    {
        var scene = new ParsedPs2Scene
        {
            MaterialVersion = 0,
            MeshVersion = 0,
            VertexVersion = 0,
            Materials =
            [
                new Ps2Material
                {
                    Checksum = 0x12345678
                }
            ],
            MeshGroups =
            [
                new Ps2MeshGroup
                {
                    Checksum = 0x11111111,
                    Meshes =
                    [
                        new Ps2Mesh
                        {
                            Checksum = 0x12345678,
                            MaterialChecksum = 0x12345678,
                            StartsOnOddOutputSlot = true,
                            Vertices =
                            [
                                MakeVertex(0, 0),
                                MakeVertex(1, 0),
                                MakeVertex(0, 1),
                                MakeVertex(1, 1)
                            ]
                        }
                    ]
                }
            ]
        };

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_Ps2Parity_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "odd_parity.glb");

            var triangles = Ps2SceneGltfWriter.Write(scene, outputFile);

            Assert.Equal(2, triangles);
            Assert.True(File.Exists(outputFile), "GLB file was not created");

            var model = ModelRoot.Load(outputFile);
            var primitive = model.LogicalMeshes.Single().Primitives.Single();
            var positions = primitive.GetVertexAccessor("POSITION").AsVector3Array().ToArray();
            var indices = primitive.IndexAccessor!.AsIndicesArray().ToArray();

            Assert.True(indices.Length >= 3, "Expected at least one triangle");

            var a = positions[(int)indices[0]];
            var b = positions[(int)indices[1]];
            var c = positions[(int)indices[2]];
            var normalZ = Vector3.Cross(b - a, c - a).Z;

            Assert.True(normalZ < 0, $"Expected odd-parity first triangle winding, got normal Z {normalZ}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void AddTriangleStrip_ResetOnRestart_SuppressesCrossStripTriangles()
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>("restart_test");
        var prim = mesh.UsePrimitive(new MaterialBuilder("mat"));
        var verts = new[]
        {
            MakeVertex(0, 0, true),
            MakeVertex(1, 0),
            MakeVertex(0, 1),
            MakeVertex(100, 100, true),
            MakeVertex(101, 100),
            MakeVertex(100, 101)
        };

        var triangles = Ps2SceneGltfWriter.AddTriangleStrip(prim, verts, resetOnRestart: true);

        Assert.Equal(2, triangles);
    }

    private static Ps2Vertex MakeVertex(float x, float y, bool isRestart = false)
    {
        return new Ps2Vertex(
            new Vector3(x, y, 0),
            Vector3.UnitZ,
            128, 128, 128, 128,
            0f, 0f,
            true,
            false,
            false,
            isRestart);
    }
}