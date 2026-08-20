using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.CLI;

public sealed class MeshCommandN64AnimationTests(TestPaths paths)
{
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string Thps2RomName = "Tony Hawk's Pro Skater 2 (USA).z64";
    private const string SpiderN64Build = "Spider-Man (2000-11-21, N64 - Final)";
    private const string SpiderRomName = "Spider-Man (USA).z64";

    [CorpusFact]
    public void MeshCommand_N64AnimationFlag_IsOptInAndWiresTheWholeEmbeddedBank()
    {
        var romPath = paths.FindSampleFile(Thps2N64Build, Thps2RomName);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");

        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = N64Bundles.FindBundle(backend, "045");
        var archiveSource = new ArchiveAssetSource(backend, entry);

        using var temp = new TempDirectory();
        var bundleDirectory = Path.Combine(temp.Path, "models", "045");
        var group2Directory = Path.Combine(temp.Path, "group2");
        Directory.CreateDirectory(bundleDirectory);
        Directory.CreateDirectory(group2Directory);

        var shellPath = Path.Combine(bundleDirectory, entry.Name);
        File.WriteAllBytes(shellPath, archiveSource.ReadBytes());
        File.WriteAllBytes(
            Path.Combine(bundleDirectory, "renderbank-id.bin"),
            Assert.IsType<byte[]>(archiveSource.TryReadCompanion("renderbank-id.bin")));
        var renderBankId = Assert.IsType<uint>(N64ModelCompanions.TryReadRenderBankId(archiveSource));
        File.WriteAllBytes(
            Path.Combine(group2Directory, $"{renderBankId:D3}.bin"),
            Assert.IsType<byte[]>(N64ModelCompanions.TryReadRenderBank(archiveSource)));

        var staticOutput = Path.Combine(temp.Path, "static");
        var staticExitCode = MeshCommand.Create()
            .Parse([shellPath, "--output", staticOutput])
            .Invoke();
        Assert.Equal(0, staticExitCode);

        var animatedOutput = Path.Combine(temp.Path, "animated");
        var animatedExitCode = MeshCommand.Create()
            .Parse([shellPath, "--output", animatedOutput, "--n64-animations"])
            .Invoke();
        Assert.Equal(0, animatedExitCode);

        var staticModel = ReadOnlyGlb(staticOutput);
        Assert.Empty(staticModel.LogicalAnimations);
        Assert.Empty(staticModel.LogicalSkins);

        var animatedModel = ReadOnlyGlb(animatedOutput);
        Assert.Equal(218, animatedModel.LogicalAnimations.Count);
        Assert.NotEmpty(animatedModel.LogicalSkins);
    }

    [CorpusFact]
    public void MeshCommand_N64AnimationFlag_WiresTheWholeDirectMatrixBank()
    {
        var romPath = paths.FindSampleFile(SpiderN64Build, SpiderRomName);
        Assert.SkipWhen(romPath == null, "Spider-Man N64 ROM sample not available");

        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = N64Bundles.FindBundle(backend, "002");
        var archiveSource = new ArchiveAssetSource(backend, entry);

        using var temp = new TempDirectory();
        var bundleDirectory = Path.Combine(temp.Path, "models", "002");
        var group2Directory = Path.Combine(temp.Path, "group2");
        Directory.CreateDirectory(bundleDirectory);
        Directory.CreateDirectory(group2Directory);

        var shellPath = Path.Combine(bundleDirectory, entry.Name);
        File.WriteAllBytes(shellPath, archiveSource.ReadBytes());
        File.WriteAllBytes(
            Path.Combine(bundleDirectory, "renderbank-id.bin"),
            Assert.IsType<byte[]>(archiveSource.TryReadCompanion("renderbank-id.bin")));
        var renderBankId = Assert.IsType<uint>(N64ModelCompanions.TryReadRenderBankId(archiveSource));
        File.WriteAllBytes(
            Path.Combine(group2Directory, $"{renderBankId:D3}.bin"),
            Assert.IsType<byte[]>(N64ModelCompanions.TryReadRenderBank(archiveSource)));

        var staticOutput = Path.Combine(temp.Path, "static");
        Assert.Equal(0, MeshCommand.Create()
            .Parse([shellPath, "--output", staticOutput])
            .Invoke());
        var animatedOutput = Path.Combine(temp.Path, "animated");
        Assert.Equal(0, MeshCommand.Create()
            .Parse([shellPath, "--output", animatedOutput, "--n64-animations"])
            .Invoke());

        var staticModel = ReadOnlyGlb(staticOutput);
        Assert.Empty(staticModel.LogicalAnimations);
        Assert.Empty(staticModel.LogicalSkins);

        var animatedModel = ReadOnlyGlb(animatedOutput);
        Assert.Equal(3, animatedModel.LogicalAnimations.Count);
        Assert.NotEmpty(animatedModel.LogicalSkins);
    }

    [CorpusFact]
    public void MeshCommand_SingleN64AnimationOption_SelectsExactlyThoseSlots()
    {
        // The bank holds three direct clips; --n64-animations takes all of them.
        // Per-slot selection is the point of the option: a character bank can
        // carry hundreds, and the GUI has always been able to pick exact slots.
        using var temp = new TempDirectory();
        var shellPath = StageBundle(SpiderN64Build, SpiderRomName, "002", temp);

        var oneOutput = Path.Combine(temp.Path, "one");
        Assert.Equal(0, MeshCommand.Create()
            .Parse([shellPath, "--output", oneOutput, "--n64-animation", "1"])
            .Invoke());
        var twoOutput = Path.Combine(temp.Path, "two");
        Assert.Equal(0, MeshCommand.Create()
            .Parse([shellPath, "--output", twoOutput, "--n64-animation", "0", "--n64-animation", "2"])
            .Invoke());

        Assert.Equal("anim_1", Assert.Single(ReadOnlyGlb(oneOutput).LogicalAnimations).Name);
        Assert.Equal(
            ["anim_0", "anim_2"],
            ReadOnlyGlb(twoOutput).LogicalAnimations.Select(a => a.Name).Order().ToArray());
    }

