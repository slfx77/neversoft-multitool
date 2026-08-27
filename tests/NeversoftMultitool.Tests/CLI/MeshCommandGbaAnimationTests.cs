using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Gba;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.CLI;

/// <summary>
///     Pins the <c>mesh</c> command's GBA animation options: opt-in, exact clip
///     selection, and — the important one — that a static export stays
///     byte-identical whether or not the options exist and whether or not a
///     requested selection turns out to be exportable.
/// </summary>
public sealed class MeshCommandGbaAnimationTests(TestPaths paths)
{
    private const string GbaBuild = "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)";
    private const string GbaRomName = "Tony Hawk's Pro Skater 2 (USA, Europe).gba";

    /// <summary>
    ///     Morph weights address every target of the mesh, so bundling all 217
    ///     clips into one document is not expressible — each clip is its own
    ///     file, named after the clip.
    /// </summary>
    [CorpusFact]
    public void MeshCommand_GbaAnimationsFlag_IsOptInAndWritesOneFilePerClip()
    {
        using var temp = new TempDirectory();
        var characterPath = StageCharacter(temp);

        var staticOutput = Path.Combine(temp.Path, "static");
        Assert.Equal(0, MeshCommand.Create()
            .Parse([characterPath, "--output", staticOutput]).Invoke());
        var animatedOutput = Path.Combine(temp.Path, "animated");
        Assert.Equal(0, MeshCommand.Create()
            .Parse([characterPath, "--output", animatedOutput, "--gba-animations"]).Invoke());

        var staticModel = ReadOnlyGlb(staticOutput);
        Assert.Empty(staticModel.LogicalAnimations);
        Assert.All(staticModel.LogicalMeshes.SelectMany(m => m.Primitives),
            p => Assert.Equal(0, p.MorphTargetsCount));

        var files = Directory.GetFiles(animatedOutput, "*.glb", SearchOption.AllDirectories);
        Assert.Equal(217, files.Length);
        Assert.Contains(files, f => Path.GetFileName(f) == "13_character__Kickflip__20.glb");

        // Each animating clip carries exactly its own clip as a morph-weights
        // track and no skin — this model has no skeleton to invent one from.
        // The other 51 are the port's static placeholders: every one of their
        // frames IS the neutral pose, so they export as that pose with nothing
        // to animate, byte-identical to the plain static export.
        var animating = 0;
        var staticPose = 0;
        var staticHash = SingleGlbHash(staticOutput);
        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            var model = ModelRoot.ReadGLB(stream);
            Assert.Empty(model.LogicalSkins);
            if (model.LogicalAnimations.Count == 1)
            {
                Assert.All(model.LogicalMeshes.SelectMany(m => m.Primitives),
                    p => Assert.True(p.MorphTargetsCount > 0));
                animating++;
                continue;
            }

            Assert.Empty(model.LogicalAnimations);
            Assert.Equal(staticHash, Hash(file));
            staticPose++;
        }

        Assert.Equal(166, animating);
        Assert.Equal(51, staticPose);
    }

    [CorpusFact]
    public void MeshCommand_SingleGbaAnimationOption_SelectsExactlyThoseClips()
    {
        using var temp = new TempDirectory();
        var characterPath = StageCharacter(temp);

        var one = Path.Combine(temp.Path, "one");
        Assert.Equal(0, MeshCommand.Create()
            .Parse([characterPath, "--output", one, "--gba-animation", "3"]).Invoke());
        Assert.Equal("anim_3", Assert.Single(ReadOnlyGlb(one).LogicalAnimations).Name);

        var two = Path.Combine(temp.Path, "two");
        Assert.Equal(0, MeshCommand.Create()
            .Parse([characterPath, "--output", two, "--gba-animation", "0", "2"]).Invoke());
        Assert.Equal(
            ["13_character__anim_0.glb", "13_character__anim_2.glb"],
            Directory.GetFiles(two, "*.glb").Select(Path.GetFileName).Order().ToArray());
    }

    [CorpusFact]
    public void MeshCommand_DefaultGbaPath_IsByteIdenticalAndFailsClosed()
    {
        using var temp = new TempDirectory();
        var characterPath = StageCharacter(temp);

        // Bare export, an out-of-range clip, and one of the four authored-empty
        // clips must all produce the identical static file: the options cannot
        // perturb an export by existing, and an unexportable selection falls back
        // rather than emitting a degraded document.
        var bare = Path.Combine(temp.Path, "bare");
        var outOfRange = Path.Combine(temp.Path, "out-of-range");
        var emptyClip = Path.Combine(temp.Path, "empty-clip");
        Assert.Equal(0, MeshCommand.Create().Parse([characterPath, "--output", bare]).Invoke());
        Assert.Equal(0, MeshCommand.Create()
            .Parse([characterPath, "--output", outOfRange, "--gba-animation", "999"]).Invoke());
        Assert.Equal(0, MeshCommand.Create()
            .Parse([characterPath, "--output", emptyClip, "--gba-animation", "65"]).Invoke());

        var expected = SingleGlbHash(bare);
        Assert.Equal(expected, SingleGlbHash(outOfRange));
        Assert.Equal(expected, SingleGlbHash(emptyClip));
        Assert.Empty(ReadOnlyGlb(bare).LogicalAnimations);
    }

    /// <summary>
    ///     Materialises one carved character record beside the ROM companion its
    ///     mesh and colours resolve through, and returns the record's path.
    /// </summary>
    private string StageCharacter(TempDirectory temp, int character = 13)
    {
        var romPath = paths.FindSampleFile(GbaBuild, GbaRomName);
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath);
        var model = GbaSkaterModel.TryLocate(rom);
        Assert.NotNull(model);

        var directory = Path.Combine(temp.Path, "models");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, GbaLevelCarver.RomEntryName), rom);

        var record = rom.AsSpan(
            model.CharacterTableOffset + character * GbaSkaterModel.CharacterRecordSize,
            GbaSkaterModel.CharacterRecordSize).ToArray();
        var path = Path.Combine(directory, $"{character:D2}_character.chr.gba");
        File.WriteAllBytes(path, record);
        return path;
    }

    private static string SingleGlbHash(string outputDirectory)
    {
        return Hash(Assert.Single(Directory.GetFiles(
            outputDirectory, "*.glb", SearchOption.AllDirectories)));
    }

    private static string Hash(string path)
    {
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static ModelRoot ReadOnlyGlb(string outputDirectory)
    {
        var path = Assert.Single(Directory.GetFiles(
            outputDirectory, "*.glb", SearchOption.AllDirectories));
        using var stream = File.OpenRead(path);
        return ModelRoot.ReadGLB(stream);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-gba-mesh-command-{Guid.NewGuid():N}");
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
