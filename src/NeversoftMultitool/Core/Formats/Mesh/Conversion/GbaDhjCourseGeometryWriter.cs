using System.Numerics;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>Builds portable GLB documents from Downhill Jam's polygon courses.</summary>
internal static class GbaDhjCourseGeometryWriter
{
    /// <summary>The exported coordinates retain the course's authored world-unit scale.</summary>
    internal const float Scale = 1f;

    // A half-unit viewer ribbon keeps the engine's mathematical collision lines
    // visible in ordinary triangle-only GLB viewers without materially changing
    // their positions. These triangles are not present in the source format.
    private const float CollisionRibbonHalfWidth = 0.5f;

    // A placed object stores a single point, so the marker is a viewer aid with
    // no authored extent. Eight units keeps it legible against a road that is
    // roughly 80 units wide on the runtime course without hiding the surface
    // underneath it.
    private const float PlacedObjectMarkerRadius = 8f;

    /// <summary>
    ///     Name of the node carrying the placed-object markers. It is a sibling
    ///     of the course mesh node rather than part of it, so the exported course
    ///     geometry is bit-identical to an export without markers and a consumer
    ///     can drop the whole node by name.
    /// </summary>
    internal const string PlacedObjectNodeSuffix = "_placed_objects";

    internal static ModelDocument BuildVisual(
        ReadOnlySpan<byte> rom,
        GbaDhjCourse.CourseInfo course,
        string name)
    {
        var sourceVertices = GbaDhjCourse.ReadVertices(rom, course);
        var faces = GbaDhjCourse.ReadFaces(rom, course);
        var palette = GbaDhjCourse.ReadPaletteRgba(rom, course);
        var document = new ModelDocument
        {
            Name = name,
            SourceKind = ModelSourceKind.Generic
        };
        var mesh = new ModelMesh { Name = name };
        var textureMaterials = new Dictionary<int, int>();
        var flatMaterials = new Dictionary<int, int>();

        foreach (var group in faces
                     .GroupBy(static face => new FaceMaterialKey(face.IsFlatColour, face.IsFlatColour
                         ? face.PaletteIndex
                         : face.TexturePage))
                     .OrderBy(static group => group.Key.Flat)
                     .ThenBy(static group => group.Key.Index))
        {
            var materialIndex = group.Key.Flat
                ? GetFlatMaterial(document, flatMaterials, palette, group.Key.Index, name)
                : GetTextureMaterial(rom, course, document, textureMaterials, group.Key.Index, name);
            var primitiveVertices = new List<ModelVertex>();
            var indices = new List<int>();
            foreach (var face in group)
            {
                var a = ToGlb(sourceVertices[face.V0]);
                var b = ToGlb(sourceVertices[face.V1]);
                var c = ToGlb(sourceVertices[face.V2]);
                var normal = FaceNormal(a, b, c);
                var divisor = face.TexturePage == 0
                    ? GbaDhjCourse.PageZeroDimension
                    : GbaDhjCourse.TexturePageDimension;
                ModelDocumentGeometryAdapter.AddTriangle(
                    primitiveVertices,
                    indices,
                    new ModelVertex(a, normal, Vector4.One, Uv(face.Uv0, divisor)),
                    new ModelVertex(b, normal, Vector4.One, Uv(face.Uv1, divisor)),
                    new ModelVertex(c, normal, Vector4.One, Uv(face.Uv2, divisor)));
            }

            var primitiveName = group.Key.Flat
                ? $"flat_palette_{group.Key.Index:D3}"
                : $"texture_page_{group.Key.Index:D2}";
            ModelDocumentGeometryAdapter.AddPrimitive(
                mesh, primitiveName, materialIndex, primitiveVertices, indices);
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, name, mesh);
        AddPlacedObjectMarkers(rom, course, document, name);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
        return document;
    }

