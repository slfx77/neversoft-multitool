using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.NextGen;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.NextGen;

/// <summary>
///     Neversoft's next-gen <c>FA CE CA A7</c> texture dictionary (THAW, Project 8
///     and Proving Ground on Xbox 360 and PS3), derived 2026-08-26/27.
///     The decoder is validated two ways, because neither alone is sufficient:
///     cross-platform pixel comparison against the PS3 builds (whose payloads are
///     linear, so they referee the Xenos untiling), and LEGIBLE ART — a comparison
///     between two platforms that share a decode path cannot see an orientation
///     error, and indeed 371/371 textures matched while every one was upside-down
///     until a "KEEP OUT / NO TRESPASSING" sign showed it.
/// </summary>
public class NextGenTexFileTests(TestPaths paths)
{
    private const string ThawX360 = "Tony Hawk's American Wasteland (2005-10-29, X360 - Final)";

    [Fact]
    public void IsNextGenTex_RequiresACompleteValidatedHeader()
    {
        var header = BuildEmptyXenonDictionary();
        Assert.True(NextGenTexFile.IsNextGenTex(header));

        header[1] = 0x00;
        Assert.False(NextGenTexFile.IsNextGenTex(header));
        Assert.False(NextGenTexFile.IsNextGenTex([0xFA, 0xCE, 0xCA, 0xA7, 1, 0x1C, 0, 0]));
        Assert.False(NextGenTexFile.IsNextGenTex(new byte[4]));
    }

    [Fact]
    public void Parse_RejectsHeaderPlatformSentinelAndPs3LengthMismatches()
    {
        var wrongPlatform = BuildEmptyXenonDictionary();
        wrongPlatform[4] = 3;
        Assert.False(NextGenTexFile.Parse(wrongPlatform).Success);

        var wrongSentinel = BuildEmptyXenonDictionary();
        wrongSentinel[0x10] = 0;
        Assert.False(NextGenTexFile.Parse(wrongSentinel).Success);

        var wrongLength = BuildSinglePs3ArgbDictionary();
        wrongLength[0x1F]--;
        Assert.False(NextGenTexFile.Parse(wrongLength).Success);
    }

