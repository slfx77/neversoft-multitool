using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Xbox 360 / PS3 scene files (derived 2026-08-28). The container is the THAW
///     layout read big-endian behind a repeating FAAABACA sentinel, but every inner
///     struct has its own next-gen size and the vertex/index pointers land on a
///     20-byte GPU descriptor with the data at descriptor+20.
///     <para>
///         The load-bearing check here is the cross-platform one: the same asset
///         ships on GameCube, whose GX display-list reader shares no code with this
///         parser, so agreement on vertex count, triangle count and every position
///         cannot happen by accident.
///     </para>
/// </summary>
public class NextGenSceneFileTests(TestPaths paths)
{
    private const string ThawX360 = "Tony Hawk's American Wasteland (2005-10-29, X360 - Final)";
    private const string ThawGc = "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";
    private const string P8X360 = "Tony Hawk's Project 8 (2006-11-7, X360 - Final)";
    private const string ProvingGroundX360 = "Tony Hawk's Proving Ground (2007-8-30, X360 - Final)";
    private const string P8Ps3 = "Tony Hawk's Project 8 (2006-10-5, PS3 - Final)";
    private const string PgPs3 = "Tony Hawk's Proving Ground (2007-8-31, PS3 - Final)";

    /// <summary>
    ///     Both games ship a cutscene of this name in dozens of pak directories, so
    ///     these tests address one asset by its full relative path. Looking it up by
    ///     bare filename resolves to whichever copy the index happened to keep last,
    ///     which differs between the two builds and silently compares two unrelated
    ///     scenes.
    /// </summary>
    private const string SharedCutscene =
        @"DATA\COMPRESSED\CUTSCENES\bam_mugging_main.pak\cutscene.skin.xen";

