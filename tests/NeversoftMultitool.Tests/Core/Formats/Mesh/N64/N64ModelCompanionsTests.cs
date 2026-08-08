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

    private (IArchiveFileSystem Fs, ArchiveAssetSource Source) OpenBundle(string slot)
    {
        var romPath = paths.FindSampleFile(Thps2N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");
        var fs = ArchiveFileSystem.TryOpen(romPath!);
        Assert.NotNull(fs);
        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        return (fs!, N64Bundles.OpenBundle(backend!, slot));
    }

    [Fact]
    public void RenderBankId_ResolvesToItsGroup2Record()
    {
        var (fs, source) = OpenBundle("000");
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
    public void TextureProvider_DecodesByDictionarySlot()
    {
        var (fs, source) = OpenBundle("000");
        using var _ = fs;

        var provider = N64ModelCompanions.BuildTextureProvider(source);

        // Slot 2816 is psxtxt_bfd7c623, the skven porta-potty record verified
        // against the PS1 CLUT. Render-bank groups address textures BY SLOT.
        var texture = provider(2816);
        Assert.NotNull(texture);
        Assert.Equal("psxtxt_bfd7c623", texture!.Name);
        Assert.Equal(24, texture.Width);
        Assert.Equal(48, texture.Height);
        Assert.Equal(0x89, texture.Png[0]);
        Assert.Equal((byte)'P', texture.Png[1]);

        // Repeat lookups are cached and must stay identical.
        Assert.Same(texture, provider(2816));

        // Slot 0 is the untextured sentinel; an absent slot resolves to null.
        Assert.Null(provider(0));
        Assert.Null(provider(65000));
    }

    [Fact]
    public void Shell_ParsesFromInsideTheRom()
    {
        var (fs, source) = OpenBundle("045");
        using var _ = fs;

        var shell = PsxN64ShellFile.Parse(source.ReadBytes());
        Assert.NotNull(shell);
        Assert.NotEmpty(shell!.Objects);
        Assert.Equal(70u, N64ModelCompanions.TryReadRenderBankId(source));
    }
}
