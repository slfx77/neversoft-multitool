using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the cross-directory companion hops for a carved N64 model bundle
///     (2026-08-06): a bundle in <c>models/NNN/</c> reaches its geometry in
///     <c>group2/</c> and its art in <c>textures/</c>, neither of which the
///     stock same-directory companion lookup can see. Exercised through the
///     .z64 archive backend, i.e. the way the GUI reads a ROM.
/// </summary>
public sealed class N64ModelCompanionsTests(TestPaths paths)
{
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater 2 (USA).z64";

    private (IArchiveFileSystem Fs, ArchiveAssetSource Source) OpenBundle(string bundlePath)
    {
        var romPath = paths.FindSampleFile(Thps2N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");
        var fs = ArchiveFileSystem.TryOpen(romPath!);
        Assert.NotNull(fs);
        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        var entry = backend!.FindByPath(bundlePath);
        Assert.NotNull(entry);
        return (fs!, new ArchiveAssetSource(backend, entry!));
    }

    [Fact]
    public void RenderBankId_ResolvesToItsGroup2Record()
    {
        var (fs, source) = OpenBundle("models/000/geometry.psx.n64");
        using var _ = fs;

        // models/000 stores BE 0x00000016 = 22.
        Assert.Equal(22u, N64ModelCompanions.TryReadRenderBankId(source));

        var bank = N64ModelCompanions.TryReadRenderBank(source);
        Assert.NotNull(bank);
        // group2/022.bin opens with the recursive BE table the carver walks.
        Assert.True(bank!.Length > 64);
        Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bank));
    }

    [Fact]
    public void TextureProvider_DecodesByPs1TextureId()
    {
        var (fs, source) = OpenBundle("models/000/geometry.psx.n64");
        using var _ = fs;

        var provider = N64ModelCompanions.BuildTextureProvider(source);

        // psxtxt_bfd7c623 is the skven porta-potty record verified against the
        // PS1 CLUT; the id in the name IS the PS1 texture id.
        var png = provider(0xBFD7C623);
        Assert.NotNull(png);
        Assert.Equal(0x89, png![0]);
        Assert.Equal((byte)'P', png[1]);

        // Repeat lookups are cached and must stay identical.
        Assert.Same(png, provider(0xBFD7C623));

        // An id with no record resolves to null rather than throwing.
        Assert.Null(provider(0xDEADBEEF));
    }

    [Fact]
    public void Shell_ParsesFromInsideTheRom()
    {
        var (fs, source) = OpenBundle("models/045/geometry.psx.n64");
        using var _ = fs;

        var shell = PsxN64ShellFile.Parse(source.ReadBytes());
        Assert.NotNull(shell);
        Assert.NotEmpty(shell!.Objects);
        Assert.Equal(70u, N64ModelCompanions.TryReadRenderBankId(source));
    }
}