    private string? SampleFile(string buildName, string relativePath)
    {
        if (paths.SampleBuildsDir is null)
            return null;

        var path = Path.Combine(paths.SampleBuildsDir, buildName, relativePath);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    ///     Builds the smallest header the detector should accept: the repeating
    ///     sentinel, a material-list size, and the 0xBABEFACE sentinel with its pad,
    ///     all big-endian.
    /// </summary>
    private static byte[] MinimalHeader(bool bigEndian = true)
    {
        var data = new byte[0x80];
        for (var o = 4; o < 32; o += 4)
        {
            data[o] = 0xFA;
            data[o + 1] = 0xAA;
            data[o + 2] = 0xBA;
            data[o + 3] = 0xCA;
        }

        Write(0x24, 0x20);            // material list size -> afterMaterials 0x40
        Write(0x40, 0xBABEFACE);
        Write(0x44, 0);               // pad -> scene at 0x48
        return data;

        void Write(int offset, uint value)
        {
            var span = data.AsSpan(offset);
            if (bigEndian)
                BinaryPrimitives.WriteUInt32BigEndian(span, value);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        }
    }

    [Fact]
    public void IsNextGenScene_RequiresTheSentinelAndABigEndianHeader()
    {
        Assert.False(NextGenSceneFile.IsNextGenScene(new byte[0x80]));

        var data = MinimalHeader();
        Assert.True(NextGenSceneFile.IsNextGenScene(data));

        // One word short of seven copies is not the sentinel.
        var broken = MinimalHeader();
        broken[28] = 0;
        Assert.False(NextGenSceneFile.IsNextGenScene(broken));
    }

    /// <summary>
    ///     The sentinel by itself does NOT mean next-gen. Every THAW <b>PC</b> scene
    ///     file carries the same repeating word, so detecting on it alone claimed all
    ///     723 of them and broke a corpus that had been converting for months. Byte
    ///     order is what actually separates the platforms, so a little-endian header
    ///     under the same sentinel must be refused.
    /// </summary>
    [Fact]
    public void IsNextGenScene_RejectsALittleEndianHeaderUnderTheSameSentinel()
    {
        Assert.False(NextGenSceneFile.IsNextGenScene(MinimalHeader(bigEndian: false)));
    }

    [CorpusFact]
    public void IsNextGenScene_ClaimsNoThawPcSceneFile()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = paths
            .FindSampleFiles("Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "*.wpc")
            .Where(f => MeshTypeDetector.DetectByName(Path.GetFileName(f)).Kind == MeshFileKind.XbxScene)
            .ToArray();
        Assert.SkipWhen(files.Length == 0, "THAW PC scene files not present");

        // Every one of these carries the FAAABACA sentinel; none is a next-gen scene.
        Assert.All(files, f => Assert.False(
            NextGenSceneFile.IsNextGenScene(File.ReadAllBytes(f)),
            $"{Path.GetFileName(f)} was claimed as a next-gen scene"));
    }

    [Theory]
    [InlineData("a.skin.xen")]
    [InlineData("a.mdl.xen")]
    [InlineData("a.scn.xen")]
    [InlineData("a.skin.ps3")]
    [InlineData("a.mdl.ps3")]
    [InlineData("a.scn.ps3")]
    public void NextGenSuffixes_RouteToTheSceneFamily(string fileName)
    {
        var route = MeshTypeDetector.DetectByName(fileName);
        Assert.Equal(MeshFileKind.XbxScene, route.Kind);
    }

    [CorpusFact]
    public void BaseballBat_MatchesItsDeclaredBoundingBox()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = paths.FindSampleFile(ThawX360, "baseball_bat.mdl.mdl.xen");
        Assert.SkipWhen(path == null, "THAW X360 cutscene models not present");

        var scene = NextGenSceneFile.Parse(File.ReadAllBytes(path!));
        Assert.Equal(107, scene.TotalVertices);
        Assert.Equal(152, scene.TotalTriangles);

        // The file states this box itself; a wrong base, stride or component
        // format cannot reproduce all six faces.
        var sector = Assert.Single(scene.Sectors);
        var xs = sector.Meshes.SelectMany(m => m.Vertices).ToArray();
        Assert.Equal(-1.5625f, xs.Min(v => v.Position.X), 4);
        Assert.Equal(1.5625f, xs.Max(v => v.Position.X), 4);
        Assert.Equal(0.4375f, xs.Min(v => v.Position.Z), 4);
        Assert.Equal(35.6875f, xs.Max(v => v.Position.Z), 4);
    }

    /// <summary>
    ///     The same asset through two unrelated readers. Vertex ORDER differs — each
    ///     console's pipeline reorders for its own vertex cache — so positions are
    ///     compared as multisets.
    /// </summary>
    [CorpusFact]
    public void BaseballBat_AgreesWithItsGameCubeTwin()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var xen = paths.FindSampleFile(ThawX360, "baseball_bat.mdl.mdl.xen");
        var ngc = paths.FindSampleFile(ThawGc, "baseball_bat.mdl.mdl.ngc");
        Assert.SkipWhen(xen == null || ngc == null, "Cross-platform baseball_bat pair not present");

        var fromXenon = NextGenSceneFile.Parse(File.ReadAllBytes(xen!));
        var fromCube = NgcSceneFile.Parse(File.ReadAllBytes(ngc!));

        Assert.Equal(fromCube.TotalVertices, fromXenon.TotalVertices);
        Assert.Equal(fromCube.TotalTriangles, fromXenon.TotalTriangles);

        static IEnumerable<(int X, int Y, int Z)> Positions(global::NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene s)
        {
            return s.Sectors
                .SelectMany(sec => sec.Meshes)
                .SelectMany(m => m.Vertices)
                .Select(v => ((int)MathF.Round(v.Position.X * 10000f),
                    (int)MathF.Round(v.Position.Y * 10000f),
                    (int)MathF.Round(v.Position.Z * 10000f)))
                .OrderBy(p => p.Item1).ThenBy(p => p.Item2).ThenBy(p => p.Item3);
        }

