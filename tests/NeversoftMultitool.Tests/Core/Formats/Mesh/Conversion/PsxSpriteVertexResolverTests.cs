using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     PSX sprite vertices (type bit4) store anchor/axis-mate byte offsets and
///     a signed half-width instead of a position; the resolver bakes the
///     engine's axial-billboard expansion (corner = A + k·Z·L̂, k = 1.6 for
///     vertical axes) into static glTF positions. Corpus census:
///     tools/diagnostics/psx_sprite_vertex_census.py.
/// </summary>
public sealed class PsxSpriteVertexResolverTests(TestPaths paths)
{
    private const float VerticalFactor = PsxSpriteVertexResolver.VerticalAxisWidthFactor;

    [Fact]
    public void TryCreate_ReturnsNullForMeshesWithoutSpriteVertices()
    {
        var mesh = new PsxMesh
        {
            Vertices = [new PsxVertex { X = 1f, Y = 2f, Z = 3f }],
            Normals = [],
            Faces = []
        };

        Assert.Null(PsxSpriteVertexResolver.TryCreate(mesh));
    }

    [Fact]
    public void Resolve_ExpandsSpriteCornersAboutTheAnchorAxis()
    {
        // Corpus authoring pattern (skph mesh 31): the two corners at the far
        // axis end reference the axis REVERSED, so the cross-product sign flip
        // cancels and symmetric half-widths produce a rectangle of width
        // 2·1.6·|Z| centred on the axis.
        var mesh = CreateSpriteQuadMesh(axisHalfExtent: 40f, halfWidth: 5f);
        var resolver = PsxSpriteVertexResolver.TryCreate(mesh);
        Assert.NotNull(resolver);

        // PSX (0,-40,0) → glTF (0,+40,0): the axis top.
        AssertResolves(resolver, 4, new Vector3(-VerticalFactor * 5f, 40f, 0f));
        AssertResolves(resolver, 5, new Vector3(VerticalFactor * 5f, 40f, 0f));
        AssertResolves(resolver, 6, new Vector3(-VerticalFactor * 5f, -40f, 0f));
        AssertResolves(resolver, 7, new Vector3(VerticalFactor * 5f, -40f, 0f));

        // Anchor vertices are ordinary positions — not sprite corners.
        Assert.False(resolver.TryResolvePosition(0, out _));
        Assert.False(resolver.TryResolvePosition(2, out _));
    }

    [Fact]
    public void Resolve_PublishesTheSharedRotationAxisMetadata()
    {
        var resolver = PsxSpriteVertexResolver.TryCreate(
            CreateSpriteQuadMesh(axisHalfExtent: 40f, halfWidth: 5f));
        Assert.NotNull(resolver);

        var billboard = resolver.BillboardMetadata;
        Assert.NotNull(billboard);
        // Anchor midpoint of the (0,±40,0) endpoints; direction canonicalized
        // to dominant-positive (+Y) regardless of per-corner axis reversal.
        Assert.Equal(0f, billboard.AnchorX, 4);
        Assert.Equal(0f, billboard.AnchorY, 4);
        Assert.Equal(0f, billboard.AnchorZ, 4);
        Assert.Equal(0f, billboard.AxisX, 4);
        Assert.Equal(1f, billboard.AxisY, 4);
        Assert.Equal(0f, billboard.AxisZ, 4);
    }

    [Fact]
    public void Populate_EmitsOnlyResolvedCornersAndTagsThePrimitives()
    {
        var mesh = CreateSpriteQuadMesh(axisHalfExtent: 40f, halfWidth: 5f);
        var file = CreateFile(mesh);
        var document = new ModelDocument { Name = "sprite" };
        PsxGeometryWriter.PopulatePsx(document, file, null);

        var modelMesh = Assert.Single(document.Meshes);
        var primitive = Assert.Single(modelMesh.Primitives);
        Assert.Equal(2, primitive.TriangleCount);

        // Every emitted corner is one of the four resolved billboard corners —
        // the anchor-only vertices (referenced by no face) emit no geometry
        // and the raw sprite fields never leak through as positions.
        var expected = new[]
        {
            new Vector3(-VerticalFactor * 5f, 40f, 0f),
            new Vector3(VerticalFactor * 5f, 40f, 0f),
            new Vector3(-VerticalFactor * 5f, -40f, 0f),
            new Vector3(VerticalFactor * 5f, -40f, 0f)
        };
        Assert.All(primitive.Vertices, vertex =>
            Assert.Contains(expected, corner =>
                Vector3.Distance(corner, vertex.Position) < 1e-4f));
        Assert.Equal(expected.Length, primitive.Vertices
            .Select(static vertex => vertex.Position)
            .Distinct()
            .Count());

        var billboard = Assert.Single(
            primitive.NativeMetadata.OfType<PsxAxialBillboardMetadata>());
        Assert.Equal(1f, billboard.AxisY, 4);
    }

    [Fact]
    public void Populate_HorizontalAxisUsesUnitWidthFactor()
    {
        // Horizontal A→B axis (glTF X): k = 1.0 and the head-on perpendicular
        // of cross(±X, +Z) is ∓Y.
        var mesh = CreateSpriteQuadMesh(axisHalfExtent: 40f, halfWidth: 5f, horizontal: true);
        var resolver = PsxSpriteVertexResolver.TryCreate(mesh);
        Assert.NotNull(resolver);

        // PSX (-40,0,0) → glTF (-40,0,0) anchor; axis toward (+40,0,0):
        // cross((1,0,0),(0,0,1)) = (0,-1,0).
        AssertResolves(resolver, 4, new Vector3(-40f, -5f, 0f));
        AssertResolves(resolver, 5, new Vector3(-40f, 5f, 0f));
        AssertResolves(resolver, 6, new Vector3(40f, -5f, 0f));
        AssertResolves(resolver, 7, new Vector3(40f, 5f, 0f));
    }

