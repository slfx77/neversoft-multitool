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

    [Fact]
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

    [Fact]
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