    [CorpusFact]
    public void MeshCommand_OutOfRangeN64AnimationIndex_IsSkippedRatherThanFailing()
    {
        // A bad index must not take the neighbouring slots down with it, and it
        // must not fabricate a clip. Slot 002 has exactly three.
        using var temp = new TempDirectory();
        var shellPath = StageBundle(SpiderN64Build, SpiderRomName, "002", temp);

        var output = Path.Combine(temp.Path, "mixed");
        Assert.Equal(0, MeshCommand.Create()
            .Parse([shellPath, "--output", output, "--n64-animation", "0", "--n64-animation", "99"])
            .Invoke());

        Assert.Equal("anim_0", Assert.Single(ReadOnlyGlb(output).LogicalAnimations).Name);
    }

    [CorpusFact]
    public void MeshCommand_OneShot_ChangesOnlyTweenedDirectClipEndings()
    {
        // Slot 010 is the direct (0x2A) family WITH tween flags (slot 0 is
        // tween=4 over 74 frames), so the two end-of-clip branches genuinely
        // differ there. Slot 002's direct clips carry no tween flag, so the
        // flag must be inert for them: that asymmetry is the evidence the flag
        // reaches tween expansion and nothing else.
        using var temp = new TempDirectory();
        var tweenedPath = StageBundle(SpiderN64Build, SpiderRomName, "010", temp, "tweened");
        var untweenedPath = StageBundle(SpiderN64Build, SpiderRomName, "002", temp, "plain");

        var tweenedCycle = Path.Combine(temp.Path, "tweened-cycle");
        var tweenedClamp = Path.Combine(temp.Path, "tweened-clamp");
        Assert.Equal(0, MeshCommand.Create()
            .Parse([tweenedPath, "--output", tweenedCycle, "--n64-animation", "0"]).Invoke());
        Assert.Equal(0, MeshCommand.Create()
            .Parse([tweenedPath, "--output", tweenedClamp, "--n64-animation", "0", "--one-shot"])
            .Invoke());
        Assert.NotEqual(
            SingleGlbHash(tweenedCycle),
            SingleGlbHash(tweenedClamp));

        var plainCycle = Path.Combine(temp.Path, "plain-cycle");
        var plainClamp = Path.Combine(temp.Path, "plain-clamp");
        Assert.Equal(0, MeshCommand.Create()
            .Parse([untweenedPath, "--output", plainCycle, "--n64-animations"]).Invoke());
        Assert.Equal(0, MeshCommand.Create()
            .Parse([untweenedPath, "--output", plainClamp, "--n64-animations", "--one-shot"])
            .Invoke());
        Assert.Equal(SingleGlbHash(plainCycle), SingleGlbHash(plainClamp));
    }

    [CorpusFact]
    public void MeshCommand_DefaultPath_IsByteIdenticalToTheFlaglessExport()
    {
        // The new options must not perturb anything by merely existing. An
        // explicitly-false --one-shot is the same run as omitting it.
        using var temp = new TempDirectory();
        var shellPath = StageBundle(SpiderN64Build, SpiderRomName, "010", temp);

        var bare = Path.Combine(temp.Path, "bare");
        var explicitFalse = Path.Combine(temp.Path, "explicit");
        Assert.Equal(0, MeshCommand.Create()
            .Parse([shellPath, "--output", bare, "--n64-animations"]).Invoke());
        Assert.Equal(0, MeshCommand.Create()
            .Parse([shellPath, "--output", explicitFalse, "--n64-animations", "--one-shot:false"])
            .Invoke());

        Assert.Equal(SingleGlbHash(bare), SingleGlbHash(explicitFalse));
    }

    /// <summary>
    ///     Materialises one carved bundle plus the render-bank companions the
    ///     converter resolves by relative path, and returns the shell path.
    /// </summary>
    private string StageBundle(
        string build, string rom, string slot, TempDirectory temp, string? root = null)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = N64Bundles.FindBundle(backend, slot);
        var archiveSource = new ArchiveAssetSource(backend, entry);

        var stageRoot = root == null ? temp.Path : Path.Combine(temp.Path, root);
        var bundleDirectory = Path.Combine(stageRoot, "models", slot);
        var group2Directory = Path.Combine(stageRoot, "group2");
        Directory.CreateDirectory(bundleDirectory);
        Directory.CreateDirectory(group2Directory);

        var shellPath = Path.Combine(bundleDirectory, entry.Name);
        File.WriteAllBytes(shellPath, archiveSource.ReadBytes());
        File.WriteAllBytes(
            Path.Combine(bundleDirectory, "renderbank-id.bin"),
            Assert.IsType<byte[]>(archiveSource.TryReadCompanion("renderbank-id.bin")));
        var renderBankId = Assert.IsType<uint>(N64ModelCompanions.TryReadRenderBankId(archiveSource));
        File.WriteAllBytes(
            Path.Combine(group2Directory, $"{renderBankId:D3}.bin"),
            Assert.IsType<byte[]>(N64ModelCompanions.TryReadRenderBank(archiveSource)));
        return shellPath;
    }

    private static string SingleGlbHash(string outputDirectory)
    {
        var path = Assert.Single(Directory.GetFiles(
            outputDirectory, "*.glb", SearchOption.AllDirectories));
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
                System.IO.Path.GetTempPath(),
                "nmt-n64-cli-" + Guid.NewGuid().ToString("N"));
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
