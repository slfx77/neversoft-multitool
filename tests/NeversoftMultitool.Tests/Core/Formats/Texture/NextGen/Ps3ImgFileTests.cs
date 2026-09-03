using System.Buffers.Binary;
using System.Security.Cryptography;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Texture.NextGen;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.NextGen;

public sealed class Ps3ImgFileTests(TestPaths paths)
{
    private const string Project8Build =
        "Tony Hawk's Project 8 (2006-10-5, PS3 - Final)";
    private const string ProvingGroundBuild =
        "Tony Hawk's Proving Ground (2007-8-31, PS3 - Final)";

    [Theory]
    [InlineData(0x86)]
    [InlineData(0x88)]
    [InlineData(0xA6)]
    [InlineData(0xA8)]
    public void Parse_LinearLittleEndianDxt_DecodesEveryCorpusFormat(byte format)
    {
        var descriptor = BuildDescriptor(4, 4, format);
        var payload = SolidBlock(format, 0x07E0);

        var result = Ps3ImgFile.Parse(descriptor, payload);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal((4, 4, (uint)format), (texture.Width, texture.Height, texture.Psm));
        Assert.NotNull(texture.Pixels);
        Assert.All(Pixels(texture.Pixels!), pixel =>
            Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), pixel));
    }

    [Fact]
    public void Parse_PreservesTopDownBlockRowOrder()
    {
        var descriptor = BuildDescriptor(4, 8, 0x86);
        var payload = SolidBlock(0x86, 0xF800)
            .Concat(SolidBlock(0x86, 0x001F))
            .ToArray();

        var result = Ps3ImgFile.Parse(descriptor, payload);

        Assert.True(result.Success, result.ErrorMessage);
        var pixels = Assert.Single(result.Textures).Pixels!;
        AssertPixel(pixels, 4, 0, 0, 255, 0, 0);
        AssertPixel(pixels, 4, 0, 7, 0, 0, 255);
    }

    [Fact]
    public void IsPs3Img_RequiresTheExactDescriptorAndFixedFields()
    {
        var valid = BuildDescriptor(4, 4, 0x86);
        Assert.True(Ps3ImgFile.IsPs3Img(valid));
        Assert.False(Ps3ImgFile.IsPs3Img(valid.AsSpan(0, valid.Length - 1)));
        Assert.False(Ps3ImgFile.IsPs3Img([.. valid, 0]));

        int[] fixedFieldOffsets =
        [
            0x04, 0x0C, 0x0E, 0x10, 0x12, 0x14, 0x15, 0x16, 0x17,
            0x24, 0x28, 0x30, 0x31, 0x32, 0x34, 0x38, 0x3A, 0x3C, 0x44
        ];
        foreach (var offset in fixedFieldOffsets)
        {
            var changed = (byte[])valid.Clone();
            changed[offset] ^= 0x01;
            Assert.False(Ps3ImgFile.TryInspect(changed, out _, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Theory]
    [InlineData(7, "truncated")]
    [InlineData(9, "oversized")]
    public void Parse_RejectsPayloadThatIsNotTheExactDeclaredSize(
        int payloadLength,
        string expectedReason)
    {
        var result = Ps3ImgFile.Parse(
            BuildDescriptor(4, 4, 0x86),
            new byte[payloadLength]);

        Assert.False(result.Success);
        Assert.Contains(expectedReason, result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_AssetSource_ResolvesNamedImvCompanion()
    {
        var descriptor = BuildDescriptor(4, 4, 0x86);
        var source = new MemoryAssetSource(
            "SAMPLE.IMG.PS3",
            descriptor,
            "SAMPLE.imv.PS3",
            SolidBlock(0x86, 0xF800));

        var result = Ps3ImgFile.Parse(source, descriptor);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(result.Textures);
        Assert.Equal("SAMPLE.imv.PS3", source.LastCompanionRequest,
            ignoreCase: true);
    }

    [Fact]
    public void PayloadLocator_UsesCompressedTreeMirrorOnlyWhenLocalIsAbsent()
    {
        var root = CreateTempDirectory("ps3-img-mirror");
        try
        {
            var compressed = Path.Combine(root, "DATA", "COMPRESSED", "PS3", "TEX");
            var uncompressed = Path.Combine(root, "DATA", "TEX");
            Directory.CreateDirectory(compressed);
            Directory.CreateDirectory(uncompressed);
            var descriptorPath = Path.Combine(compressed, "shirt.img.ps3");
            var localPayload = Path.Combine(compressed, "shirt.imv.ps3");
            var mirrorPayload = Path.Combine(uncompressed, "shirt.imv.ps3");
            File.WriteAllBytes(descriptorPath, BuildDescriptor(4, 4, 0x86));
            File.WriteAllBytes(mirrorPayload, SolidBlock(0x86, 0xF800));

            var mirrored = Ps3ImgPayloadLocator.Resolve(descriptorPath, 8);
            Assert.True(mirrored.Found, mirrored.Message);
            Assert.Equal(Ps3ImgPayloadSource.UncompressedMirror, mirrored.Source);

            File.WriteAllBytes(localPayload, new byte[7]);
            var localWins = Ps3ImgPayloadLocator.Resolve(descriptorPath, 8);
            Assert.Equal(Ps3ImgPayloadStatus.InvalidSize, localWins.Status);
            Assert.Equal(Ps3ImgPayloadSource.SameDirectory, localWins.Source);
            Assert.Contains("truncated", localWins.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("truncated")]
    public void FormatProbe_RequiresACompletePixelCompanion(string mode)
    {
        var root = CreateTempDirectory("ps3-img-probe");
        try
        {
            var descriptorPath = Path.Combine(root, "green.img.ps3");
            var payloadPath = Path.Combine(root, "green.imv.ps3");
            File.WriteAllBytes(descriptorPath, BuildDescriptor(4, 4, 0x86));
            if (mode == "valid")
                File.WriteAllBytes(payloadPath, SolidBlock(0x86, 0x07E0));
            else if (mode == "truncated")
                File.WriteAllBytes(payloadPath, new byte[7]);

            var probe = FormatProbe.ProbeTexture(descriptorPath);

            if (mode == "valid")
            {
                Assert.Equal(FormatProbe.FormatSupport.Supported, probe.Support);
                Assert.Equal("PlayStation 3 IMG (DXT1)", probe.FormatName);
            }
            else
            {
                Assert.Equal(FormatProbe.FormatSupport.Unsupported, probe.Support);
                Assert.Contains(mode == "missing" ? "not found" : "truncated",
                    probe.UnsupportedReason!, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void XbxTexCommand_ImgPs3_WritesOnePng()
    {
        var root = CreateTempDirectory("ps3-img-cli");
        try
        {
            var descriptorPath = Path.Combine(root, "green.img.ps3");
            var output = Path.Combine(root, "out");
            File.WriteAllBytes(descriptorPath, BuildDescriptor(4, 4, 0x88));
            File.WriteAllBytes(
                Path.Combine(root, "green.imv.ps3"),
                SolidBlock(0x88, 0x07E0));

            Assert.Equal(0, XbxTexCommand.Execute(
                descriptorPath, output, verbose: false,
                TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(output, "green.png")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [CorpusFact]
    public void Parse_RealPs3Images_PinsLinearOrientationAndBlockEndian()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var root = Path.Combine(paths.SampleBuildsDir!, Project8Build, "PS3_GAME", "USRDIR");
        var samples = new[]
        {
            (Path.Combine(root, "DATA", "IMAGES", "LOADINGSCREENS", "loadscrn.img.ps3"),
                1280, 720,
                "813777DE8A393A71A5660AE9745D8D63CC766A00AD39E2D1C8752B62E59BF4A4"),
            (Path.Combine(root, "DATA", "IMAGES", "TAGS", "tag_hawk.img.ps3"),
                128, 128,
                "10CF40DA30F1AE86A39DC615A3BF7CCA6AF47DD02CF706F81E4AC48B5AC68E17")
        };

        foreach (var (file, width, height, sha) in samples)
        {
            Assert.SkipWhen(!File.Exists(file), $"Sample not found: {file}");
            var result = Ps3ImgFile.Parse(file);
            Assert.True(result.Success, $"{file}: {result.ErrorMessage}");
            var texture = Assert.Single(result.Textures);
            Assert.Equal((width, height), (texture.Width, texture.Height));
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(texture.Pixels!)));
        }
    }

    [CorpusFact]
    public void Corpus_All2853DescriptorsResolveOrFailForTwoKnownPhysicalTruncations()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var roots = new[]
        {
            (Project8Build, 1_789),
            (ProvingGroundBuild, 1_064)
        };
        var descriptors = new List<string>();
        var payloadFileCount = 0;
        foreach (var (build, expectedDescriptors) in roots)
        {
            var root = Path.Combine(paths.SampleBuildsDir!, build);
            Assert.SkipWhen(!Directory.Exists(root), $"Build not found: {build}");
            var buildDescriptors = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => file.EndsWith(".img.ps3", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Equal(expectedDescriptors, buildDescriptors.Length);
            descriptors.AddRange(buildDescriptors);
            payloadFileCount += Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Count(file => file.EndsWith(".imv.ps3", StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal(2_853, descriptors.Count);
        Assert.Equal(3_272, payloadFileCount);

        var dxt1 = 0;
        var dxt5 = 0;
        var sourceCounts = new Dictionary<Ps3ImgPayloadSource, int>();
        var invalid = new List<string>();
        foreach (var file in descriptors)
        {
            var descriptor = File.ReadAllBytes(file);
            Assert.True(Ps3ImgFile.TryInspect(descriptor, out var info, out var error),
                $"{file}: {error}");
            if (info.BaseGcmFormat == 0x86) dxt1++;
            else if (info.BaseGcmFormat == 0x88) dxt5++;
            else Assert.Fail($"{file}: unexpected GCM format 0x{info.GcmFormat:X2}");

            var resolution = Ps3ImgPayloadLocator.Resolve(file, info.PayloadSize);
            if (!resolution.Found)
            {
                Assert.Equal(Ps3ImgPayloadStatus.InvalidSize, resolution.Status);
                invalid.Add(Path.GetFileNameWithoutExtension(
                    Path.GetFileNameWithoutExtension(file)));
                var failed = Ps3ImgFile.Parse(file);
                Assert.False(failed.Success);
                Assert.Empty(failed.Textures);
                Assert.Contains("truncated", failed.ErrorMessage!,
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }

            sourceCounts[resolution.Source] = sourceCounts.GetValueOrDefault(resolution.Source) + 1;
            var result = Ps3ImgFile.Parse(descriptor, resolution.Bytes);
            Assert.True(result.Success, $"{file}: {result.ErrorMessage}");
            var texture = Assert.Single(result.Textures);
            Assert.NotNull(texture.Pixels);
            Assert.Equal(checked(info.Width * info.Height * 4), texture.Pixels!.Length);
        }

        Assert.Equal(989, dxt1);
        Assert.Equal(1_864, dxt5);
        Assert.Equal(1_598, sourceCounts.GetValueOrDefault(Ps3ImgPayloadSource.SameDirectory));
        Assert.Equal(997, sourceCounts.GetValueOrDefault(Ps3ImgPayloadSource.ExtractedVramPak));
        Assert.Equal(236, sourceCounts.GetValueOrDefault(Ps3ImgPayloadSource.VramPakArchive));
        Assert.Equal(20, sourceCounts.GetValueOrDefault(Ps3ImgPayloadSource.UncompressedMirror));
        Assert.Equal(2_851, sourceCounts.Values.Sum());
        Assert.Equal(
            ["SHT_BILLABONG01", "SHT_ZUMIEZ02"],
            invalid.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    [CorpusFact]
    public void PakArchive_Project8CasFemWalksPastZeroBytePlaceholder()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var archive = paths.FindSampleFile(Project8Build, "cas_fem_vram.pak.ps3");
        Assert.SkipWhen(archive == null, "cas_fem_vram.pak.ps3 not present");

        var entries = PakArchive.GetTypedEntries(archive!);

        Assert.Equal(14, entries.Count(entry =>
            entry.TypeHash == Ps3ImgPayloadLocator.PayloadType));
        Assert.DoesNotContain(entries, entry => entry.Entry.Size == 0);
    }

    private static byte[] BuildDescriptor(int width, int height, byte format)
    {
        var baseFormat = (byte)(format & ~0x20);
        var blockBytes = baseFormat == 0x86 ? 8 : 16;
        var payloadSize = checked(((width + 3) / 4) * ((height + 3) / 4) * blockBytes);
        var descriptor = new byte[Ps3ImgFile.DescriptorSize];
        BinaryPrimitives.WriteUInt32BigEndian(descriptor, Ps3ImgFile.Magic);
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(0x04), 0x55434F44);
        BinaryPrimitives.WriteUInt16BigEndian(descriptor.AsSpan(0x08), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(descriptor.AsSpan(0x0A), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(descriptor.AsSpan(0x0C), 1);
        BinaryPrimitives.WriteUInt16BigEndian(descriptor.AsSpan(0x0E), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(descriptor.AsSpan(0x10), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(descriptor.AsSpan(0x12), 1);
        descriptor[0x14] = 1;
        descriptor[0x15] = baseFormat == 0x86 ? (byte)4 : (byte)8;
        descriptor[0x16] = baseFormat == 0x86 ? (byte)1 : (byte)5;
        descriptor[0x17] = 0x18;
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(0x20), (uint)payloadSize);
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(0x24), 1);
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(0x28), 0x30);
        descriptor[0x30] = format;
        descriptor[0x31] = 1;
        descriptor[0x32] = 2;
        BinaryPrimitives.WriteUInt32BigEndian(descriptor.AsSpan(0x34), 0x0000AAE4);
        BinaryPrimitives.WriteUInt16BigEndian(descriptor.AsSpan(0x38), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(descriptor.AsSpan(0x3A), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(descriptor.AsSpan(0x3C), 1);
        return descriptor;
    }

    private static byte[] SolidBlock(byte format, ushort rgb565)
    {
        var dxt1 = (format & ~0x20) == 0x86;
        var block = new byte[dxt1 ? 8 : 16];
        var colorOffset = dxt1 ? 0 : 8;
        if (!dxt1)
        {
            block[0] = 0xFF;
            block[1] = 0;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(colorOffset), rgb565);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(colorOffset + 2), 0);
        return block;
    }

    private static IEnumerable<(byte R, byte G, byte B, byte A)> Pixels(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
            yield return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    private static void AssertPixel(
        byte[] rgba, int width, int x, int y, byte r, byte g, byte b)
    {
        var offset = (y * width + x) * 4;
        Assert.Equal((r, g, b, (byte)255),
            (rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]));
    }

    private static string CreateTempDirectory(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nmt-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class MemoryAssetSource(
        string entryName,
        byte[] data,
        string companionName,
        byte[] companionData) : AssetSource
    {
        public string? LastCompanionRequest { get; private set; }
        public override string DisplayName => "memory::" + entryName;
        public override string EntryName => entryName;
        public override byte[] ReadBytes() => data;

        public override bool CompanionExists(string nameWithExtension)
        {
            return string.Equals(nameWithExtension, companionName,
                StringComparison.OrdinalIgnoreCase);
        }

        public override byte[]? TryReadCompanion(string nameWithExtension)
        {
            LastCompanionRequest = nameWithExtension;
            return CompanionExists(nameWithExtension) ? companionData : null;
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