    [Fact]
    public void Thps2Skph_KnownSpriteMeshExpandsToItsAuthoredWidth()
    {
        var path = paths.SampleBuildsDir is null
            ? string.Empty
            : Path.Combine(
                paths.SampleBuildsDir,
                "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)",
                "CD",
                "skph.psx");
        Assert.SkipWhen(!File.Exists(path), "THPS2 PSX final sample not available");

        var file = PsxMeshFile.Parse(path);
        Assert.NotNull(file);

        // Palm-tree leaf card: 4 anchor verts (two coincident axis-end pairs
        // at raw Y -57/+58) + 4 sprite corners with half-widths +6/-3/-3/+6,
        // one textured quad referencing only the sprite corners.
        var meshIndex = Array.IndexOf(file.MeshNameHashes, 0xC07E62EAu);
        Assert.True(meshIndex >= 0, "sprite mesh 0xC07E62EA not found");
        var mesh = file.Meshes[meshIndex];
        Assert.Equal(8, mesh.Vertices.Count);
        var face = Assert.Single(mesh.Faces);
        Assert.True(face.IsQuad);
        Assert.True(mesh.HasSpriteVertices);

        var resolver = PsxSpriteVertexResolver.TryCreate(mesh);
        Assert.NotNull(resolver);
        Assert.True(resolver.TryResolvePosition(4, out var corner4));
        Assert.True(resolver.TryResolvePosition(5, out var corner5));
        Assert.True(resolver.TryResolvePosition(6, out var corner6));
        Assert.True(resolver.TryResolvePosition(7, out var corner7));

        // Quad width = k·|z4 - z5| in model units: 1.6·(6+3)/2.25 = 6.4 —
        // not the raw-field sliver (the old emission collapsed X to byte
        // offsets ÷ divisor, ~0-3.6 units of garbage).
        var expectedWidth = VerticalFactor * 9f / file.ScaleDivisor;
        Assert.Equal(expectedWidth, Vector3.Distance(corner4, corner5), 3);
        Assert.Equal(expectedWidth, Vector3.Distance(corner6, corner7), 3);

        // Corner height matches the authored axis extent (raw -57/+58 ÷ 2.25).
        Assert.Equal(57f / file.ScaleDivisor, corner4.Y, 3);
        Assert.Equal(-58f / file.ScaleDivisor, corner6.Y, 3);

        var billboard = resolver.BillboardMetadata;
        Assert.NotNull(billboard);
        Assert.Equal(0f, billboard.AxisX, 4);
        Assert.Equal(1f, MathF.Abs(billboard.AxisY), 4);
        Assert.Equal(0f, billboard.AxisZ, 4);
    }

    private static void AssertResolves(
        PsxSpriteVertexResolver resolver, uint vertexIndex, Vector3 expected)
    {
        Assert.True(resolver.TryResolvePosition(vertexIndex, out var position));
        Assert.True(Vector3.Distance(expected, position) < 1e-4f,
            $"v{vertexIndex}: expected {expected}, got {position}");
    }

    /// <summary>
    ///     Synthetic corpus-shape sprite mesh: vertices 0-3 are the anchor
    ///     pairs (two coincident per axis end), vertices 4-7 the sprite
    ///     corners, one textured quad over the corners. The far-end corners
    ///     reference the axis reversed, mirroring authored data.
    /// </summary>
    private static PsxMesh CreateSpriteQuadMesh(
        float axisHalfExtent, float halfWidth, bool horizontal = false)
    {
        PsxVertex Anchor(float extent)
        {
            return horizontal
                ? new PsxVertex { X = extent, Y = 0f, Z = 0f }
                : new PsxVertex { X = 0f, Y = extent, Z = 0f };
        }

        static PsxVertex Sprite(int anchorIndex, int mateIndex, float halfWidth)
        {
            return new PsxVertex
            {
                Type = 0x0010,
                RawX = (short)(anchorIndex * 8),
                RawY = (short)(mateIndex * 8),
                Z = halfWidth
            };
        }

        return new PsxMesh
        {
            Vertices =
            [
                Anchor(-axisHalfExtent),
                Anchor(-axisHalfExtent),
                Anchor(axisHalfExtent),
                Anchor(axisHalfExtent),
                Sprite(0, 2, halfWidth),
                Sprite(1, 2, -halfWidth),
                Sprite(2, 0, -halfWidth),
                Sprite(3, 0, halfWidth)
            ],
            Normals = [new PsxNormal { Z = 1f }],
            Faces =
            [
                new PsxFace
                {
                    IsQuad = true,
                    IsTextured = true,
                    TextureHash = 1,
                    Index0 = 4,
                    Index1 = 5,
                    Index2 = 6,
                    Index3 = 7
                }
            ],
            VertexCount = 8
        };
    }

    private static PsxMeshFile CreateFile(params PsxMesh[] meshes)
    {
        return new PsxMeshFile
        {
            Version = 4,
            Objects = meshes
                .Select((_, index) => new PsxMeshObject { MeshIndex = (ushort)index })
                .ToList(),
            Meshes = meshes.ToList(),
            MeshNameHashes = new uint[meshes.Length],
            TextureHashes = [1],
            ScaleDivisor = 2.25f,
            TranslationDivisor = 2.25f
        };
    }
}