        Assert.Equal(Positions(fromCube), Positions(fromXenon));
    }

    /// <summary>
    ///     Project 8 is a later revision of the same container: batched vertex
    ///     chains and an index block the record states at +0x5C. Each mesh's own
    ///     bounding sphere is the oracle — a wrong batch walk, stride or index
    ///     block cannot reproduce the declared radius.
    /// </summary>
    [CorpusFact]
    public void ProjectEight_DecodesEveryMeshAgainstItsBoundingSphere()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = SampleFile(P8X360, SharedCutscene);
        Assert.SkipWhen(path == null, "Project 8 cutscene scene not present");

        var scene = NextGenSceneFile.Parse(File.ReadAllBytes(path!));
        var meshes = scene.Sectors.SelectMany(s => s.Meshes).ToArray();
        Assert.Equal(6, meshes.Length);

        foreach (var mesh in meshes)
        {
            Assert.NotEmpty(mesh.Vertices);
            Assert.NotEmpty(mesh.FaceIndices);

            var furthest = mesh.Vertices.Max(v => (v.Position - mesh.BsphereCenter).Length());
            Assert.Equal(1.0f, furthest / mesh.BsphereRadius, 2);

            // Batched meshes address the whole concatenated chain, so every index
            // must land inside it.
            Assert.True(mesh.FaceIndices.Max() < mesh.Vertices.Length);
        }

        // The batched (skinned) meshes are the ones a contiguous read gets wrong.
        Assert.Contains(meshes, m => m.Vertices.Length == 1256);
        Assert.Equal(1892, meshes.Sum(m => m.FaceIndices.Length / 3));
    }

    /// <summary>
    ///     Proving Ground is the same later revision, read by the same code. It was
    ///     briefly thought to store topology behind an unknown marker, because an
    ///     earlier pass located Project 8's indices by searching for a
    ///     <c>FACEF001 FACEF000</c> pair and Proving Ground contains no such pair.
    ///     Both builds state the index block at sMesh +0x5C; those words are
    ///     unresolved-pointer filler that the two builds park in different slots.
    /// </summary>
    [CorpusFact]
    public void ProvingGround_DecodesEveryMeshAgainstItsBoundingSphere()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = SampleFile(ProvingGroundX360, SharedCutscene);
        Assert.SkipWhen(path == null, "Proving Ground cutscene scene not present");

        var scene = NextGenSceneFile.Parse(File.ReadAllBytes(path!));
        var meshes = scene.Sectors.SelectMany(s => s.Meshes).ToArray();
        Assert.Equal(6, meshes.Length);

        foreach (var mesh in meshes)
        {
            Assert.NotEmpty(mesh.Vertices);
            Assert.NotEmpty(mesh.FaceIndices);

            var furthest = mesh.Vertices.Max(v => (v.Position - mesh.BsphereCenter).Length());
            Assert.Equal(1.0f, furthest / mesh.BsphereRadius, 2);

            // A strip that addresses its whole buffer is the second oracle: the
            // highest index a correctly located block uses is the last vertex.
            Assert.Equal(mesh.Vertices.Length - 1, mesh.FaceIndices.Max());
        }

        Assert.Contains(meshes, m => m.Vertices.Length == 1256);
        Assert.Equal(1892, meshes.Sum(m => m.FaceIndices.Length / 3));
    }

    /// <summary>
    ///     The descriptor path reads the file's own normals rather than deriving
    ///     them. The vertex carries three 11/11/10 packed unit vectors; the one at
    ///     +0x10 is the normal, and its agreement with the facet normal of the
    ///     triangles we emit is a second, independent result — it says the strip
    ///     triangulation winds the way the authored normals expect, which nothing
    ///     else in this format could tell us.
    /// </summary>
    [CorpusFact]
    public void ProvingGround_ReadsAuthoredNormalsThatAgreeWithOurWinding()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = SampleFile(ProvingGroundX360, SharedCutscene);
        Assert.SkipWhen(path == null, "Proving Ground cutscene scene not present");

        var meshes = NextGenSceneFile.Parse(File.ReadAllBytes(path!))
            .Sectors.SelectMany(s => s.Meshes).ToArray();

        var dots = new List<float>();
        foreach (var mesh in meshes)
        {
            Assert.All(mesh.Vertices, v =>
            {
                Assert.True(v.HasNormal);
                Assert.Equal(1.0f, v.Normal.Length(), 2);
            });

            for (var i = 0; i + 2 < mesh.FaceIndices.Length; i += 3)
            {
                var a = mesh.Vertices[mesh.FaceIndices[i]];
                var b = mesh.Vertices[mesh.FaceIndices[i + 1]];
                var c = mesh.Vertices[mesh.FaceIndices[i + 2]];
                var facet = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
                if (facet.LengthSquared() <= 0)
                    continue;

                dots.Add(Vector3.Dot(a.Normal, Vector3.Normalize(facet)));
            }
        }

        // Strongly positive, not merely non-negative: a reversed winding would give
        // about -0.9, and an unrelated field would hover around zero.
        Assert.NotEmpty(dots);
        Assert.True(dots.Average() > 0.8f, $"mean dot with facet normal was {dots.Average()}");
    }

    /// <summary>
    ///     Level scenes use the OTHER vertex layout. A record whose +0x60 is
    ///     0xFFFFFFFF has no <c>CAFEBAB4</c> descriptor at all: the whole vertex sits
    ///     in the +0x40 block at a per-mesh stride the record states as
    ///     <c>+0x4C / vertexCount</c>. Every <c>.mdl</c> and <c>.scn</c> is built this
    ///     way, which is why the entire level population read as "carries no buffer
    ///     descriptors" until the branch existed.
    /// </summary>
    [CorpusFact]
    public void ProvingGroundLevel_DecodesItsDescriptorLessVertexStreams()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = SampleFile(ProvingGroundX360,
            @"DATA\COMPRESSED\ZONES\z_bw_bridge.pak\19CD9F68_009F6000.scn.xen");
        Assert.SkipWhen(path == null, "Proving Ground bridge zone not present");

        var scene = NextGenSceneFile.Parse(File.ReadAllBytes(path!));
        var meshes = scene.Sectors.SelectMany(s => s.Meshes).ToArray();

        // The scene states 698 meshes, every one descriptor-less, at strides from
        // 24 to 52 bytes — a fixed stride cannot read this file at all.
        Assert.Equal(698, meshes.Length);
        Assert.All(meshes, mesh =>
        {
            var furthest = mesh.Vertices.Max(v => (v.Position - mesh.BsphereCenter).Length());
            Assert.Equal(1.0f, furthest / mesh.BsphereRadius, 2);
            Assert.Equal(mesh.Vertices.Length - 1, mesh.FaceIndices.Max());
        });
    }

    /// <summary>
    ///     PlayStation 3 keeps the attribute stream and the index buffer in a sibling
    ///     VRAM file, addressed by the SAME record pointers as raw offsets from byte
    ///     zero — no scene base, no block header. The same cutscene ships on Xbox 360,
    ///     so the decode is checked against that twin rather than against itself.
    /// </summary>
    [CorpusFact]
    public void ProjectEightPs3_DecodesThroughItsVramCompanion()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var scenePath = SampleFile(P8Ps3,
            @"PS3_GAME\USRDIR\DATA\MODELS\ANIMALS\anl_pigeon.skin.ps3");
        var vramPath = SampleFile(P8Ps3,
            @"PS3_GAME\USRDIR\DATA\MODELS\ANIMALS\anl_pigeon.skiv.ps3");
        // The copy under MODELS\ is one of the whole-file-compressed duplicates; the
        // pak-extracted one is the plain scene.
        var xenonPath = SampleFile(P8X360,
            @"DATA\COMPRESSED\PAK\modelviewer.pak\anl_pigeon.skin.xen");
        Assert.SkipWhen(scenePath == null || vramPath == null || xenonPath == null,
            "Project 8 cross-platform pigeon not present");

        // The companion name is derived, not hard-coded, so the rule is exercised.
        Assert.Equal(Path.GetFileName(vramPath),
            NextGenSceneFile.GetVramCompanionName(Path.GetFileName(scenePath!)));

        var ps3 = NextGenSceneFile.Parse(
                File.ReadAllBytes(scenePath!), File.ReadAllBytes(vramPath!))
            .Sectors.SelectMany(s => s.Meshes).ToArray();
        var xenon = NextGenSceneFile.Parse(File.ReadAllBytes(xenonPath!))
            .Sectors.SelectMany(s => s.Meshes).ToArray();

        Assert.Equal(xenon.Length, ps3.Length);
        for (var i = 0; i < ps3.Length; i++)
        {
            Assert.Equal(
                xenon[i].Vertices.Select(v => v.Position),
                ps3[i].Vertices.Select(v => v.Position));
            Assert.NotEmpty(ps3[i].FaceIndices);
            Assert.Equal(ps3[i].Vertices.Length - 1, ps3[i].FaceIndices.Max());
        }
    }

    /// <summary>
    ///     Proving Ground's PS3 build decodes its positions but not its topology, and
    ///     is declined rather than exported with scrambled triangles. Its geometry
    ///     passes both the glTF validator and the bounding-sphere gate — the sphere is
    ///     order-insensitive — so nothing short of an explicit refusal would catch it.
    /// </summary>
    [CorpusFact]
    public void ProvingGroundPs3_IsDeclinedForItsUnreadableTopology()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var scenePath = SampleFile(PgPs3,
            @"PS3_GAME\USRDIR\DATA\COMPRESSED\PS3\CUTSCENES\BAM_MUGGING_MAIN.PAK\cutscene.skin.ps3");
        var vramPath = SampleFile(PgPs3,
            @"PS3_GAME\USRDIR\DATA\COMPRESSED\PS3\CUTSCENES\BAM_MUGGING_MAIN.PAK\cutscene.skiv.ps3");
        Assert.SkipWhen(scenePath == null || vramPath == null,
            "Proving Ground PS3 cutscene pair not present");

        var ex = Assert.Throws<InvalidDataException>(() => NextGenSceneFile.Parse(
            File.ReadAllBytes(scenePath!), File.ReadAllBytes(vramPath!)));
        Assert.Contains("Proving Ground", ex.Message);
    }

    /// <summary>
    ///     This cutscene ships in both games, and the two builds are a year and a
    ///     revision apart — so agreement on every decoded position and index is a
    ///     cross-build check the reader cannot pass by accident. It is deliberately
    ///     narrow: only these six meshes survive a same-counts comparison across the
    ///     two builds, the rest of the shared-path assets having been re-authored.
    /// </summary>
    [CorpusFact]
    public void ProvingGround_AgreesWithItsProjectEightTwin()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var fromEight = SampleFile(P8X360, SharedCutscene);
        var fromGround = SampleFile(ProvingGroundX360, SharedCutscene);
        Assert.SkipWhen(fromEight == null || fromGround == null, "Cross-build cutscene pair not present");

        var eight = NextGenSceneFile.Parse(File.ReadAllBytes(fromEight!))
            .Sectors.SelectMany(s => s.Meshes).ToArray();
        var ground = NextGenSceneFile.Parse(File.ReadAllBytes(fromGround!))
            .Sectors.SelectMany(s => s.Meshes).ToArray();

        Assert.Equal(eight.Length, ground.Length);
        for (var i = 0; i < eight.Length; i++)
        {
            Assert.Equal(eight[i].Vertices.Length, ground[i].Vertices.Length);
            Assert.Equal(eight[i].FaceIndices, ground[i].FaceIndices);
            Assert.Equal(
                eight[i].Vertices.Select(v => v.Position),
                ground[i].Vertices.Select(v => v.Position));
        }
    }
}
