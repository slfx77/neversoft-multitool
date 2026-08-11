using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Opaque overlay discovery observes the billboard corners emitted by the
///     writer, while the separately calibrated transparent-layer detector
///     deliberately retains its legacy raw-candidate behavior.
/// </summary>
public sealed class PsxCoplanarOverlaySpriteCandidateTests
{
    [Fact]
    public void Find_UsesWriterExpandedSpriteCorners()
    {
        var file = CreateSpriteOverlayFile(semiTransparent: false);
        var baseFace = new PsxFaceInstanceKey(0, 0);
        var spriteFace = new PsxFaceInstanceKey(1, 0);

        var overlays = PsxCoplanarOverlayDetector.Find(file);
        var diagnostic = PsxCoplanarOverlayDetector.DiagnosePair(
            file, baseFace, spriteFace);

        Assert.Equal(spriteFace, Assert.Single(overlays));
        Assert.Equal(spriteFace, diagnostic.Overlay);
        Assert.Null(diagnostic.DeclineReason);
        Assert.Null(diagnostic.NotComparedReason);
        Assert.Equal(0f, diagnostic.AdmittedPlaneDistanceDelta);
        Assert.True(
            diagnostic.AdmittedTriangleSharedAreaFraction
            >= CoplanarOverlayGeometry.MinimumSharedAreaFraction);
    }

    [Fact]
    public void SemiTransparentLayerSteps_RetainLegacyRawSpriteCandidates()
    {
        var file = CreateSpriteOverlayFile(semiTransparent: true);

        var steps = PsxCoplanarOverlayDetector.FindSemiTransparentLayerSteps(file);

        // Expanding sprites in this separately calibrated path promotes 14
        // unverified two-step layers in Apocalypse grav_4. Keep that behavior
        // out of the opaque writer-geometry fix until it has its own oracle.
        Assert.Empty(steps);
    }

    private static PsxMeshFile CreateSpriteOverlayFile(bool semiTransparent)
    {
        var baseMesh = new PsxMesh
        {
            Vertices =
            [
                new PsxVertex { X = -20f, Y = -10f },
                new PsxVertex { X = 20f, Y = -10f },
                new PsxVertex { X = -20f, Y = 10f },
                new PsxVertex { X = 20f, Y = 10f }
            ],
            Normals = [new PsxNormal { Z = 1f }],
            Faces = [CreateFace(textureHash: 1, semiTransparent)]
        };

        var spriteMesh = new PsxMesh
        {
            Vertices =
            [
                new PsxVertex { Y = -10f },
                new PsxVertex { Y = -10f },
                new PsxVertex { Y = 10f },
                new PsxVertex { Y = 10f },
                CreateSpriteVertex(0, 2, 5f),
                CreateSpriteVertex(1, 2, -5f),
                CreateSpriteVertex(2, 0, -5f),
                CreateSpriteVertex(3, 0, 5f)
            ],
            Normals = [new PsxNormal { Z = 1f }],
            Faces = [CreateFace(textureHash: 2, semiTransparent, firstVertex: 4)],
            VertexCount = 8
        };

        return new PsxMeshFile
        {
            Version = 4,
            Objects =
            [
                new PsxMeshObject { MeshIndex = 0 },
                new PsxMeshObject { MeshIndex = 1 }
            ],
            Meshes = [baseMesh, spriteMesh],
            MeshNameHashes = new uint[2],
            TextureHashes = [1, 2],
            ScaleDivisor = 2.25f,
            TranslationDivisor = 2.25f
        };
    }

    private static PsxFace CreateFace(
        uint textureHash,
        bool semiTransparent,
        uint firstVertex = 0)
    {
        return new PsxFace
        {
            IsQuad = true,
            IsTextured = true,
            IsSemiTransparent = semiTransparent,
            TextureHash = textureHash,
            Index0 = firstVertex,
            Index1 = firstVertex + 1,
            Index2 = firstVertex + 2,
            Index3 = firstVertex + 3
        };
    }

    private static PsxVertex CreateSpriteVertex(
        int anchorIndex,
        int mateIndex,
        float halfWidth)
    {
        const float scaleDivisor = 2.25f;
        var rawX = (short)(anchorIndex * 8);
        var rawY = (short)(mateIndex * 8);
        return new PsxVertex
        {
            Type = PsxMeshSemantics.SpriteVertexTypeBit,
            RawX = rawX,
            RawY = rawY,
            X = rawX / scaleDivisor,
            Y = rawY / scaleDivisor,
            Z = halfWidth
        };
    }
}
