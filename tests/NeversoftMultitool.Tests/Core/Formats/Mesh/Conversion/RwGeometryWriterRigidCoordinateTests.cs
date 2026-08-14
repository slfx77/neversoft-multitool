using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class RwGeometryWriterRigidCoordinateTests
{
    private static readonly Matrix4x4 ZupToYup = new(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    [Fact]
    public void PopulateRwDff_RigidAtomicAppliesAxisConversionAfterAuthoredWorld()
    {
        var parentLocal = Matrix4x4.CreateTranslation(1, 2, 3);
        var childLocal = Matrix4x4.CreateTranslation(4, 5, 6);
        var document = PopulateRigidClump(
            [
                new RwFrame { LocalTransform = parentLocal, ParentIndex = -1, Flags = 0 },
                new RwFrame { LocalTransform = childLocal, ParentIndex = 0, Flags = 0 }
            ],
            frameIndex: 1);

        var node = Assert.Single(document.Nodes);
        var expected = childLocal * parentLocal * ZupToYup;

        Assert.Equal(expected, node.Transform);
        Assert.Equal(-Vector3.UnitZ, Vector3.TransformNormal(Vector3.UnitY, node.Transform));
        Assert.Equal(Vector3.UnitY, Vector3.TransformNormal(Vector3.UnitZ, node.Transform));
        Assert.Equal(new Vector3(5, 9, -7), node.Transform.Translation);
    }

    [Fact]
    public void PopulateRwDff_InvalidFrameFallsBackToConvertedIdentity()
    {
        var document = PopulateRigidClump(
            [new RwFrame { LocalTransform = Matrix4x4.CreateTranslation(1, 2, 3), ParentIndex = -1, Flags = 0 }],
            frameIndex: 7);

        var node = Assert.Single(document.Nodes);

        Assert.Equal(ZupToYup, node.Transform);
    }

    private static ModelDocument PopulateRigidClump(RwFrame[] frames, int frameIndex)
    {
        var document = new ModelDocument
        {
            Name = "rigid_rw",
            SourceKind = ModelSourceKind.RenderWareDff
        };
        var clump = new RwDffClump
        {
            Frames = frames,
            Geometries =
            [
                new RwGeometry
                {
                    Flags = 0,
                    Vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                    Normals = null,
                    UVs = null,
                    Colors = null,
                    Triangles = [new RwTriangle(0, 1, 2, 0)],
                    Materials =
                    [
                        new RwMaterial
                        {
                            R = 255,
                            G = 255,
                            B = 255,
                            A = 255,
                            TextureName = null,
                            MaskName = null
                        }
                    ],
                    BoundingSphere = Vector4.Zero
                }
            ],
            Atomics =
            [
                new RwAtomic
                {
                    FrameIndex = frameIndex,
                    GeometryIndex = 0,
                    Flags = 0
                }
            ]
        };

        RwGeometryWriter.PopulateRwDff(document, clump, null);
        return document;
    }
}