    [Fact]
    public void Parse_TruncatedXenonTiledPayload_FailsInsteadOfPaddingBlack()
    {
        var complete = BuildSingleXenonArgbDictionary();
        var decoded = NextGenTexFile.Parse(complete);
        Assert.True(decoded.Success, decoded.ErrorMessage);
        Assert.Equal([10, 20, 30, 255], Assert.Single(decoded.Textures).Pixels);

        var truncated = complete[..^1];
        var result = NextGenTexFile.Parse(truncated);

        Assert.False(result.Success);
        Assert.False(NextGenTexFile.IsNextGenTex(truncated));
        Assert.Contains("truncated", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_AlignedTruncatedUnbiasedXenonPayload_FailsInsteadOfWrapping()
    {
        var truncated = BuildSingleXenonArgbDictionary();
        const int tableOffset = 0x20;
        BinaryPrimitives.WriteUInt16BigEndian(truncated.AsSpan(tableOffset + 8), 64);
        BinaryPrimitives.WriteUInt16BigEndian(truncated.AsSpan(tableOffset + 10), 64);

        // The allocation is still a real 4 KiB-aligned Xenon region. Broad
        // modulo addressing used to repeat it until a false 64x64 ARGB decode
        // succeeded even though that surface needs 16 KiB of unique texels.
        var result = NextGenTexFile.Parse(truncated);

        Assert.False(result.Success);
        Assert.Contains("truncated", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UntileUnits_BiasedAlignedTruncation_CannotManufactureMissingBlocks()
    {
        Assert.Throws<InvalidDataException>(() =>
            XenosTiling.UntileUnits(
                new byte[1024], 16, 32, 4, 0,
                XenosTiling.SubTileBlockOffset * 4,
                wrapAtEnd: true));
    }

    [Fact]
    public void UntileUnits_TruncatedSource_ThrowsInsteadOfReturningZeroFilledBlocks()
    {
        Assert.Throws<InvalidDataException>(() =>
            XenosTiling.UntileUnits(new byte[3], 1, 1, 4, 0));
    }

    /// <summary>
    ///     The VRAM twin's name and, critically, its DIRECTORY: an extracted
    ///     <c>FOO.PAK</c> pairs with the sibling <c>FOO_VRAM.PAK</c>. Appending
    ///     the suffix instead (<c>FOO.PAK_vram.pak</c>) silently falls back to a
    ///     same-directory copy that is not the payload — that mistake cost 49 of
    ///     49 pak-contained textures, all of which decode once it is fixed.
    /// </summary>
    [Theory]
    [InlineData("cutscene.tex.ps3", "cutscene.tvx.ps3")]
    [InlineData("CUTSCENE.TEX.PS3", "CUTSCENE.tvx.PS3")]
    [InlineData("level.stex.ps3", "level.vstex.ps3")]
    public void GetVramTwinFileName_SwapsTheTextureSuffix(string input, string expected)
    {
        Assert.Equal(expected, NextGenTexFile.GetVramTwinFileName(input));
    }

    [Theory]
    [InlineData("BAM_MUGGING_MAIN.PAK", "BAM_MUGGING_MAIN_VRAM.PAK")]
    [InlineData("foo.pak", "foo_VRAM.pak")]
    [InlineData("plain", "plain_VRAM")]
    public void GetVramTwinDirectoryName_InsertsBeforeTheExtension(string input, string expected)
    {
        Assert.Equal(expected, NextGenTexFile.GetVramTwinDirectoryName(input));
    }

    [Fact]
    public void Parse_ArchiveSource_UsesItsNamedPs3VramTwin()
    {
        var dictionary = BuildSinglePs3ArgbDictionary();
        byte[] vram = [255, 10, 20, 30]; // stored A,R,G,B
        var source = new MemoryAssetSource("sample.tex.ps3", dictionary,
            "sample.tvx.ps3", vram);

        var result = NextGenTexFile.Parse(source, dictionary);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(0x12345678u, texture.Checksum);
        Assert.Equal((1, 1, 0x85u), (texture.Width, texture.Height, texture.Psm));
        Assert.Equal([10, 20, 30, 255], texture.Pixels);
        Assert.Equal(
            ["sample.tvx.ps3", "sample.vtex.ps3"],
            source.CompanionRequests);
    }

    [Fact]
    public void Parse_ArchiveSource_AcceptsProject8VtexTwinName()
    {
        var dictionary = BuildSinglePs3ArgbDictionary();
        byte[] vram = [255, 10, 20, 30];
        var source = new MemoryAssetSource("sample.tex.ps3", dictionary,
            "sample.vtex.ps3", vram);

        var result = NextGenTexFile.Parse(source, dictionary);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal([10, 20, 30, 255], Assert.Single(result.Textures).Pixels);
        Assert.Equal(
            ["sample.tvx.ps3", "sample.vtex.ps3"],
            source.CompanionRequests);
    }

    [Fact]
    public void VramTwinLocator_ArchiveSourceDisagreeingExactNamesFailClosed()
    {
        var dictionary = BuildSinglePs3ArgbDictionary();
        var source = new MemoryAssetSource(
            "sample.tex.ps3",
            dictionary,
            "sample.tvx.ps3",
            [255, 10, 20, 30],
            "sample.vtex.ps3",
            [255, 40, 50, 60]);

        var payload = NextGenVramTwinLocator.TryLoad(source, dictionary);

        Assert.Null(payload);
        Assert.Equal(
            ["sample.tvx.ps3", "sample.vtex.ps3"],
            source.CompanionRequests);
    }

    [Fact]
    public void Parse_ArchiveSource_RejectsATruncatedNamedPs3VramTwin()
    {
        var dictionary = BuildSinglePs3ArgbDictionary();
        var source = new MemoryAssetSource(
            "sample.tex.ps3", dictionary, "sample.tvx.ps3", new byte[3]);

        var result = NextGenTexFile.Parse(source, dictionary);

        Assert.False(result.Success);
        Assert.Contains("truncated", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sample.stex.ps3", "sample.692F8667.ps3")]
    [InlineData("sample.tex.ps3", "sample.1CD4C0A7.ps3")]
    public void VramTwinLocator_ResolvesHashNamedCompactPakEntries(
        string dictionaryName,
        string twinName)
    {
        var root = Directory.CreateTempSubdirectory("nmt-ps3-vram-");
        try
        {
            var dictionaryDirectory = Directory.CreateDirectory(
                Path.Combine(root.FullName, "sample.pak"));
            var twinDirectory = Directory.CreateDirectory(
                Path.Combine(root.FullName, "sample_VRAM.pak"));
            var dictionaryPath = Path.Combine(dictionaryDirectory.FullName, dictionaryName);
            var twinPath = Path.Combine(twinDirectory.FullName, twinName);
            File.WriteAllBytes(twinPath, [255, 10, 20, 30]);

            var resolved = NextGenVramTwinLocator.TryResolve(
                dictionaryPath, BuildSinglePs3ArgbDictionary());

            Assert.Equal(twinPath, resolved);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void VramTwinLocator_DoesNotFallBackFromAuthoritativeVramPakToLocalTwin()
    {
        var root = Directory.CreateTempSubdirectory("nmt-ps3-vram-owner-");
        try
        {
            var dictionaryDirectory = Directory.CreateDirectory(
                Path.Combine(root.FullName, "sample.pak"));
            var twinDirectory = Directory.CreateDirectory(
                Path.Combine(root.FullName, "sample_VRAM.pak"));
            var dictionaryPath = Path.Combine(dictionaryDirectory.FullName, "sample.tex.ps3");
            var localPath = Path.Combine(dictionaryDirectory.FullName, "sample.tvx.ps3");
            var ownedPath = Path.Combine(twinDirectory.FullName, "sample.vtex.ps3");
            File.WriteAllBytes(localPath, [1, 2, 3, 4]);
            File.WriteAllBytes(ownedPath, [255, 10, 20, 30]);
            var dictionary = BuildSinglePs3ArgbDictionary();

            Assert.Equal(ownedPath,
                NextGenVramTwinLocator.TryResolve(dictionaryPath, dictionary));

            File.Delete(ownedPath);
            Assert.Null(NextGenVramTwinLocator.TryResolve(dictionaryPath, dictionary));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void VramTwinLocator_IdenticalDictionaryReusesExactSameBuildPayload()
    {
        var root = Directory.CreateTempSubdirectory("nmt-ps3-vram-content-");
        try
        {
            var dataRoot = CreatePs3DataRoot(root.FullName);
            var sourceDirectory = Directory.CreateDirectory(
                Path.Combine(dataRoot, "MODELS", "source"));
            var targetDirectory = Directory.CreateDirectory(
                Path.Combine(dataRoot, "CUTSCENES", "target"));
            var dictionary = BuildSinglePs3ArgbDictionary();
            var sourcePath = Path.Combine(sourceDirectory.FullName, "source.stex.ps3");
            var targetPath = Path.Combine(targetDirectory.FullName, "target.stex.ps3");
            var payloadPath = Path.Combine(sourceDirectory.FullName, "source.vstex.ps3");
            File.WriteAllBytes(sourcePath, dictionary);
            File.WriteAllBytes(targetPath, dictionary);
            File.WriteAllBytes(payloadPath, [255, 10, 20, 30]);

            var resolution = NextGenVramTwinLocator.ResolvePayload(targetPath, dictionary);

            Assert.Equal(NextGenVramPayloadSource.IdenticalDictionary, resolution.Source);
            Assert.Equal(payloadPath, resolution.FileSystemPath);
            var parsed = NextGenTexFile.Parse(dictionary, resolution.Bytes);
            Assert.True(parsed.Success, parsed.ErrorMessage);
            Assert.Equal([10, 20, 30, 255], Assert.Single(parsed.Textures).Pixels);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void VramTwinLocator_IdenticalDictionaryPayloadDisagreementFailsClosed()
    {
        var root = Directory.CreateTempSubdirectory("nmt-ps3-vram-ambiguous-");
        try
        {
            var dataRoot = CreatePs3DataRoot(root.FullName);
            var dictionary = BuildSinglePs3ArgbDictionary();
            foreach (var (name, payload) in new[]
                     {
                         ("first", new byte[] { 255, 10, 20, 30 }),
                         ("second", new byte[] { 255, 40, 50, 60 })
                     })
            {
                var directory = Directory.CreateDirectory(Path.Combine(dataRoot, name));
                File.WriteAllBytes(Path.Combine(directory.FullName, name + ".tex.ps3"), dictionary);
                File.WriteAllBytes(Path.Combine(directory.FullName, name + ".tvx.ps3"), payload);
            }

            var targetDirectory = Directory.CreateDirectory(Path.Combine(dataRoot, "target"));
            var targetPath = Path.Combine(targetDirectory.FullName, "target.tex.ps3");
            File.WriteAllBytes(targetPath, dictionary);

            var resolution = NextGenVramTwinLocator.ResolvePayload(targetPath, dictionary);

            Assert.Equal(NextGenVramPayloadSource.None, resolution.Source);
            Assert.Null(resolution.Bytes);
            Assert.Null(NextGenVramTwinLocator.TryResolve(targetPath, dictionary));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void VramTwinLocator_IdenticalDictionaryNeverBorrowsAcrossBuildRoots()
    {
        var root = Directory.CreateTempSubdirectory("nmt-ps3-vram-build-scope-");
        try
        {
            var dictionary = BuildSinglePs3ArgbDictionary();
            var firstData = CreatePs3DataRoot(Path.Combine(root.FullName, "first"));
            var secondData = CreatePs3DataRoot(Path.Combine(root.FullName, "second"));
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(firstData, "source"));
            var targetDirectory = Directory.CreateDirectory(Path.Combine(secondData, "target"));
            File.WriteAllBytes(
                Path.Combine(sourceDirectory.FullName, "source.tex.ps3"), dictionary);
            File.WriteAllBytes(
                Path.Combine(sourceDirectory.FullName, "source.tvx.ps3"),
                [255, 10, 20, 30]);
            var targetPath = Path.Combine(targetDirectory.FullName, "target.tex.ps3");
            File.WriteAllBytes(targetPath, dictionary);

            var resolution = NextGenVramTwinLocator.ResolvePayload(targetPath, dictionary);

            Assert.Equal(NextGenVramPayloadSource.None, resolution.Source);
            Assert.Null(resolution.Bytes);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void VramTwinLocator_ShortExactOwnerBlocksContentFallback()
    {
        var root = Directory.CreateTempSubdirectory("nmt-ps3-vram-short-owner-");
        try
        {
            var dataRoot = CreatePs3DataRoot(root.FullName);
            var dictionary = BuildSinglePs3ArgbDictionary();
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(dataRoot, "source"));
            File.WriteAllBytes(
                Path.Combine(sourceDirectory.FullName, "source.tex.ps3"), dictionary);
            File.WriteAllBytes(
                Path.Combine(sourceDirectory.FullName, "source.tvx.ps3"),
                [255, 10, 20, 30]);

            var targetDirectory = Directory.CreateDirectory(Path.Combine(dataRoot, "target"));
            var targetPath = Path.Combine(targetDirectory.FullName, "target.tex.ps3");
            File.WriteAllBytes(targetPath, dictionary);
            File.WriteAllBytes(Path.Combine(targetDirectory.FullName, "target.tvx.ps3"), [1, 2, 3]);

            var resolution = NextGenVramTwinLocator.ResolvePayload(targetPath, dictionary);
            var parsed = NextGenTexFile.Parse(dictionary, resolution.Bytes);

            Assert.Equal(NextGenVramPayloadSource.ExactName, resolution.Source);
            Assert.False(parsed.Success);
            Assert.Contains("truncated", parsed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void VramTwinLocator_DisagreeingExactNamesFailClosed()
    {
        var root = Directory.CreateTempSubdirectory("nmt-ps3-vram-exact-ambiguous-");
        try
        {
            var dataRoot = CreatePs3DataRoot(root.FullName);
            var dictionary = BuildSinglePs3ArgbDictionary();
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(dataRoot, "source"));
            File.WriteAllBytes(
                Path.Combine(sourceDirectory.FullName, "source.tex.ps3"), dictionary);
            File.WriteAllBytes(
                Path.Combine(sourceDirectory.FullName, "source.tvx.ps3"),
                [255, 10, 20, 30]);

            var targetDirectory = Directory.CreateDirectory(Path.Combine(dataRoot, "target"));
            var targetPath = Path.Combine(targetDirectory.FullName, "target.tex.ps3");
            File.WriteAllBytes(targetPath, dictionary);
            File.WriteAllBytes(
                Path.Combine(targetDirectory.FullName, "target.tvx.ps3"),
                [255, 40, 50, 60]);
            File.WriteAllBytes(
                Path.Combine(targetDirectory.FullName, "target.vtex.ps3"),
                [255, 70, 80, 90]);

            var resolution = NextGenVramTwinLocator.ResolvePayload(targetPath, dictionary);

            Assert.Equal(NextGenVramPayloadSource.None, resolution.Source);
            Assert.Null(resolution.Bytes);
            Assert.Null(NextGenVramTwinLocator.TryResolve(targetPath, dictionary));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void VramTwinLocator_CompressedPakNamedEntryLoadsItsExactDecodedRange()
    {
        var root = Directory.CreateTempSubdirectory("nmt-ps3-vram-pak-name-");
        try
        {
            var owner = Directory.CreateDirectory(
                Path.Combine(CreatePs3DataRoot(root.FullName), "CUTSCENES"));
            var dictionary = BuildSinglePs3ArgbDictionary(0x11111111);
            byte[] payload = [255, 10, 20, 30];
            var mainPath = Path.Combine(owner.FullName, "sample.pak.ps3");
            var vramPath = Path.Combine(owner.FullName, "sample_vram.pak.ps3");
            File.WriteAllBytes(mainPath, Deflate(BuildCompactPs3Pak(
                (NextGenVramTwinLocator.TexDescriptorType, 0x13579BDF, dictionary))));
            File.WriteAllBytes(vramPath, Deflate(BuildCompactPs3Pak(
                (NextGenVramTwinLocator.VtexPayloadType, 0x13579BDF, payload))));

            var descriptor = Assert.Single(PakArchive.GetTypedEntries(mainPath)).Entry;
            var extracted = Directory.CreateDirectory(Path.Combine(owner.FullName, "sample.pak"));
            var dictionaryPath = Path.Combine(extracted.FullName, descriptor.FullName);
            File.WriteAllBytes(dictionaryPath, dictionary);

            var resolution = NextGenVramTwinLocator.ResolvePayload(dictionaryPath, dictionary);

            Assert.Equal(NextGenVramPayloadSource.ArchiveNamedEntry, resolution.Source);
            Assert.Equal(payload, resolution.Bytes);
            Assert.Null(resolution.FileSystemPath);
            Assert.Contains("sample_vram.pak.ps3::", resolution.Location,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void VramTwinLocator_RawPakCollisionUsesProvenTypedOrdinal()
    {
        var root = Directory.CreateTempSubdirectory("nmt-ps3-vram-pak-index-");
        try
        {
            var owner = Directory.CreateDirectory(
                Path.Combine(CreatePs3DataRoot(root.FullName), "CUTSCENES"));
            var firstDictionary = BuildSinglePs3ArgbDictionary(0x11111111);
            var targetDictionary = BuildSinglePs3ArgbDictionary(0x22222222);
            byte[] firstPayload = [255, 10, 20, 30];
            byte[] targetPayload = [255, 40, 50, 60];
            const uint collidingNameCrc = 0x2468ACE0;
            var mainPath = Path.Combine(owner.FullName, "sample.pak.ps3");
            var vramPath = Path.Combine(owner.FullName, "sample_vram.pak.ps3");
            File.WriteAllBytes(mainPath, BuildCompactPs3Pak(
                (NextGenVramTwinLocator.TexDescriptorType, collidingNameCrc, firstDictionary),
                (NextGenVramTwinLocator.TexDescriptorType, collidingNameCrc, targetDictionary)));
            File.WriteAllBytes(vramPath, BuildCompactPs3Pak(
                (NextGenVramTwinLocator.VtexPayloadType, collidingNameCrc, firstPayload),
                (NextGenVramTwinLocator.VtexPayloadType, collidingNameCrc, targetPayload)));

            var descriptors = PakArchive.GetTypedEntries(mainPath);
            Assert.Equal(2, descriptors.Count);
            var extracted = Directory.CreateDirectory(Path.Combine(owner.FullName, "sample.pak"));
            var targetPath = Path.Combine(extracted.FullName, descriptors[1].Entry.FullName);
            File.WriteAllBytes(targetPath, targetDictionary);

            var resolution = NextGenVramTwinLocator.ResolvePayload(targetPath, targetDictionary);
            var parsed = NextGenTexFile.Parse(targetDictionary, resolution.Bytes);

            Assert.Equal(NextGenVramPayloadSource.ArchiveIndexedEntry, resolution.Source);
            Assert.Equal(targetPayload, resolution.Bytes);
            Assert.True(parsed.Success, parsed.ErrorMessage);
            Assert.Equal([40, 50, 60, 255], Assert.Single(parsed.Textures).Pixels);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void VramTwinLocator_RawPakPopulationMismatchFailsClosed()
    {
        var root = Directory.CreateTempSubdirectory("nmt-ps3-vram-pak-ambiguous-");
        try
        {
            var owner = Directory.CreateDirectory(
                Path.Combine(CreatePs3DataRoot(root.FullName), "CUTSCENES"));
            var firstDictionary = BuildSinglePs3ArgbDictionary(0x11111111);
            var targetDictionary = BuildSinglePs3ArgbDictionary(0x22222222);
            const uint collidingNameCrc = 0x2468ACE0;
            var mainPath = Path.Combine(owner.FullName, "sample.pak.ps3");
            var vramPath = Path.Combine(owner.FullName, "sample_vram.pak.ps3");
            File.WriteAllBytes(mainPath, BuildCompactPs3Pak(
                (NextGenVramTwinLocator.TexDescriptorType, collidingNameCrc, firstDictionary),
                (NextGenVramTwinLocator.TexDescriptorType, collidingNameCrc, targetDictionary)));
            File.WriteAllBytes(vramPath, BuildCompactPs3Pak(
                (NextGenVramTwinLocator.VtexPayloadType, collidingNameCrc,
                    new byte[] { 255, 10, 20, 30 })));

            var descriptors = PakArchive.GetTypedEntries(mainPath);
            Assert.Equal(2, descriptors.Count);
            var extracted = Directory.CreateDirectory(Path.Combine(owner.FullName, "sample.pak"));
            var targetPath = Path.Combine(extracted.FullName, descriptors[1].Entry.FullName);
            File.WriteAllBytes(targetPath, targetDictionary);

            var resolution = NextGenVramTwinLocator.ResolvePayload(targetPath, targetDictionary);

            Assert.Equal(NextGenVramPayloadSource.None, resolution.Source);
            Assert.Null(resolution.Bytes);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    ///     Untiling is a PERMUTATION of storage units, so a uniform surface must
    ///     come back uniform whatever the layout — the property that proved a
    ///     mismatching texture held genuinely different art rather than a decode
    ///     error.
    /// </summary>
    [Theory]
    [InlineData(64, 64, 8)]
    [InlineData(64, 64, 16)]
    [InlineData(16, 16, 8)]
    public void UntileBlocks_IsAPermutation(int width, int height, int blockBytes)
    {
        // The stored region is PADDED to whole 32-block macro tiles, so a tiled
        // address can sit past the tight size — which is why decoding reads to
        // the end of the file rather than a computed length.
        var blocksX = (Math.Max(1, (width + 3) / 4) + 31) & ~31;
        var blocksY = (Math.Max(1, (height + 3) / 4) + 31) & ~31;
        var source = new byte[blocksX * blocksY * blockBytes];
        Array.Fill(source, (byte)0xAB);

        var untiled = XenosTiling.UntileBlocks(source, width, height, blockBytes, false);

        var tight = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes;
        Assert.Equal(tight, untiled.Length);
        Assert.All(untiled, b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public void SubMacroTileSurfaces_StartThirtyTwoBlocksIn()
    {
        // Derived from the measured block permutation; before it, every sub-32
        // texture failed and every larger one passed.
        Assert.Equal(32 * 8, XenosTiling.GetSurfaceByteOffset(16, 16, 8));
        Assert.Equal(32 * 16, XenosTiling.GetSurfaceByteOffset(8, 8, 16));
        Assert.Equal(0, XenosTiling.GetSurfaceByteOffset(32, 32, 8));
        Assert.Equal(0, XenosTiling.GetSurfaceByteOffset(256, 64, 8));
        // A surface short on EITHER axis is sub-macro-tile.
        Assert.Equal(32 * 8, XenosTiling.GetSurfaceByteOffset(256, 16, 8));
    }

    /// <summary>
    ///     DXN has two 8-byte BC4 halves, but the Xenos address calculation sees
    ///     the complete 16-byte BC5 block. These addresses distinguish that from
    ///     the tempting (and visibly scrambled) 8-byte-unit interpretation.
    /// </summary>
    [Fact]
    public void UntileUnits_DxnUsesSixteenByteBlockAddresses()
    {
        const int blocksX = 32;
        const int blocksY = 32;
        const int blockBytes = 16;
        var tiled = new byte[blocksX * blocksY * blockBytes];
        for (var storedIndex = 0; storedIndex < blocksX * blocksY; storedIndex++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                tiled.AsSpan(storedIndex * blockBytes), storedIndex);
        }

        var linear = XenosTiling.UntileUnits(
            tiled, blocksX, blocksY, blockBytes, swapWidth: 0);

        AssertStoredIndex(linear, blocksX, 0, 0, 0);
        AssertStoredIndex(linear, blocksX, 1, 0, 2);
        AssertStoredIndex(linear, blocksX, 2, 0, 16);
        AssertStoredIndex(linear, blocksX, 0, 1, 1);
        AssertStoredIndex(linear, blocksX, 8, 0, 4);
        AssertStoredIndex(linear, blocksX, 0, 8, 520);
        AssertStoredIndex(linear, blocksX, 31, 31, 1015);
    }

    /// <summary>
    ///     One THAW X360 dictionary pinned by RGBA hash per texture. FCBC3132 is
    ///     the "KEEP OUT / NO TRESPASSING" sign whose legibility settled the
    ///     bottom-up row order, so these hashes lock the orientation that a
    ///     cross-platform comparison cannot check.
    /// </summary>
    [CorpusFact]
    public void Parse_ThawX360Dictionary_DecodesToPinnedPixels()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(ThawX360, "45EDAA46.stex.xen");
        Assert.SkipWhen(file == null, "45EDAA46.stex.xen not present");

        var result = NextGenTexFile.Parse(File.ReadAllBytes(file!));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(8, result.Textures.Count);

        var expected = new Dictionary<uint, (int Width, int Height, string Sha)>
        {
            [0x0900939E] = (512, 512, "C97C6F0F14504FBCEF50578990F432C169E95F0316ED6B78299492007D885DE7"),
            [0x5C1FAA8C] = (128, 32, "38491630733F027340FB50799CC98A378E19BEEEA1EA54D2E504F5B2A31786A8"),
            [0xB334B4A5] = (64, 128, "84BF7E2AE7CFE89AD8F78AC689E5EBBD2E890BA1088638EAC18163FF1759D5E4"),
            [0xFCBC3132] = (128, 128, "BAEDC37319B9C57F711D44E2EE81EC0CCD78B8A4E19A9937D07E956F1F2ABF2A"),
        };

        foreach (var (checksum, want) in expected)
        {
            var texture = result.Textures.Single(t => t.Checksum == checksum);
            Assert.Equal((want.Width, want.Height), (texture.Width, texture.Height));
            Assert.NotNull(texture.Pixels);
            Assert.Equal(want.Sha, Convert.ToHexString(SHA256.HashData(texture.Pixels!)));
        }
    }

    /// <summary>
    ///     A named THAW X360 wardrobe dictionary with one DXN normal map. The
    ///     selected texture is recognizable glasses art, which discriminates
    ///     the correct 16-byte Xenos tiling from two plausible 8-byte variants.
    /// </summary>
    [CorpusFact]
    public void Parse_ThawX360Dxn_DecodesToPinnedPixels()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(ThawX360, "specs_vz04.tex.xen");
        Assert.SkipWhen(file == null, "specs_vz04.tex.xen not present");

        var result = NextGenTexFile.Parse(File.ReadAllBytes(file!));

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures, texture => texture.Checksum == 0x40365BF6);
        Assert.Equal((128, 128, 0x31u), (texture.Width, texture.Height, texture.Psm));
        Assert.NotNull(texture.Pixels);
        Assert.Equal(
            "8995A43277D275D9919D45BF1F58BABAEC2920D1DB76177BE190D29DB333C621",
            Convert.ToHexString(SHA256.HashData(texture.Pixels!)));
    }

    /// <summary>
    ///     These final-record surfaces exactly fill their 4/8 KiB allocations.
    ///     Their 32-unit GPU base bias crosses EOF and must wrap inside that
    ///     allocation; reading onward either pads black or borrows the next
    ///     texture. Two independently packed copies of checksum 97289379 are
    ///     byte-identical after bounded circular untiling.
    /// </summary>
    [CorpusFact]
    public void Parse_ThawX360BiasedSurfaces_WrapInsideTheirOwnPayloadAllocation()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var mullen = paths.FindSampleFile(ThawX360, "skater_mullen.tex.xen");
        var first = paths.FindSampleFile(ThawX360, "6D5FA9CD.stex.xen");
        var second = paths.FindSampleFile(ThawX360, "F4509242.stex.xen");
        Assert.SkipWhen(mullen == null || first == null || second == null,
            "Biased Xenos surface fixtures are not present");

        static Ps2Texture Texture(string file, uint checksum)
        {
            var result = NextGenTexFile.Parse(File.ReadAllBytes(file));
            Assert.True(result.Success, result.ErrorMessage);
            return Assert.Single(result.Textures, texture => texture.Checksum == checksum);
        }

        var mullenTexture = Texture(mullen!, 0xED658F30);
        Assert.Equal((16, 32), (mullenTexture.Width, mullenTexture.Height));
        Assert.Equal(
            "7F9302188AE268211CD86CFF1EC21F4C71D497BB4A8975A850F089F8186343DF",
            Convert.ToHexString(SHA256.HashData(mullenTexture.Pixels!)));

        var firstTexture = Texture(first!, 0x97289379);
        var secondTexture = Texture(second!, 0x97289379);
        Assert.Equal(firstTexture.Pixels, secondTexture.Pixels);
        Assert.Equal(
            "BB8959420133BF1EC4DBB6F43DA1A519C0EAB403A1FDBB5FEB15A3562F80967B",
            Convert.ToHexString(SHA256.HashData(firstTexture.Pixels!)));
    }

    /// <summary>
    ///     Whole-corpus structural sweep across the five shipping builds. This is
    ///     the check that catches layout regressions: a byte-typed loop counter
    ///     once wrapped at 255 and rejected every dictionary whose record table
    ///     sat past that offset, which no single-fixture test noticed.
    /// </summary>
    [CorpusFact]
    public void Parse_NextGenTextureCorpus_ParsesEveryFile()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        string[] builds =
        [
            ThawX360,
            "Tony Hawk's Project 8 (2006-11-7, X360 - Final)",
            "Tony Hawk's Proving Ground (2007-8-30, X360 - Final)",
            "Tony Hawk's Project 8 (2006-10-5, PS3 - Final)",
            "Tony Hawk's Proving Ground (2007-8-31, PS3 - Final)"
        ];

        var files = 0;
        var textures = 0;
        var emptyDictionaries = 0;
        var ps3ResolvedDictionaries = 0;
        var ps3UnresolvedDictionaries = 0;
        var ps3ResolvedTextures = 0;
        var ps3UnresolvedTextures = 0;
        var ps3SourceDictionaries = new Dictionary<NextGenVramPayloadSource, int>();
        var ps3SourceTextures = new Dictionary<NextGenVramPayloadSource, int>();
        var representativeExpectations = new Dictionary<
            string, (NextGenVramPayloadSource Source, string PixelHash)>(
            StringComparer.OrdinalIgnoreCase)
        {
            [builds[3] + "|PS3_GAME/USRDIR/DATA/CUTSCENES/" +
             "bob_mugging_main.pak/cutscene_00001EE0.stex.ps3"] =
                (NextGenVramPayloadSource.IdenticalDictionary,
                    "AEC63987D80CE0AAFCDC42304BBDCF96F1F66D453F1BC455DC71C64AEF8EA030"),
            [builds[4] + "|PS3_GAME/USRDIR/DATA/ZONES/" +
             "Z_BELL.PAK/z_bell_0022DF20.tex.ps3"] =
                (NextGenVramPayloadSource.IdenticalDictionary,
                    "2BD8C0C2B222507588534497A68B972453AA50E3658FA2C32B360C50148AAFCA"),
            [builds[3] + "|PS3_GAME/USRDIR/DATA/CUTSCENES/" +
             "c_classic_cretepark_main.pak/cutscene_00026B20.stex.ps3"] =
                (NextGenVramPayloadSource.ArchiveIndexedEntry,
                    "66A2BDA8FB2D592673D8BF9FF60C1CB0C139F742788FF7292D5E913B6E2E7357"),
            [builds[3] + "|PS3_GAME/USRDIR/DATA/ZONES/" +
             "global.pak/AC1EA426.tex.ps3"] =
                (NextGenVramPayloadSource.ArchiveNamedEntry,
                    "D0388279B76B857245CC8835745CD2CA55F956C635B816C5F064D4FDED356D77")
        };
        var representativeResults = new Dictionary<
            string, (NextGenVramPayloadSource Source, string PixelHash)>(
            StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var build in builds)
        {
            var root = Path.Combine(paths.SampleBuildsDir!, build);
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(".tex.xen", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".stex.xen", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".tex.ps3", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".stex.ps3", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = File.ReadAllBytes(file);
                if (!NextGenTexFile.IsNextGenTex(data)) continue;

                files++;
                var resolution = NextGenVramTwinLocator.ResolvePayload(file, data);
                var result = NextGenTexFile.Parse(data, resolution.Bytes);
                if (!result.Success)
                {
                    failures.Add($"{name}: {result.ErrorMessage}");
                    continue;
                }

                if (result.Textures.Count == 0) emptyDictionaries++;
                var representativeKey = build + "|" + Path.GetRelativePath(root, file)
                    .Replace('\\', '/');
                if (representativeExpectations.ContainsKey(representativeKey)
                    && result.Textures.All(static texture => texture.Pixels != null))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    foreach (var texture in result.Textures)
                        hash.AppendData(texture.Pixels!);
                    representativeResults[representativeKey] = (
                        resolution.Source,
                        Convert.ToHexString(hash.GetHashAndReset()));
                }
                if (name.EndsWith(".ps3", StringComparison.OrdinalIgnoreCase)
                    && result.Textures.Count > 0)
                {
                    var resolvedTextures = result.Textures.Count(
                        static texture => texture.Pixels != null);
                    if (resolvedTextures != 0 && resolvedTextures != result.Textures.Count)
                    {
                        failures.Add($"{name}: only {resolvedTextures}/" +
                                     $"{result.Textures.Count} records resolved pixels");
                    }
                    else if (resolvedTextures == result.Textures.Count)
                    {
                        ps3ResolvedDictionaries++;
                        ps3ResolvedTextures += resolvedTextures;
                        ps3SourceDictionaries[resolution.Source] =
                            ps3SourceDictionaries.GetValueOrDefault(resolution.Source) + 1;
                        ps3SourceTextures[resolution.Source] =
                            ps3SourceTextures.GetValueOrDefault(resolution.Source) + resolvedTextures;
                    }
                    else
                    {
                        ps3UnresolvedDictionaries++;
                        ps3UnresolvedTextures += result.Textures.Count;
                    }
                }
                textures += result.Textures.Count;
            }
        }

        Assert.SkipWhen(files == 0, "Next-gen builds not present");
        Assert.True(failures.Count == 0,
            $"{failures.Count} parse failures:\n{string.Join("\n", failures.Take(10))}");
        Assert.Equal(12_335, files);
        Assert.Equal(90_477, textures);
        Assert.Equal(789, emptyDictionaries);
        // Every non-empty PS3 dictionary now has proof-bound pixels. The content
        // route stays inside one PS3_GAME/USRDIR/DATA build and compares every
        // eligible payload byte-for-byte. Raw PAK routes require complete typed
        // populations, matching CRC/logical stem, table index, and exact size.
        Assert.Equal(3_570, ps3ResolvedDictionaries);
        Assert.Equal(23_090, ps3ResolvedTextures);
        Assert.Equal(0, ps3UnresolvedDictionaries);
        Assert.Equal(0, ps3UnresolvedTextures);
        foreach (var (key, expected) in representativeExpectations)
        {
            Assert.True(representativeResults.TryGetValue(key, out var actual),
                $"Representative PS3 dictionary was not decoded: {key}");
            Assert.Equal(expected.Source, actual.Source);
            Assert.Equal(expected.PixelHash, actual.PixelHash);
        }
        Assert.Equal(2_388,
            ps3SourceDictionaries.GetValueOrDefault(NextGenVramPayloadSource.ExactName));
        Assert.Equal(15_573,
            ps3SourceTextures.GetValueOrDefault(NextGenVramPayloadSource.ExactName));
        Assert.Equal(1_083,
            ps3SourceDictionaries.GetValueOrDefault(
                NextGenVramPayloadSource.IdenticalDictionary));
        Assert.Equal(6_577,
            ps3SourceTextures.GetValueOrDefault(
                NextGenVramPayloadSource.IdenticalDictionary));
        Assert.Equal(19,
            ps3SourceDictionaries.GetValueOrDefault(
                NextGenVramPayloadSource.ArchiveNamedEntry));
        Assert.Equal(91,
            ps3SourceTextures.GetValueOrDefault(
                NextGenVramPayloadSource.ArchiveNamedEntry));
        Assert.Equal(80,
            ps3SourceDictionaries.GetValueOrDefault(
                NextGenVramPayloadSource.ArchiveIndexedEntry));
        Assert.Equal(849,
            ps3SourceTextures.GetValueOrDefault(
                NextGenVramPayloadSource.ArchiveIndexedEntry));
    }

    private static byte[] BuildSinglePs3ArgbDictionary(uint checksum = 0x12345678)
    {
        const int tableOffset = 0x28;
        const int recordSize = 48;
        const int auxOffset = tableOffset + recordSize;
        const int dataStart = auxOffset + 24;
        var data = new byte[dataStart];

        BinaryPrimitives.WriteUInt32BigEndian(data, 0xFACECAA7);
        data[4] = 2; // PS3
        data[5] = 0x24;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), tableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C), dataStart);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x10), uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x18), 0x24);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x1C), (uint)data.Length);
        data.AsSpan(0x24, tableOffset - 0x24).Fill(0xEF);

        data[tableOffset] = recordSize / 4;
        data[tableOffset + 1] = recordSize;
        data[tableOffset + 3] = 0x85;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(tableOffset + 4), checksum);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(tableOffset + 8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(tableOffset + 10), 1);
        // PS3 trailing words 1 and 2 are the offset and byte length in the twin.
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(tableOffset + 28), 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(tableOffset + 32), 4);
        data[auxOffset] = 0x85;
        return data;
    }

    private static byte[] BuildCompactPs3Pak(
        params (uint Type, uint NameCrc, byte[] Data)[] entries)
    {
        const int entrySize = 0x20;
        const uint lastSentinel = 0xB524565F;
        var payloadOffset = checked((entries.Length + 1) * entrySize);
        var data = new byte[checked(payloadOffset + entries.Sum(static entry => entry.Data.Length))];

        var nextPayload = payloadOffset;
        for (var i = 0; i < entries.Length; i++)
        {
            var headerOffset = i * entrySize;
            var entry = entries[i];
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(headerOffset), entry.Type);
            BinaryPrimitives.WriteUInt32BigEndian(
                data.AsSpan(headerOffset + 4), checked((uint)(nextPayload - headerOffset)));
            BinaryPrimitives.WriteUInt32BigEndian(
                data.AsSpan(headerOffset + 8), checked((uint)entry.Data.Length));
            BinaryPrimitives.WriteUInt32BigEndian(
                data.AsSpan(headerOffset + 0x14), entry.NameCrc);
            entry.Data.CopyTo(data.AsSpan(nextPayload));
            nextPayload += entry.Data.Length;
        }

        BinaryPrimitives.WriteUInt32BigEndian(
            data.AsSpan(entries.Length * entrySize), lastSentinel);
        return data;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(
                   output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    private static string CreatePs3DataRoot(string root)
    {
        return Directory.CreateDirectory(
            Path.Combine(root, "PS3_GAME", "USRDIR", "DATA")).FullName;
    }

    private static byte[] BuildEmptyXenonDictionary()
    {
        const int tableOffset = 0x20;
        var data = new byte[tableOffset];
        BinaryPrimitives.WriteUInt32BigEndian(data, 0xFACECAA7);
        data[4] = 1;
        data[5] = 0x1C;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), tableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C), tableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x10), uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x18), 0x1C);
        data.AsSpan(0x1C).Fill(0xEF);
        return data;
    }

    private static byte[] BuildSingleXenonArgbDictionary()
    {
        const int tableOffset = 0x20;
        const int recordSize = 32;
        const int auxStride = 40;
        const int dataStart = tableOffset + recordSize + auxStride;
        const int payloadOffset = 4096;
        const int allocationSize = 4096;
        const int surfaceOffset = XenosTiling.SubTileBlockOffset * 4;
        var data = new byte[payloadOffset + allocationSize];

        BinaryPrimitives.WriteUInt32BigEndian(data, 0xFACECAA7);
        data[4] = 1;
        data[5] = 0x1C;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), tableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C), dataStart);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x10), uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x18), 0x1C);
        data.AsSpan(0x1C, tableOffset - 0x1C).Fill(0xEF);

        data[tableOffset] = recordSize / 4;
        data[tableOffset + 1] = recordSize;
        data[tableOffset + 3] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(tableOffset + 4), 0x12345678);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(tableOffset + 8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(tableOffset + 10), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(tableOffset + 28), payloadOffset);

