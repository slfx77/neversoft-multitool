using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the THPS3 GBA 3D rider: the content-located directory, the three
///     closures that fix it (self-pointer, face bank ending at the pose bank,
///     the stride identity), the clip table read as tick ranges into the remap
///     that fills the region after the bank, and the exports. Every number was
///     derived by closure arithmetic during the decode (see GbaThps3RiderModel) —
///     a change means the locate regressed, not that the ROM did.
/// </summary>
public sealed class GbaThps3RiderModelTests(TestPaths paths)
{
    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)", "Tony Hawk's Pro Skater 3 (USA, Europe).gba");

    [Fact]
    public void LocatesTheRiderDirectoryByContent()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS3 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);

        var model = GbaThps3RiderModel.TryLocate(rom);
        Assert.NotNull(model);
        Assert.Equal(0x161CA4, model.DirectoryOffset);
        Assert.Equal(6, model.Records.Count);

        // {stride, part0Verts, part1Verts, part0Faces, part1Faces, animated, lz77}
        // per record — the stride identity is exact on all six.
        (int, int, int, int, int, bool, bool)[] expected =
        [
            (360, 115, 24, 215, 28, true, false),
            (424, 115, 24, 215, 28, false, true),
            (412, 112, 24, 207, 28, false, true),
            (388, 103, 24, 202, 28, false, true),
            (328, 108, 0, 196, 0, false, true),
            (28, 8, 0, 12, 0, false, true)
        ];
        for (var i = 0; i < expected.Length; i++)
        {
            var r = model.Records[i];
            Assert.Equal(expected[i],
                (r.FrameStride, r.Part0Verts, r.Part1Verts, r.Part0Faces, r.Part1Faces, r.Animated, r.Compressed));
            Assert.Equal(r.MeshOffset + GbaThps3RiderModel.MeshHeaderSize + 12 * r.FaceCount, r.BankStart);
        }

        // The rider's raw bank: 72 deck bytes then 5,024 frames of 360, exactly.
        Assert.Equal(0x169B80, model.Rider.MeshOffset);
        Assert.Equal(0x16A6F0, model.DeckVertexOffset);
        Assert.Equal(0x16A6F0 + 72, model.FramePoolOffset);
        Assert.Equal(5024, model.FrameCount);

        // The remap after the bank closes against the clip table's furthest tick.
        Assert.Equal(0x324038, model.TickTableOffset);
        Assert.Equal(8507, model.TickCount);
        Assert.Equal(0x163124, model.Rider.ClipTableOffset);
        Assert.Equal(239, model.ClipCount);
        Assert.Equal(7, GbaThps3RiderModel.ReadClips(rom, model).Count(c => c.TickCount == 0));
    }

    [Fact]
    public void FacesIndexBothPartsTightly()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS3 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaThps3RiderModel.TryLocate(rom)!;

        var faces = GbaThps3RiderModel.ReadFaces(rom, model);
        Assert.Equal(243, faces.Count);
        var rider = faces.Where(f => f.Part == 0).SelectMany(f => new[] { f.V0, f.V1, f.V2 }).ToList();
        var deck = faces.Where(f => f.Part == 1).SelectMany(f => new[] { f.V0, f.V1, f.V2 }).ToList();
        Assert.Equal(215 * 3, rider.Count);
        Assert.Equal((0, 114), (rider.Min(), rider.Max()));
        Assert.Equal((115, 138), (deck.Min(), deck.Max()));

        // The first face, verbatim: indices, three 6.2 texel pairs, material, flag.
        var first = faces[0];
        Assert.Equal((2, 3, 4), (first.V0, first.V1, first.V2));
        Assert.Equal(new GbaThps3RiderModel.TexCoord(63, 108), first.T0);
        Assert.Equal(new GbaThps3RiderModel.TexCoord(65, 132), first.T1);
        Assert.Equal(new GbaThps3RiderModel.TexCoord(106, 108), first.T2);
        Assert.Equal((2, 1), (first.Material, first.Flag));

        // Texel bytes stay inside the 64-texel page (6.2 fixed point: max 63.75).
        Assert.All(faces, f => Assert.All(new[] { f.T0, f.T1, f.T2 },
            t => Assert.True(t.U < 256 && t.V < 256)));
        Assert.Equal(14, faces.Select(f => (int)f.Material).Distinct().Count());
        Assert.Equal(22, faces.Max(f => f.Material));
        Assert.All(faces, f => Assert.InRange(f.Flag, 1, 8));
    }

    /// <summary>
    ///     The reading that resolved the recorded contradiction: clip 13 is
    ///     (6322, 23) — past the 5,024-frame bank as a FRAME range, in range as a
    ///     TICK range into the remap, where it names frames 3899..3920 then 1275.
    /// </summary>
    [Fact]
    public void EveryClipTickRemapsInsideThePool()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS3 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaThps3RiderModel.TryLocate(rom)!;

        var clips = GbaThps3RiderModel.ReadClips(rom, model);
        Assert.Equal(239, clips.Count);
        Assert.Equal((6322, 23), (clips[13].TickStart, clips[13].TickCount));
        var frames = GbaThps3RiderModel.ClipFrames(rom, model, clips[13]);
        Assert.Equal(Enumerable.Range(3899, 22).Append(1275), frames);

        foreach (var clip in clips)
        foreach (var frame in GbaThps3RiderModel.ClipFrames(rom, model, clip))
            Assert.InRange(frame, 0, model.FrameCount - 1);

        // Holds are real: clip 0 plays six frames for two ticks each.
        Assert.Equal([1, 1, 196, 196, 197, 197, 198, 198, 199, 199, 200, 200],
            GbaThps3RiderModel.ClipFrames(rom, model, clips[0]));
    }

    /// <summary>
    ///     Frame 686 is the pose the retained emulator capture was drawing; its
    ///     transformed deck copy in RAM equals the stored deck plus header bytes
    ///     4–6 on all 24 vertices, which is what makes the translation proven and
    ///     the other header bytes not.
    /// </summary>
    [Fact]
    public void FrameHeaderDeckTranslationPosesTheDeck()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS3 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaThps3RiderModel.TryLocate(rom)!;

        var header = GbaThps3RiderModel.ReadFrameHeader(rom, model, 686);
        Assert.Equal(((sbyte)-3, (sbyte)0, (sbyte)33), header.Anchor);
        Assert.Equal(((sbyte)0, (sbyte)0, (sbyte)-9), header.DeckTranslation);
        Assert.Equal(((sbyte)0, (sbyte)1, (sbyte)-2), header.Unknown);

        var deck = GbaThps3RiderModel.ReadDeckVertices(rom, model);
        Assert.Equal(24, deck.Length);
        Assert.Equal([0, -24, 2], deck[0]);
        Assert.Equal([-6, -17, 0], deck[1]);

        var pose = GbaThps3RiderGeometryWriter.PoseOf(rom, model, 686);
        Assert.Equal(139, pose.Length);
        Assert.Equal(GbaThps3RiderGeometryWriter.ToGlb(0, -24, 2 - 9), pose[115]);

        // The rider stands ~97 units tall in frame 0 (feet at -12, head at 85).
        var frame0 = GbaThps3RiderModel.ReadFrameVertices(rom, model, 0);
        Assert.Equal(115, frame0.Length);
        Assert.Equal((-12, 85), (frame0.Min(v => v[2]), frame0.Max(v => v[2])));
    }

    [Fact]
    public void StaticRecordsDecompressToWholeFrames()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS3 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaThps3RiderModel.TryLocate(rom)!;

        int[] expectedFrames = [25, 25, 28, 1, 1];
        for (var i = 1; i < model.Records.Count; i++)
        {
            var record = GbaThps3RiderModel.TryReadStaticRecord(rom, model.Records[i]);
            Assert.NotNull(record);
            Assert.Equal(expectedFrames[i - 1], record.Value.FrameCount);
            Assert.Equal(model.Records[i].Part0Verts, record.Value.Part0.Length);
            Assert.Equal(model.Records[i].Part1Verts, record.Value.Part1.Length);
        }

        Assert.Null(GbaThps3RiderModel.TryReadStaticRecord(rom, model.Rider));
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba")]
    [InlineData("Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)", "Tony Hawk's Pro Skater 4 (USA, Europe).gba")]
    [InlineData("Tony Hawk's Underground (2003-10-27, GBA - Final)", "Tony Hawk's Underground (USA, Europe).gba")]
    [InlineData("Tony Hawk's Underground 2 (2004-10-4, GBA - Final)", "Tony Hawk's Underground 2 (USA, Europe).gba")]
    [InlineData("Tony Hawk's American Sk8land (2005-10-18, GBA - Final)", "Tony Hawk's American Sk8land (USA).gba")]
    [InlineData("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)", "Tony Hawk's Downhill Jam (USA).gba")]
    public void OtherCartsDoNotClaimTheThps3RiderContainer(string build, string file)
    {
        var path = paths.FindSampleFile(build, file);
        Assert.SkipWhen(path == null, $"{build} ROM sample not available");
        Assert.Null(GbaThps3RiderModel.TryLocate(File.ReadAllBytes(path!)));
    }

    [CorpusFact]
    public void ConvertsTheRiderWithTextureCoordinatesAndOneClip()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS3 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var model = GbaThps3RiderModel.TryLocate(rom)!;
        var record = rom.AsSpan(model.DirectoryOffset, GbaThps3RiderModel.DirectoryRecordSize).ToArray();
        var native = new GbaThps3RiderNativeSource(record, rom);

        var document = ModelDocument.CreateNative("00_rider", ModelSourceKind.GbaModel, native);
        GbaThps3RiderGeometryWriter.Populate(document, native);
        Assert.Equal(243, document.TriangleCount);
        Assert.Equal(14, document.Materials.Count); // one per material byte
        Assert.All(document.Materials, m => Assert.EndsWith("_debug", m.Name));
        Assert.All(document.Meshes.SelectMany(m => m.Primitives).SelectMany(p => p.Vertices), v =>
        {
            Assert.InRange(v.TexCoord.X, 0f, 1f);
            Assert.InRange(v.TexCoord.Y, 0f, 1f);
        });
        Assert.Empty(document.Animations);

        // Clip 13: 23 ticks naming 23 distinct frames, none of them the base pose.
        var animated = ModelDocument.CreateNative("00_rider", ModelSourceKind.GbaModel, native);
        Assert.True(GbaThps3RiderAnimatedWriter.TryPopulate(animated, native, 13));
        var animation = Assert.Single(animated.Animations);
        Assert.Equal("anim_13", animation.Name);
        Assert.Equal(23, animation.MorphChannel!.Times.Length);
        Assert.Equal(23, animation.MorphChannel.TargetCount);
        Assert.Equal(243, animated.TriangleCount);

        // Authored-empty and out-of-range clips fail closed, adding nothing.
        var empty = ModelDocument.CreateNative("00_rider", ModelSourceKind.GbaModel, native);
        Assert.False(GbaThps3RiderAnimatedWriter.TryPopulate(empty, native, 62));
        Assert.False(GbaThps3RiderAnimatedWriter.TryPopulate(empty, native, 239));
        Assert.Empty(empty.Meshes);
    }

    [CorpusFact]
    public void CarvedRiderRecordConvertsThroughTheMeshParser()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS3 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);

        using var temp = new TempDirectory();
        GbaLevelCarver.ExtractFiles(romPath!, temp.Path);
        var record = Path.Combine(temp.Path, "models", "00_rider.chr.gba");
        Assert.True(File.Exists(record));
        Assert.Equal(GbaThps3RiderModel.DirectoryRecordSize, new FileInfo(record).Length);

        var parser = new MeshModelParser();
        var animated = parser.Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(record),
            FileName = "00_rider.chr.gba",
            OutputStem = "00_rider",
            SourceKind = ModelSourceKind.GbaModel,
            GbaAnimationIndices = [13]
        });
        Assert.Single(animated.Animations);

        // The shared clip catalogue lists this cart's rider too.
        var clips = GbaRiderClips.TryList(rom);
        Assert.NotNull(clips);
        Assert.Equal(239, clips.Count);
        Assert.Equal(139, GbaRiderClips.TryGetVertexCount(rom));
        Assert.Equal("anim_13", GbaRiderClips.ExportName(rom, 13));
    }

    /// <summary>
    ///     The seam the Meshes &amp; Characters tab opens a raw ROM through: the
    ///     carve happens in memory, and the rider entry is the exact record length
    ///     the GUI scanner gates on (App/** is outside the test project, so this
    ///     Core seam is the only place the tab's open path can be pinned).
    /// </summary>
    [CorpusFact]
    public void ArchiveFileSystemOpensTheRomWithItsRider()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS3 GBA ROM sample not available");

        using var fs = ArchiveFileSystem.TryOpen(romPath!);
        Assert.NotNull(fs);
        Assert.Equal(12, fs.Entries.Count); // 9 levels + the rider + two ROM companions

        var rider = fs.FindByName("00_rider.chr.gba");
        Assert.NotNull(rider);
        Assert.Equal(GbaThps3RiderModel.DirectoryRecordSize, fs.ReadEntry(rider).Length);
        Assert.Equal("models", rider.Directory);

        var level = fs.FindByName("0_level.lvl.gba");
        Assert.NotNull(level);
        Assert.Equal(GbaThps3LevelArt.LevelRecordStride, fs.ReadEntry(level).Length);

        // Both record kinds route by NAME, like an N64 bundle — a plain .gba is an
        // archive, never a mesh.
        Assert.Equal(MeshFileKind.GbaModel, MeshTypeDetector.DetectByName("00_rider.chr.gba").Kind);
        Assert.Equal(MeshFileKind.GbaLevel, MeshTypeDetector.DetectByName("0_level.lvl.gba").Kind);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-gba-thps3-rider-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
