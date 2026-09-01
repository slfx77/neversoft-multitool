using System.Numerics;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Texture.Gba;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Builds a Vicarious Visions GBA level as a <b>textured 3D model</b>: the
///     cartridge-derived collision surface (quarter-pipes curve and ramps slope,
///     computed by executing the ROM's height functions) with the level's own
///     pre-baked isometric art applied as the texture. THPS3 functions that depend
///     on unavailable live scene state are evaluated with the documented
///     deterministic empty-scene contribution.
///
///     <para>For THPS2, the texture mapping is the engine's own art transform: each surface
///     point <c>(wx, wy, z)</c> samples the art pixel the game draws it with —
///     <c>artX = X0 + 16(wy − wx)</c>, <c>artY = Y0 + 8(wx + wy) − 16z</c>, the
///     per-level origin being the ROM field at the record's <c>+0x64/+0x68</c>. The
///     projection is the iso view direction, so front-facing surfaces are textured
///     exactly; surfaces the iso view grazes (steep walls parallel to the view)
///     stretch their art strip, which is the honest limit of a single pre-rendered
///     view. THPS3 through American Sk8land use the same isometric basis, but no
///     equivalent stored origin has yet been identified; their projected collision
///     bounds are centred on the art canvas. Vertical skirts between cells make
///     ledges and walls solid.</para>
///
///     <para>The one-cell out-of-bounds kill-wall ring (base height above
///     <see cref="GbaCollisionRenderer.OutOfBoundsHeight" />) is omitted, exactly as
///     in the 2D renders — drawing a 34-unit wall around the playfield would
///     entomb it. Materials are unlit: the art carries the game's baked shading.</para>
/// </summary>
internal static class GbaLevelGeometryWriter
{
    /// <summary>World-unit → GLB-unit scale (16 GLB units per world unit puts the
    ///     texture at roughly one texel per GLB unit and levels at PSX-like extents).</summary>
    public const float Scale = 16f;

    /// <summary>
    ///     Sub-quads per cell edge in the EXPORTED mesh. Deliberately the writer's
    ///     own constant: it used to alias the 2D renderer's, so changing how finely
    ///     the mesh is tessellated would silently move a pinned overlay image, and
    ///     the two have no reason to agree — one is a picture, the other is geometry.
    /// </summary>
    private const int Sub = 4;

    /// <summary>
    ///     Marks the primitive holding faces the baked art cannot texture. Same
    ///     convention as the N64 writer's <c>__overlay</c>: a suffix a viewer can
    ///     key on without the geometry changing meaning.
    /// </summary>
    public const string GrazedSuffix = "__grazed";

    /// <summary>Visibility-group id for the out-of-bounds ring.</summary>
    public const string BoundaryGroupId = "gba-level-boundary";
    private const double Fixed = 4096.0;