        var auxOffset = tableOffset + recordSize;
        BinaryPrimitives.WriteUInt32BigEndian(
            data.AsSpan(auxOffset + auxStride - 20),
            6u | 2u << 6); // ARGB8888, 32-bit endian swap.
        // The fetch constant requests a 32-bit endian swap. Stored 30,20,10,255
        // becomes A,R,G,B = 255,10,20,30 before RGBA conversion.
        data[payloadOffset + surfaceOffset] = 30;
        data[payloadOffset + surfaceOffset + 1] = 20;
        data[payloadOffset + surfaceOffset + 2] = 10;
        data[payloadOffset + surfaceOffset + 3] = 255;
        return data;
    }

    private static void AssertStoredIndex(
        byte[] linear,
        int blocksX,
        int x,
        int y,
        int expectedStoredIndex)
    {
        const int blockBytes = 16;
        var actual = BinaryPrimitives.ReadInt32LittleEndian(
            linear.AsSpan((y * blocksX + x) * blockBytes));
        Assert.Equal(expectedStoredIndex, actual);
    }

    private sealed class MemoryAssetSource(
        string entryName,
        byte[] data,
        string companionName,
        byte[] companionData,
        string? secondCompanionName = null,
        byte[]? secondCompanionData = null) : AssetSource
    {
        public List<string> CompanionRequests { get; } = [];
        public override string DisplayName => "memory::" + entryName;
        public override string EntryName => entryName;
        public override byte[] ReadBytes() => data;

        public override bool CompanionExists(string nameWithExtension)
        {
            return string.Equals(nameWithExtension, companionName,
                       StringComparison.OrdinalIgnoreCase)
                   || secondCompanionData != null
                   && string.Equals(nameWithExtension, secondCompanionName,
                       StringComparison.OrdinalIgnoreCase);
        }

        public override byte[]? TryReadCompanion(string nameWithExtension)
        {
            CompanionRequests.Add(nameWithExtension);
            if (string.Equals(nameWithExtension, companionName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return companionData;
            }

            return secondCompanionData != null
                   && string.Equals(nameWithExtension, secondCompanionName,
                       StringComparison.OrdinalIgnoreCase)
                ? secondCompanionData
                : null;
        }

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null)
        {
            foreach (var extension in extensions)
            {
                var bytes = TryReadCompanion(stem + extension);
                if (bytes != null) return bytes;
            }

            return null;
        }
    }
}
