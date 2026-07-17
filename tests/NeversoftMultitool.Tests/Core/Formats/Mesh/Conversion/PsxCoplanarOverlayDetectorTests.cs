using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxCoplanarOverlayDetectorTests(TestPaths paths)
{
    [Fact]
    public void Find_MarksSmallerOpaqueFaceNestedOnDifferentMaterial()
    {
        var file = CreateFile(CreateQuad(10f, 0f, 1), CreateQuad(2f, 0f, 2));

        var overlays = PsxCoplanarOverlayDetector.Find(file);

        Assert.DoesNotContain(new PsxFaceInstanceKey(0, 0), overlays);
        Assert.Contains(new PsxFaceInstanceKey(1, 0), overlays);
    }

    [Fact]
    public void Find_DoesNotMarkNearbyOrEqualSizedGeometry()
    {
        var nearby = PsxCoplanarOverlayDetector.Find(
            CreateFile(CreateQuad(10f, 0f, 1), CreateQuad(2f, 0.1f, 2)));
        var equal = PsxCoplanarOverlayDetector.Find(
            CreateFile(CreateQuad(10f, 0f, 1), CreateQuad(10f, 0f, 2)));

        Assert.Empty(nearby);
        Assert.Empty(equal);
    }

    [Fact]
    public void SpiderManL2A1_FindsOpaqueRoofOverlayFaces()
    {
        var path = paths.SampleBuildsDir is null
            ? string.Empty
            : Path.Combine(
                paths.SampleBuildsDir,
                "Spider-Man (2000-9-1, PSX - Final)",
                "CD",
                "l2a1_g.psx");
        Assert.SkipWhen(!File.Exists(path), "Spider-Man PSX final sample not available");

        var file = PsxMeshFile.Parse(path);
        Assert.NotNull(file);
        var overlays = PsxCoplanarOverlayDetector.Find(file!);

        Assert.Equal(75, overlays.Count);
        Assert.Contains(new PsxFaceInstanceKey(47, 0), overlays);
        Assert.Contains(new PsxFaceInstanceKey(47, 5), overlays);
        Assert.All(
            overlays.Where(key => key.ObjectIndex == 47),
            key => Assert.False(file!.Meshes[file.Objects[key.ObjectIndex].MeshIndex]
                .Faces[key.FaceIndex].IsSemiTransparent));
    }

    private static PsxMeshFile CreateFile(params PsxMesh[] meshes)
    {
        return new PsxMeshFile
        {
            Version = 4,
            Objects = meshes.Select((_, index) => new PsxMeshObject { MeshIndex = (ushort)index }).ToList(),
            Meshes = meshes.ToList(),
            MeshNameHashes = new uint[meshes.Length],
            TextureHashes = [1, 2],
            ScaleDivisor = 2.25f,
            TranslationDivisor = 2.25f
        };
    }

    private static PsxMesh CreateQuad(float size, float y, uint textureHash)
    {
        var half = size * 0.5f;
        return new PsxMesh
        {
            Vertices =
            [
                new PsxVertex { X = -half, Y = y, Z = -half },
                new PsxVertex { X = half, Y = y, Z = -half },
                new PsxVertex { X = -half, Y = y, Z = half },
                new PsxVertex { X = half, Y = y, Z = half }
            ],
            Normals = [new PsxNormal { Y = 1f }],
            Faces =
            [
                new PsxFace
                {
                    IsQuad = true,
                    IsTextured = true,
                    TextureHash = textureHash,
                    Index0 = 0,
                    Index1 = 1,
                    Index2 = 2,
                    Index3 = 3
                }
            ]
        };
    }
}