    public static void Populate(
        ModelDocument document,
        GbaLevelNativeSource native,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null)
    {
        var rom = native.Rom;
        var trueRecord = native.TrueRecordOffset;

        GbaLaterLevelArt.LaterLevel? later = null;
        foreach (var candidate in GbaLaterLevelArt.FindLevels(rom))
            if (candidate.LevelRecordOffset == trueRecord)
            {
                later = candidate;
                break;
            }

        GbaThps3LevelArt.Thps3Level? thps3 = null;
        foreach (var candidate in GbaThps3LevelArt.FindLevels(rom))
            if (candidate.LevelRecordOffset == trueRecord)
            {
                thps3 = candidate;
                break;
            }

        IGbaCollisionGrid grid;
        GbaLevelImages.GbaLevelRender art;
        if (later is { } laterLevel)
        {
            grid = GbaLaterCollisionSurface.TryLoad(rom, laterLevel)
                   ?? throw new InvalidDataException("The later level's collision complex does not validate");
            art = GbaLaterLevelArt.RenderColourSurface(rom, laterLevel)
                  ?? throw new InvalidDataException("The later level's art layers do not decode");
        }
        else if (thps3 is { } thps3Level)
        {
            grid = GbaThps3CollisionSurface.TryLoad(rom, thps3Level)
                   ?? throw new InvalidDataException("The THPS3 level's collision complex does not validate");
            art = GbaThps3LevelArt.RenderColourSurface(rom, thps3Level)
                  ?? throw new InvalidDataException("The THPS3 level's art layers do not decode");
        }
        else
        {
            grid = GbaCollisionSurface.TryLoad(rom, trueRecord)
                   ?? throw new InvalidDataException("The level record's collision fields do not validate");
            var scanLevel = new GbaLevelImages.GbaLevel(
                (uint)(0x08000000 + trueRecord + 0x144), 0, 0, 0);
            art = GbaLevelImages.RenderColourSurface(rom, scanLevel)
                  ?? throw new InvalidDataException("The level's art layers do not decode");
        }

        document.Textures.Add(new ModelTexture
        {
            Name = native.LevelName,
            PngBytes = ImageWriter.WritePngToMemory(art.Width, art.Height, art.Rgba),
            WrapU = ModelTextureWrap.ClampToEdge,
            WrapV = ModelTextureWrap.ClampToEdge
        });
        var materialIndex = document.Materials.Count;
        document.Materials.Add(new RenderMaterial
        {
            Name = native.LevelName,
            TextureIndex = document.Textures.Count - 1
        });

        // THPS2 stores the exact art origin, so its transparency can safely remove
        // collision quads the authored isometric view never draws (School II has a
        // deep notch between its building wings). THPS3/later origins are not decoded:
        // its centred projection is useful as a texture preview, but must never be
        // allowed to delete real collision geometry.
        var hasExactArtOrigin = later == null && thps3 == null;
        var undrawn = hasExactArtOrigin
            ? GbaLevelArtCoverage.BuildUndrawnMask(art.Rgba, art.Width, art.Height)
            : null;

        // The out-of-bounds ring is the level's real boundary: the engine's own
        // kill wall, standing above 30 world units while the playfield remains at
        // or below 29.9998 in the complete later corpus. It is omitted by default
        // because a wall that tall drawn around the
        // level entombs it - which is also why the boundary reads as "flat" without
        // it. Behind a switch it becomes real geometry.
        var showBoundary = visibilityOverrides != null
                           && visibilityOverrides.TryGetValue(BoundaryGroupId, out var on)
                           && on;

        var step = Sub + 1;
        var w = grid.Width;
        var h = grid.Height;

        // Per-cell surface samples (world-unit heights) and the live mask.
        var heights = new double[w * h][];
        var live = new bool[w * h];
        var boundaryCells = 0;
        for (var gy = 0; gy < h; gy++)
        for (var gx = 0; gx < w; gx++)
        {
            var index = gy * w + gx;
            // Judged by the material's own sampled surface, never the raw
            // base-height word — material 30 stores something else there, and
            // trusting it punched holes where real objects stand.
            var outOfBounds = GbaCollisionRenderer.IsOutOfBounds(rom, grid, gx, gy);
            if (outOfBounds) boundaryCells++;
            live[index] = !outOfBounds || showBoundary;
            if (!live[index])
                continue;
            var samples = grid.SampleCell(rom, gx, gy, step);
            var values = new double[samples.Length];
            for (var i = 0; i < samples.Length; i++)
                values[i] = samples[i] / Fixed;
            heights[index] = values;
        }

        var origin = hasExactArtOrigin
            ? GbaLevelArtProjection.TryReadOrigin(rom, trueRecord)
              ?? throw new InvalidDataException("The level record carries no art origin")
            : CentreProjectionOnArt(grid, heights, live, art);

        var vertices = new List<ModelVertex>();
        var indices = new List<int>();

        // Faces the engine's own baked view cannot texture: where the surface
        // rises at 45 degrees in the view direction, the art projection's
        // determinant passes through zero and flips sign, so the strip of art
        // covering them stretches without bound. They are kept, because they are
        // real geometry, but separated so nobody reads the stretch as a decode bug.
        var grazedVertices = new List<ModelVertex>();
        var grazedIndices = new List<int>();

        for (var gy = 0; gy < h; gy++)
        for (var gx = 0; gx < w; gx++)
        {
            var index = gy * w + gx;
            if (!live[index])
                continue;
            var values = heights[index];

            // Top surface: Sub×Sub quads over the engine's sampled heights.
            for (var j = 0; j < Sub; j++)
            for (var i = 0; i < Sub; i++)
            {
                var a = SurfaceVertex(gx, gy, i, j, values[j * step + i], origin, art);
                var b = SurfaceVertex(gx, gy, i + 1, j, values[j * step + i + 1], origin, art);
                var c = SurfaceVertex(gx, gy, i + 1, j + 1, values[(j + 1) * step + i + 1], origin, art);
                var d = SurfaceVertex(gx, gy, i, j + 1, values[(j + 1) * step + i], origin, art);
                if (IsUndrawnQuad(undrawn, art, a, b, c, d))
                    continue;

                // Slope across this quad, in world units per world unit.
                var span = GbaLevelArtProjection.WorldUnitsPerCell / Sub;
                var slopeX = (values[j * step + i + 1] - values[j * step + i]) / span;
                var slopeY = (values[(j + 1) * step + i] - values[j * step + i]) / span;
                if (GbaLevelArtProjection.IsGrazing(slopeX, slopeY))
                    AddQuad(grazedVertices, grazedIndices, a, b, c, d);
                else
                    AddQuad(vertices, indices, a, b, c, d);
            }

            // Vertical skirts down to each neighbour's surface (a short lip at the
            // grid boundary), so ledges and walls are solid in the viewer.
            foreach (var (dx, dy) in (ReadOnlySpan<(int, int)>)[(1, 0), (0, 1), (-1, 0), (0, -1)])
            {
                var nx = gx + dx;
                var ny = gy + dy;
                var neighbour = nx >= 0 && nx < w && ny >= 0 && ny < h && live[ny * w + nx]
                    ? heights[ny * w + nx]
                    : null;
                for (var t = 0; t < Sub; t++)
                {
                    (int I, int J) ea, eb;
                    if (dx == 1) { ea = (Sub, t); eb = (Sub, t + 1); }
                    else if (dx == -1) { ea = (0, t + 1); eb = (0, t); }
                    else if (dy == 1) { ea = (t + 1, Sub); eb = (t, Sub); }
                    else { ea = (t, 0); eb = (t + 1, 0); }

                    var hi1 = values[ea.J * step + ea.I];
                    var hi2 = values[eb.J * step + eb.I];
                    double lo1, lo2;
                    if (neighbour != null)
                    {
                        lo1 = neighbour[(ea.J - dy * Sub) * step + (ea.I - dx * Sub)];
                        lo2 = neighbour[(eb.J - dy * Sub) * step + (eb.I - dx * Sub)];
                    }
                    else
                    {
                        lo1 = lo2 = Math.Min(hi1, hi2) - 0.5;
                    }

                    lo1 = Math.Min(lo1, hi1);
                    lo2 = Math.Min(lo2, hi2);
                    if (hi1 - lo1 < 1e-4 && hi2 - lo2 < 1e-4)
                        continue;

                    var a = SurfaceVertex(gx, gy, ea.I, ea.J, hi1, origin, art);
                    var b = SurfaceVertex(gx, gy, eb.I, eb.J, hi2, origin, art);
                    var c = SurfaceVertex(gx, gy, eb.I, eb.J, lo2, origin, art);
                    var d = SurfaceVertex(gx, gy, ea.I, ea.J, lo1, origin, art);
                    if (IsUndrawnQuad(undrawn, art, a, b, c, d))
                        continue;
                    AddQuad(vertices, indices, a, b, c, d);
                }
            }
        }

        var mesh = new ModelMesh { Name = native.LevelName };
        ModelDocumentGeometryAdapter.AddPrimitive(mesh, native.LevelName, materialIndex, vertices, indices);
        if (grazedIndices.Count > 0)
        {
            ModelDocumentGeometryAdapter.AddPrimitive(
                mesh, native.LevelName + GrazedSuffix, materialIndex, grazedVertices, grazedIndices);
        }

        ModelDocumentGeometryAdapter.AddMeshNode(document, native.LevelName, mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);

        if (boundaryCells > 0)
        {
            document.VisibilityGroups.Add(new ModelVisibilityGroup
            {
                Id = BoundaryGroupId,
                Label = $"Out-of-bounds boundary ({boundaryCells} cells)",
                DefaultEnabled = false,
                IsEnabled = showBoundary,
                Source = ModelVisibilityGroupSource.AlternateGeometry,
                SourceReference =
                    $"collision cells whose sampled surface stands above "
                    + $"{GbaCollisionRenderer.OutOfBoundsHeight:0.#} world units - the engine's "
                    + "kill wall. Its art is not derivable: the baked view grazes a wall that "
                    + "tall, and the transform's -16 px per world unit of height was measured "
                    + "only over ground heights 0-10.5."
            });
        }
    }