    /// <summary>
    ///     Append the course's placed-object bank as its own root node of small
    ///     octahedral markers, one per record, at the record's authored world
    ///     position and grouped into one primitive per raw type byte.
    ///
    ///     <para>The markers are a viewer aid: the format stores a point and a
    ///     type, not geometry, and the meshes the type ids select have not been
    ///     located. They are therefore emitted as a separate node
    ///     (<see cref="PlacedObjectNodeSuffix" />) instead of being merged into
    ///     the course mesh, which leaves every authored course primitive exactly
    ///     as it was before this node existed.</para>
    ///
    ///     <para>Primitives and materials are named by the raw type id, because
    ///     what an id denotes is not decoded. The per-type colour is a
    ///     deterministic function of that id so one type reads the same across
    ///     every course; it carries no meaning of its own.</para>
    /// </summary>
    private static void AddPlacedObjectMarkers(
        ReadOnlySpan<byte> rom,
        GbaDhjCourse.CourseInfo course,
        ModelDocument document,
        string name)
    {
        var objects = GbaDhjCourse.ReadObjects(rom, course);
        if (objects.Length == 0)
            return;

        var mesh = new ModelMesh { Name = name + PlacedObjectNodeSuffix };
        foreach (var group in objects
                     .GroupBy(static placed => placed.Type)
                     .OrderBy(static group => group.Key))
        {
            var material = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
            {
                Name = $"{name}_placed_object_type_{group.Key:D3}",
                BaseColor = TypeMarkerColour(group.Key),
                DoubleSided = true,
                Unlit = true
            });
            var vertices = new List<ModelVertex>();
            var indices = new List<int>();
            foreach (var placed in group)
                AddMarkerOctahedron(vertices, indices, ToGlb(placed));

            ModelDocumentGeometryAdapter.AddPrimitive(
                mesh, $"placed_object_type_{group.Key:D3}", material, vertices, indices);
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, mesh.Name, mesh);
    }

    /// <summary>
    ///     Build a viewer proxy for every sequential obstacle/boundary collision
    ///     polyline and for each authored road-edge array. When the two edge
    ///     arrays have exactly matching counts, also pair corresponding points
    ///     into a road-envelope proxy. None of these output triangles is an
    ///     authored collision mesh.
    /// </summary>
    internal static ModelDocument BuildCollision(
        ReadOnlySpan<byte> rom,
        GbaDhjCourse.CourseInfo course,
        string name)
    {
        var document = new ModelDocument
        {
            Name = name,
            SourceKind = ModelSourceKind.Generic
        };
        var mesh = new ModelMesh { Name = name };

        var lineMaterial = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
        {
            Name = $"{name}_collision_polylines",
            BaseColor = new Vector4(1f, 0.20f, 0.08f, 1f),
            DoubleSided = true,
            Unlit = true
        });
        var edgeMaterial = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
        {
            Name = $"{name}_authored_road_edges",
            BaseColor = new Vector4(0.96f, 0.78f, 0.08f, 1f),
            DoubleSided = true,
            Unlit = true
        });

        AddRoadEdgeRibbons(rom, course, mesh, edgeMaterial);
        if (course.LeftEdgePointCount >= 2
            && course.LeftEdgePointCount == course.RightEdgePointCount)
        {
            var roadMaterial = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
            {
                Name = $"{name}_paired_edge_proxy",
                BaseColor = new Vector4(0.12f, 0.56f, 0.95f, 0.55f),
                AlphaMode = ModelAlphaMode.Blend,
                DoubleSided = true,
                Unlit = true
            });
            AddPairedEdgeProxy(rom, course, mesh, roadMaterial);
        }
        AddCollisionRibbons(rom, course, mesh, lineMaterial);
        ModelDocumentGeometryAdapter.AddMeshNode(document, name, mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
        return document;
    }

    internal static Vector3 ToGlb(GbaDhjCourse.Vertex vertex) =>
        new(vertex.X * Scale, vertex.Y * Scale, -vertex.Z * Scale);

    internal static Vector3 ToGlb(GbaDhjCourse.EdgePoint point) =>
        new(point.X * Scale, point.Y * Scale, -point.Z * Scale);

    internal static Vector3 ToGlb(GbaDhjCourse.CollisionPoint point) =>
        new(point.X * Scale, point.Y * Scale, -point.Z * Scale);

    /// <summary>
    ///     Placed objects are stored in the vertex bank's own space and axis
    ///     order, so they take the identical conversion the course mesh takes.
    /// </summary>
    internal static Vector3 ToGlb(GbaDhjCourse.PlacedObject placed) =>
        new(placed.X * Scale, placed.Y * Scale, -placed.Z * Scale);

    /// <summary>
    ///     Eight-triangle octahedron centred on the record's authored point. It
    ///     is a drawable stand-in for a single stored coordinate, chosen because
    ///     it reads the same from every angle; it is not decoded shape.
    /// </summary>
    private static void AddMarkerOctahedron(
        List<ModelVertex> vertices,
        List<int> indices,
        Vector3 centre)
    {
        const float r = PlacedObjectMarkerRadius;
        var up = centre + new Vector3(0f, r, 0f);
        var down = centre + new Vector3(0f, -r, 0f);
        var east = centre + new Vector3(r, 0f, 0f);
        var west = centre + new Vector3(-r, 0f, 0f);
        var north = centre + new Vector3(0f, 0f, r);
        var south = centre + new Vector3(0f, 0f, -r);

        AddMarkerTriangle(vertices, indices, up, east, north);
        AddMarkerTriangle(vertices, indices, up, north, west);
        AddMarkerTriangle(vertices, indices, up, west, south);
        AddMarkerTriangle(vertices, indices, up, south, east);
        AddMarkerTriangle(vertices, indices, down, north, east);
        AddMarkerTriangle(vertices, indices, down, west, north);
        AddMarkerTriangle(vertices, indices, down, south, west);
        AddMarkerTriangle(vertices, indices, down, east, south);
    }

    private static void AddMarkerTriangle(
        List<ModelVertex> vertices,
        List<int> indices,
        Vector3 a,
        Vector3 b,
        Vector3 c)
    {
        var normal = FaceNormal(a, b, c);
        ModelDocumentGeometryAdapter.AddTriangle(
            vertices, indices,
            new ModelVertex(a, normal, Vector4.One, Vector2.Zero),
            new ModelVertex(b, normal, Vector4.One, Vector2.Zero),
            new ModelVertex(c, normal, Vector4.One, Vector2.Zero));
    }

    /// <summary>
    ///     A stable, well-separated colour per raw type id. The golden-ratio hue
    ///     step only guarantees that neighbouring ids look different; it asserts
    ///     nothing about what the ids mean.
    /// </summary>
    private static Vector4 TypeMarkerColour(byte type)
    {
        var hue = (float)((type * 0.6180339887498949) % 1.0);
        var sector = hue * 6f;
        var index = (int)MathF.Floor(sector) % 6;
        var fraction = sector - MathF.Floor(sector);
        const float value = 1f;
        const float minimum = 0.15f;
        var rising = minimum + (value - minimum) * fraction;
        var falling = value - (value - minimum) * fraction;
        return index switch
        {
            0 => new Vector4(value, rising, minimum, 1f),
            1 => new Vector4(falling, value, minimum, 1f),
            2 => new Vector4(minimum, value, rising, 1f),
            3 => new Vector4(minimum, falling, value, 1f),
            4 => new Vector4(rising, minimum, value, 1f),
            _ => new Vector4(value, minimum, falling, 1f)
        };
    }

    private static int GetTextureMaterial(
        ReadOnlySpan<byte> rom,
        GbaDhjCourse.CourseInfo course,
        ModelDocument document,
        IDictionary<int, int> materials,
        int pageIndex,
        string name)
    {
        if (materials.TryGetValue(pageIndex, out var existing))
            return existing;

        var page = GbaDhjCourse.ReadTexturePage(rom, course, pageIndex);
        var textureIndex = ModelDocumentGeometryAdapter.AddTexture(
            document,
            $"{name}_page_{pageIndex:D2}",
            ImageWriter.WritePngToMemory(page.Width, page.Height, page.Rgba),
            wrapU: ModelTextureWrap.Repeat,
            wrapV: ModelTextureWrap.Repeat,
            nearestFilter: true);
        var material = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
        {
            Name = $"{name}_page_{pageIndex:D2}",
            TextureIndex = textureIndex,
            AlphaMode = ModelAlphaMode.Mask,
            AlphaCutoff = 0.5f,
            DoubleSided = true,
            Unlit = true
        });
        materials.Add(pageIndex, material);
        return material;
    }

    private static int GetFlatMaterial(
        ModelDocument document,
        IDictionary<int, int> materials,
        byte[] palette,
        int paletteIndex,
        string name)
    {
        if (materials.TryGetValue(paletteIndex, out var existing))
            return existing;
        if ((uint)paletteIndex >= GbaDhjCourse.PaletteColourCount)
            throw new InvalidDataException("Downhill Jam flat face selects an absent palette colour");

        var at = paletteIndex * 4;
        var material = ModelDocumentGeometryAdapter.AddMaterial(document, new RenderMaterial
        {
            Name = $"{name}_palette_{paletteIndex:D3}",
            BaseColor = new Vector4(
                palette[at] / 255f,
                palette[at + 1] / 255f,
                palette[at + 2] / 255f,
                palette[at + 3] / 255f),
            DoubleSided = true,
            Unlit = true
        });
        materials.Add(paletteIndex, material);
        return material;
    }

    private static void AddPairedEdgeProxy(
        ReadOnlySpan<byte> rom,
        GbaDhjCourse.CourseInfo course,
        ModelMesh mesh,
        int materialIndex)
    {
        var left = GbaDhjCourse.ReadLeftEdge(rom, course);
        var right = GbaDhjCourse.ReadRightEdge(rom, course);
        // Discovery pins both counts, and the caller deliberately refuses to
        // zip unequal arrays: course 6 diverges (661/635), so ordinal pairing
        // after that point is not structurally established.
        if (left.Length != right.Length || left.Length < 2)
            throw new InvalidDataException("Downhill Jam edge arrays cannot be paired safely");

        var vertices = new List<ModelVertex>();
        var indices = new List<int>();
        for (var i = 0; i < left.Length - 1; i++)
        {
            AddQuad(
                vertices,
                indices,
                ToGlb(left[i]), ToGlb(right[i]), ToGlb(right[i + 1]), ToGlb(left[i + 1]));
        }

        ModelDocumentGeometryAdapter.AddPrimitive(
            mesh, "paired_road_edges_viewer_proxy", materialIndex, vertices, indices);
    }

    private static void AddCollisionRibbons(
        ReadOnlySpan<byte> rom,
        GbaDhjCourse.CourseInfo course,
        ModelMesh mesh,
        int materialIndex)
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();
        foreach (var polyline in GbaDhjCourse.ReadCollisionPolylines(rom, course))
        {
            for (var i = 0; i < polyline.Points.Length - 1; i++)
                AddViewerRibbonSegment(
                    vertices, indices, ToGlb(polyline.Points[i]), ToGlb(polyline.Points[i + 1]));
        }

        ModelDocumentGeometryAdapter.AddPrimitive(
            mesh, "referenced_collision_polylines", materialIndex, vertices, indices);
    }

    private static void AddRoadEdgeRibbons(
        ReadOnlySpan<byte> rom,
        GbaDhjCourse.CourseInfo course,
        ModelMesh mesh,
        int materialIndex)
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();
        AddEdge(GbaDhjCourse.ReadLeftEdge(rom, course));
        AddEdge(GbaDhjCourse.ReadRightEdge(rom, course));
        ModelDocumentGeometryAdapter.AddPrimitive(
            mesh, "authored_road_edges_viewer_ribbons", materialIndex, vertices, indices);

        void AddEdge(GbaDhjCourse.EdgePoint[] points)
        {
            for (var i = 0; i < points.Length - 1; i++)
                AddViewerRibbonSegment(vertices, indices, ToGlb(points[i]), ToGlb(points[i + 1]));
        }
    }

    private static void AddViewerRibbonSegment(
        List<ModelVertex> vertices,
        List<int> indices,
        Vector3 a,
        Vector3 b)
    {
        var direction = b - a;
        if (direction.LengthSquared() < 1e-8f)
            return;
        var side = Vector3.Cross(Vector3.UnitY, direction);
        if (side.LengthSquared() < 1e-8f)
            side = Vector3.UnitX;
        else
            side = Vector3.Normalize(side);
        side *= CollisionRibbonHalfWidth;
        AddQuad(vertices, indices, a - side, b - side, b + side, a + side);
    }

    private static void AddQuad(
        List<ModelVertex> vertices,
        List<int> indices,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d)
    {
        var normal = FaceNormal(a, b, c);
        var colour = Vector4.One;
        var uv = Vector2.Zero;
        ModelDocumentGeometryAdapter.AddTriangle(
            vertices, indices,
            new ModelVertex(a, normal, colour, uv),
            new ModelVertex(b, normal, colour, uv),
            new ModelVertex(c, normal, colour, uv));
        ModelDocumentGeometryAdapter.AddTriangle(
            vertices, indices,
            new ModelVertex(a, normal, colour, uv),
            new ModelVertex(c, normal, colour, uv),
            new ModelVertex(d, normal, colour, uv));
    }

    private static Vector3 FaceNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        var normal = Vector3.Cross(b - a, c - a);
        return normal.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(normal);
    }

    private static Vector2 Uv(ushort packed, int divisor) =>
        new((packed & 0xFF) / (float)divisor, (packed >> 8) / (float)divisor);

    private readonly record struct FaceMaterialKey(bool Flat, int Index);
}
