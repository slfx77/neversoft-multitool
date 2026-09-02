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
    /// <summary>The deck group's three authored texture columns / four rows.</summary>
    private static readonly float[] DeckU = [19f, 25f, 31f];

    private static readonly float[] DeckV = [0f, 4f, 27f, 31f];

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

        // The final clip is bounded like every other: its own prefix states 25.
        Assert.Equal(0xEAB218, library.ClipOffsets[^1]);
        Assert.Equal(25, library.ClipFrameCounts[^1]);

        var pose = GbaDhjModel.ReadPoseFrame(rom, library, 79, 0);
        Assert.Equal(0xEA4520, pose.Offset);
        Assert.Equal(0, pose.Header);
        Assert.Equal(GbaDhjModel.GroupCount, pose.Parts.Length);
        Assert.Equal(new GbaDhjModel.PartPose(0, 0, 9, 0, 1, 192), pose.Parts[0]);
        Assert.Equal(new GbaDhjModel.PartPose(-10, -18, 29, 11, 1, 37), pose.Parts[1]);
        Assert.Equal(new GbaDhjModel.PartPose(-8, -1, 145, 15, 250, 42), pose.Parts[12]);
    }

    /// <summary>
    ///     Each clip is PREFIXED by a u32 stating its own frame count; the word was
    ///     previously read as a trailing playback value on the clip in front of it.
    ///     This pins the measurement that separates the two readings, so the final
    ///     clip's length — which has no following offset to confirm it — rests on a
    ///     rule proven 93 times over rather than on a guess.
    /// </summary>
    [Fact]
    public void EveryClipStatesItsOwnFrameCountInThePrefixTheOffsetsPointPast()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var library = Assert.Single(GbaDhjModel.FindPoseLibraries(rom));

        // Clip 0's prefix sits exactly where the 94-entry offset table ends, with
        // no preceding clip that could own it as a trailer. That alone refutes the
        // trailer reading.
        Assert.Equal(
            library.HeaderOffset + 0x10 + library.ClipCount * 4,
            library.ClipOffsets[0] - 4);

        var prefixMatches = 0;
        var trailerMatchesOwnClip = 0;
        var trailerMatchesNextClip = 0;
        for (var clip = 0; clip < library.ClipCount; clip++)
        {
            var frames = library.ClipFrameCounts[clip];
            if (ReadU32(rom, library.ClipOffsets[clip] - 4) == frames)
                prefixMatches++;

            var trailer = ReadU32(rom, library.ClipOffsets[clip] + frames * GbaDhjModel.PoseRecordSize);
            if (trailer == frames)
                trailerMatchesOwnClip++;
            if (clip + 1 < library.ClipCount && trailer == library.ClipFrameCounts[clip + 1])
                trailerMatchesNextClip++;
        }

        // Read as a prefix the word is right every time; read as a trailer of the
        // clip it follows it is right 11 times in 94 (chance), and what it actually
        // states is the NEXT clip's count, every time there is a next clip.
        Assert.Equal(94, prefixMatches);
        Assert.Equal(11, trailerMatchesOwnClip);
        Assert.Equal(93, trailerMatchesNextClip);

        // The final clip's stated 25 records end byte-exactly on the following
        // resource header, which is the independent confirmation the offset table
        // cannot give.
        var end = library.ClipOffsets[^1]
                  + library.ClipFrameCounts[^1] * GbaDhjModel.PoseRecordSize;
        Assert.Equal(0xEAB9E8, end);
        Assert.Equal("JBOG"u8.ToArray(), rom.AsSpan(end, 4).ToArray());
    }

    /// <summary>
    ///     A stated count that contradicts the length its successor's offset
    ///     implies rejects the whole directory. That agreement across every bounded
    ///     clip is what licenses trusting the unbounded final clip's prefix, so it
    ///     must be enforced rather than merely observed.
    /// </summary>
    [Fact]
    public void PoseDirectoryWhoseStatedCountContradictsItsNextOffset_IsRejected()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var library = Assert.Single(GbaDhjModel.FindPoseLibraries(rom));

        // Corrupt one bounded clip's prefix to a value that is in range and fits
        // the ROM, but is not the count its successor's offset implies.
        var prefix = library.ClipOffsets[40] - 4;
        Assert.NotEqual(7, library.ClipFrameCounts[40]);
        BitConverter.GetBytes(7u).CopyTo(rom.AsSpan(prefix, 4));

        Assert.Empty(GbaDhjModel.FindPoseLibraries(rom));
    }

    private static uint ReadU32(ReadOnlySpan<byte> rom, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset, 4));

    /// <summary>
    ///     The per-vertex u16 at record +6 is a texture coordinate, not the normal
    ///     it was once read as. Model 19's group 0 is the skateboard deck: one flat
    ///     plane, so one geometric normal — yet all six stored values are distinct
    ///     and vary linearly with position. A normal cannot do that; a texture
    ///     coordinate must.
    /// </summary>
    [Fact]
    public void DeckVerticesCarryTextureCoordinatesThatVaryAcrossOneFlatPlane()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaDhjModel.FindModels(rom)[19];
        var vertices = GbaDhjModel.ReadVertices(rom, model);

        // Group 0 is the first 6 vertices.
        Assert.Equal(6, model.VertexCounts[0]);
        var deck = vertices.Take(6).ToArray();

        // Low byte is U, high byte is V.
        Assert.Equal(0x1B1F, deck[0].PackedTexCoord);
        Assert.Equal(31, deck[0].U);
        Assert.Equal(27, deck[0].V);
        Assert.Equal(6, deck.Select(static v => v.PackedTexCoord).Distinct().Count());

        // V tracks the deck's length (x) and U its width (y), ties included: two
        // vertices with the same coordinate take the same value, so this is a
        // mapping and not six unrelated numbers.
        Assert.Equal(
            deck.OrderBy(static v => v.X).Select(static v => (int)v.V),
            [0, 4, 4, 27, 27, 31]);
        Assert.Equal(
            deck.OrderBy(static v => v.Y).Select(static v => (int)v.U),
            [19, 19, 25, 25, 31, 31]);

        // Both centreline apex vertices (the nose and tail tips, y ~ 0) land on
        // exactly the midpoint of that 19..31 strip.
        foreach (var apex in deck.Where(static v => Math.Abs(v.Y) <= 1))
            Assert.Equal(25, apex.U);

        // And the plane really is flat: least-squares z = a*x + b*y + c leaves a
        // residual of ~3 units over a 126 x 23 unit footprint.
        var (a, b, c) = FitPlane(deck);
        var residual = deck.Max(v => Math.Abs(a * v.X + b * v.Y + c - v.Z));
        Assert.InRange(residual, 0f, 4f);
        Assert.Equal(126, deck.Max(static v => v.X) - deck.Min(static v => v.X));
    }

    /// <summary>
    ///     Texture coordinates are exported as authored. The great majority index
    ///     inside the 32x32 page, but 30 vertices do not and are NOT folded into
    ///     range — their two repeated literals are undecoded, and clamping them
    ///     would invent a meaning.
    /// </summary>
    [CorpusFact]
    public void TextureCoordinatesAreNotClampedToThePage()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "Downhill Jam GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);

        var all = GbaDhjModel.FindModels(rom)
            .SelectMany(model => GbaDhjModel.ReadVertices(rom, model))
            .ToArray();
        Assert.Equal(3248, all.Length);

        var outside = all
            .Where(static v => v.U >= GbaDhjModel.TexturePageSize
                               || v.V >= GbaDhjModel.TexturePageSize)
            .ToArray();
        Assert.Equal(30, outside.Length);
        Assert.Equal(
            [(16, 32), (61, 63)],
            outside.Select(static v => ((int)v.U, (int)v.V)).Distinct().Order());
    }

    /// <summary>
    ///     Least-squares fit of <c>z = A*x + B*y + C</c>, by Cramer's rule over the
    ///     3x3 normal equations. Used to show the deck group really is one plane,
    ///     which is what makes a per-vertex normal reading of its six distinct
    ///     stored values impossible.
    /// </summary>
    private static (float A, float B, float C) FitPlane(GbaDhjModel.Vertex[] points)
    {
        double sx = 0, sy = 0, sz = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0;
        foreach (var point in points)
        {
            sx += point.X;
            sy += point.Y;
            sz += point.Z;
            sxx += (double)point.X * point.X;
            syy += (double)point.Y * point.Y;
            sxy += (double)point.X * point.Y;
            sxz += (double)point.X * point.Z;
            syz += (double)point.Y * point.Z;
        }

        double[,] normal = { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, points.Length } };
        double[] rhs = [sxz, syz, sz];
        var det = Determinant(normal);
        return ((float)(Determinant(WithColumn(normal, 0, rhs)) / det),
            (float)(Determinant(WithColumn(normal, 1, rhs)) / det),
            (float)(Determinant(WithColumn(normal, 2, rhs)) / det));

        static double Determinant(double[,] a) =>
            a[0, 0] * (a[1, 1] * a[2, 2] - a[1, 2] * a[2, 1])
            - a[0, 1] * (a[1, 0] * a[2, 2] - a[1, 2] * a[2, 0])
            + a[0, 2] * (a[1, 0] * a[2, 1] - a[1, 1] * a[2, 0]);

        static double[,] WithColumn(double[,] a, int column, double[] values)
        {
            var copy = (double[,])a.Clone();
            for (var row = 0; row < 3; row++)
                copy[row, column] = values[row];
            return copy;
        }
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

        var allVertices = document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .ToArray();
        var boundsHeight = allVertices.Max(static vertex => vertex.Position.Y)
                           - allVertices.Min(static vertex => vertex.Position.Y);
        Assert.InRange(boundsHeight, 402f, 405f);

        // The authored texture coordinates reach the document, normalized by the
        // page edge. Nothing is bound to them: the rider's texture page has not
        // been located in ROM, so this model carries mapping and no image.
        Assert.All(document.Materials, material => Assert.Null(material.TextureIndex));
        Assert.Empty(document.Textures);
        Assert.Contains(allVertices, vertex => vertex.TexCoord != System.Numerics.Vector2.Zero);
        var deckCorners = document.Meshes[0].Primitives[0].Vertices;
        Assert.All(deckCorners, vertex =>
        {
            Assert.Contains(vertex.TexCoord.X * GbaDhjModel.TexturePageSize, DeckU);
            Assert.Contains(vertex.TexCoord.Y * GbaDhjModel.TexturePageSize, DeckV);
        });

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