    private static (double X, double Y) CentreProjectionOnArt(
        IGbaCollisionGrid grid,
        double[][] heights,
        bool[] live,
        GbaLevelImages.GbaLevelRender art)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        var step = Sub + 1;
        for (var gy = 0; gy < grid.Height; gy++)
        for (var gx = 0; gx < grid.Width; gx++)
        {
            var index = gy * grid.Width + gx;
            if (!live[index])
                continue;
            var values = heights[index];
            foreach (var (i, j) in (ReadOnlySpan<(int, int)>)[(0, 0), (Sub, 0), (0, Sub), (Sub, Sub)])
            {
                var (wx, wy) = GbaLevelArtProjection.CellSamplePosition(gx, gy, i, j, Sub);
                var projected = GbaLevelArtProjection.Project((0, 0), wx, wy, values[j * step + i]);
                minX = Math.Min(minX, projected.X);
                maxX = Math.Max(maxX, projected.X);
                minY = Math.Min(minY, projected.Y);
                maxY = Math.Max(maxY, projected.Y);
            }
        }

        if (minX > maxX)
            throw new InvalidDataException("The collision complex has no drawable cells");
        return ((art.Width - minX - maxX) / 2.0, (art.Height - minY - maxY) / 2.0);
    }

    // World (wx, wy horizontal; z up) → GLB right-handed Y-up: (X, Y, Z) = (wy, z, wx)·Scale.
    // UV = the engine's art transform, normalised into the art image.
    private static ModelVertex SurfaceVertex(
        int gx, int gy, int i, int j, double z, (double X, double Y) origin,
        GbaLevelImages.GbaLevelRender art)
    {
        var (wx, wy) = GbaLevelArtProjection.CellSamplePosition(gx, gy, i, j, Sub);
        var (artX, artY) = GbaLevelArtProjection.Project(origin, wx, wy, z);
        var u = artX / art.Width;
        var v = artY / art.Height;
        return new ModelVertex(
            new Vector3((float)(wy * Scale), (float)(z * Scale), (float)(wx * Scale)),
            Vector3.UnitY,
            Vector4.One,
            new Vector2((float)u, (float)v));
    }

    /// <summary>
    ///     True when the art draws nothing anywhere on this quad. Requiring ALL
    ///     four corners to be undrawn keeps the boundary intact: a cell straddling
    ///     the art's edge still has a drawn corner, so the level keeps its rim
    ///     rather than eroding by a cell.
    /// </summary>
    private static bool IsUndrawnQuad(
        bool[]? undrawn, GbaLevelImages.GbaLevelRender art,
        ModelVertex a, ModelVertex b, ModelVertex c, ModelVertex d)
    {
        if (undrawn == null)
            return false;
        return GbaLevelArtCoverage.IsUndrawn(undrawn, art.Width, art.Height, a.TexCoord.X, a.TexCoord.Y)
               && GbaLevelArtCoverage.IsUndrawn(undrawn, art.Width, art.Height, b.TexCoord.X, b.TexCoord.Y)
               && GbaLevelArtCoverage.IsUndrawn(undrawn, art.Width, art.Height, c.TexCoord.X, c.TexCoord.Y)
               && GbaLevelArtCoverage.IsUndrawn(undrawn, art.Width, art.Height, d.TexCoord.X, d.TexCoord.Y);
    }

    // Two triangles with a shared face normal computed from the quad's real geometry.
    private static void AddQuad(
        List<ModelVertex> vertices, List<int> indices,
        ModelVertex a, ModelVertex b, ModelVertex c, ModelVertex d)
    {
        var normal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
        normal = normal.LengthSquared() < 1e-12f ? Vector3.UnitY : Vector3.Normalize(normal);
        if (normal.Y < 0)
            normal = -normal;

        ModelVertex With(ModelVertex vtx) => vtx with { Normal = normal };
        ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, With(a), With(b), With(c));
        ModelDocumentGeometryAdapter.AddTriangle(vertices, indices, With(a), With(c), With(d));
    }
}
