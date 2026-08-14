using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class SkaIntermediateParserTests(TestPaths paths)
{
    private const string ThugPs2Build = "Tony Hawk's Underground (2003-10-2, PS2 - Final)";
    private const uint FlagIntermediate = 1u << 30;
    private const uint FlagUncompressed = 1u << 29;
    private const uint FlagCompressedTime = 1u << 26;
    private const uint FlagPreRotatedRoot = 1u << 25;
    private const uint FlagCutsceneData = 1u << 20;
    private const uint SyntheticFlags = FlagIntermediate | FlagUncompressed |
                                          FlagCompressedTime | FlagPreRotatedRoot |
                                          FlagCutsceneData;
    private const uint SkeletonChecksum = 0x10203040;
    private const uint RootChecksum = 0x11111111;
    private const uint ChildChecksum = 0x22222222;

    [Fact]
    public void Parse_SyntheticIntermediate_PreservesHierarchyRawFramesAndBothQuaternionConventions()
    {
        var data = BuildFixture();

        Assert.True(SkaFile.IsSkaFile(data));
        Assert.Null(SkaFile.TryProbe(data)); // inspection-only; never an AnimationDiscovery clip

        var directProbe = SkaIntermediateParser.TryProbe(data);
        Assert.NotNull(directProbe);
        Assert.Equal(2, directProbe!.BoneCount);
        Assert.Equal(2f, directProbe.Duration);

        var animation = SkaFile.Parse(data);
        Assert.True(animation.IsIntermediateFormat);
        Assert.Equal(3u, animation.Version);
        Assert.Equal(SyntheticFlags, animation.Flags);
        Assert.Equal(2f, animation.Duration);
        Assert.Equal(2, animation.BoneTracks.Length);
        Assert.Empty(animation.CustomKeys);

        var metadata = Assert.IsType<SkaIntermediateMetadata>(animation.IntermediateMetadata);
        Assert.Equal(SkeletonChecksum, metadata.SkeletonChecksum);
        Assert.Equal([RootChecksum, ChildChecksum], metadata.BoneNameChecksums);
        Assert.Equal([0u, RootChecksum], metadata.ParentNameChecksums);
        Assert.Equal([ChildChecksum, RootChecksum], metadata.FlipNameChecksums);
        Assert.Equal([0u, 60u], metadata.RotationFrames[0]);
        Assert.Equal([30u], metadata.RotationFrames[1]);
        Assert.Equal([0u], metadata.TranslationFrames[0]);
        Assert.Equal([0u, 120u], metadata.TranslationFrames[1]);

        Assert.Equal(RootChecksum, animation.BoneTracks[0].BoneNameChecksum);
        Assert.Equal(ChildChecksum, animation.BoneTracks[1].BoneNameChecksum);
        Assert.Equal(1f, animation.BoneTracks[0].RotationKeys[1].Time);
        Assert.Equal(2f, animation.BoneTracks[1].TranslationKeys[1].Time);

        var source = metadata.SourceRotations[0][1];
        var engine = animation.BoneTracks[0].RotationKeys[1].Rotation;
        Assert.Equal(new Vector4(0f, 0f, 0.6f, 0.8f), source);
        Assert.Equal(new Quaternion(0f, 0f, -0.6f, 0.8f), engine);

        using var json = JsonDocument.Parse(
            SkaIntermediateJsonExporter.Serialize("sample.ska", animation));
        var root = json.RootElement;
        Assert.Equal(SkaIntermediateJsonExporter.SchemaName,
            root.GetProperty("schema").GetString());
        Assert.Equal("0x10203040", root.GetProperty("skeletonChecksum").GetString());
        Assert.Equal(2, root.GetProperty("boneCount").GetInt32());
        Assert.Equal(3, root.GetProperty("rotationKeyCount").GetInt32());
        Assert.Equal("0x11111111", root.GetProperty("bones")[0]
            .GetProperty("nameChecksum").GetString());
        Assert.Equal("0x22222222", root.GetProperty("bones")[0]
            .GetProperty("flipChecksum").GetString());
        var jsonKey = root.GetProperty("bones")[0].GetProperty("rotationKeys")[1];
        Assert.Equal(60u, jsonKey.GetProperty("frame").GetUInt32());
        Assert.Equal(0.6f, jsonKey.GetProperty("sourceQuaternionXyzw")[2].GetSingle());
        Assert.Equal(-0.6f, jsonKey.GetProperty("engineQuaternionXyzw")[2].GetSingle());
    }

    [Fact]
    public void Write_InvalidMetadata_DoesNotCreateOutputArtifacts()
    {
        var animation = SkaFile.Parse(BuildFixture());
        var metadata = Assert.IsType<SkaIntermediateMetadata>(animation.IntermediateMetadata);
        metadata.RotationFrames[0] = [];

        var outputRoot = Path.Combine(
            Path.GetTempPath(),
            "NsMultitool_Test_IntermediateWrite_" + Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(outputRoot, "nested", "sample.ska.json");

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                SkaIntermediateJsonExporter.Write(outputPath, "sample.ska", animation));

            Assert.False(Directory.Exists(outputRoot));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public void AnimationDiscovery_DoesNotAdvertiseIntermediateStreams()
    {
        var temp = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_IntermediateDiscovery_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllBytes(Path.Combine(temp, "master.ska"), BuildFixture());

            var probes = AnimationDiscovery.FindInDirectory(
                temp, 2, TestContext.Current.CancellationToken);

            Assert.Empty(probes);
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void CharacterExportRoute_DoesNotAcceptIntermediateStream()
    {
        Assert.Null(SkaFile.ParseExportableCharacterAnimation(BuildFixture()));
    }

    [Fact]
    public void SkaCommand_DefaultPoseRoute_DoesNotAcceptIntermediateStream()
    {
        Assert.False(SkaCommand.IsUsableDefaultPose(SkaFile.Parse(BuildFixture())));
    }

    [Theory]
    [InlineData("foo.bar.ska", "foo.bar.ska.json")]
    [InlineData("foo.bar.ska.ps2", "foo.bar.ska.json")]
    public void SkaCommand_OutputName_PreservesDotsBeforeSka(
        string input, string expected)
    {
        Assert.Equal(expected, SkaCommand.GetCustomKeyOutputName(input));
    }

    [Fact]
    public void SkaCommand_WithSkeletonAndSkin_WritesJsonButNeverGlb()
    {
        var temp = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_IntermediateCli_" + Guid.NewGuid().ToString("N"));
        var output = Path.Combine(temp, "out");
        Directory.CreateDirectory(temp);
        try
        {
            var ska = Path.Combine(temp, "master.ska");
            var ske = Path.Combine(temp, "master.ske");
            var skin = Path.Combine(temp, "master.skin.ps2");
            File.WriteAllBytes(ska, BuildFixture());
            File.WriteAllBytes(ske, BuildThps4Skeleton());
            File.WriteAllBytes(skin, BuildEmptyPs2Scene());

            var exitCode = SkaCommand.Execute(
                ska, output, false, ske, skin, null, null);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(output, "master.ska.json")));
            Assert.False(File.Exists(Path.Combine(output, "master.glb")));
            Assert.Empty(Directory.GetFiles(output, "*.glb", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Fact]
    public void SkaCommand_DirectoryMode_PreservesRelativePathsForDuplicateNames()
    {
        var temp = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_IntermediatePaths_" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(temp, "input");
        var output = Path.Combine(temp, "out");
        Directory.CreateDirectory(Path.Combine(input, "one"));
        Directory.CreateDirectory(Path.Combine(input, "two"));
        try
        {
            File.WriteAllBytes(Path.Combine(input, "one", "shared.ska"), BuildFixture());
            File.WriteAllBytes(Path.Combine(input, "two", "shared.ska"), BuildFixture());
            File.WriteAllBytes(Path.Combine(input, "foo.bar.ska"), BuildFixture());
            File.WriteAllBytes(Path.Combine(input, "foo.baz.ska"), BuildFixture());

            var exitCode = SkaCommand.Execute(
                input, output, false, null, null, null, null);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(output, "one", "shared.ska.json")));
            Assert.True(File.Exists(Path.Combine(output, "two", "shared.ska.json")));
            Assert.True(File.Exists(Path.Combine(output, "foo.bar.ska.json")));
            Assert.True(File.Exists(Path.Combine(output, "foo.baz.ska.json")));
            Assert.Empty(Directory.GetFiles(output, "*.glb", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("badVersion")]
    [InlineData("missingIntermediate")]
    [InlineData("missingCompressedTime")]
    [InlineData("boneCount")]
    [InlineData("boneCountMax")]
    [InlineData("qHeaderMax")]
    [InlineData("tHeaderMax")]
    [InlineData("qCount")]
    [InlineData("extraByte")]
    [InlineData("truncated")]
    [InlineData("customKey")]
    [InlineData("unknownFlags")]
    [InlineData("zeroName")]
    [InlineData("duplicateName")]
    [InlineData("extraRoot")]
    [InlineData("forwardParent")]
    [InlineData("unknownParent")]
    [InlineData("unknownFlip")]
    [InlineData("selfFlip")]
    [InlineData("nonFiniteDuration")]
    [InlineData("negativeDuration")]
    [InlineData("nonMonotone")]
    [InlineData("duplicateFrame")]
    [InlineData("nonFinite")]
    [InlineData("nonUnit")]
    [InlineData("pastDuration")]
    public void Parse_MalformedIntermediate_IsRejected(string mutation)
    {
        var data = BuildFixture();
        switch (mutation)
        {
            case "checksum":
                WriteU32(data, 32, 0xDEADBEEF);
                break;
            case "badVersion":
                WriteU32(data, 0, 4);
                break;
            case "missingIntermediate":
                WriteU32(data, 4, SyntheticFlags & ~FlagIntermediate);
                break;
            case "missingCompressedTime":
                WriteU32(data, 4, SyntheticFlags & ~FlagCompressedTime);
                break;
            case "boneCount":
                WriteU32(data, 36, 3);
                break;
            case "boneCountMax":
                WriteU32(data, 16, uint.MaxValue);
                break;
            case "qHeaderMax":
                WriteU32(data, 20, uint.MaxValue);
                break;
            case "tHeaderMax":
                WriteU32(data, 24, uint.MaxValue);
                break;
            case "qCount":
                WriteU32(data, 64, 1); // per-bone sum becomes 2, header remains 3
                break;
            case "extraByte":
                Array.Resize(ref data, data.Length + 1);
                break;
            case "truncated":
                Array.Resize(ref data, data.Length - 1);
                break;
            case "customKey":
                WriteU32(data, 28, 1);
                Array.Resize(ref data, data.Length + 12);
                break;
            case "unknownFlags":
                WriteU32(data, 4, SyntheticFlags | 1u);
                break;
            case "zeroName":
                WriteU32(data, 40, 0);
                break;
            case "duplicateName":
                WriteU32(data, 44, RootChecksum);
                break;
            case "extraRoot":
                WriteU32(data, 52, 0);
                break;
            case "forwardParent":
                WriteU32(data, 48, ChildChecksum);
                break;
            case "unknownParent":
                WriteU32(data, 52, 0x33333333);
                break;
            case "unknownFlip":
                WriteU32(data, 56, 0x33333333);
                break;
            case "selfFlip":
                WriteU32(data, 56, RootChecksum);
                break;
            case "nonFiniteDuration":
                WriteF32(data, 8, float.NaN);
                break;
            case "negativeDuration":
                WriteF32(data, 8, -1f);
                break;
            case "nonMonotone":
                WriteU32(data, 92, 0); // bone 0's second Q frame follows frame 0; keep equal first
                WriteU32(data, 72, 10);
                break;
            case "duplicateFrame":
                WriteU32(data, 92, 0);
                break;
            case "nonFinite":
                WriteF32(data, 76, float.NaN);
                break;
            case "nonUnit":
                WriteF32(data, 88, 2f); // W of first identity key
                break;
            case "pastDuration":
                WriteU32(data, 72, 121); // duration is exactly 120 frames
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        var semanticKeyFailure = mutation is
            "nonMonotone" or "duplicateFrame" or "nonFinite" or
            "nonUnit" or "pastDuration";
        Assert.Equal(semanticKeyFailure, SkaFile.IsSkaFile(data));
        Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));
    }

    [Fact]
    public void Parse_UncompressedFlagIsOptional()
    {
        var data = BuildFixture();
        WriteU32(data, 4, SyntheticFlags & ~FlagUncompressed);

        var animation = SkaFile.Parse(data);

        Assert.True(animation.IsIntermediateFormat);
        Assert.Equal(2, animation.BoneTracks.Length);
    }

    [CorpusFact]
    public void Parse_RealFixtures_PinDenseV3AndSparseV2Layouts()
    {
        var fl = ReadCutMember("FL_01.cut", 0x545F29AA);
        Assert.Equal(812_824, fl.Length);
        var dense = SkaFile.Parse(fl);
        Assert.Equal(3u, dense.Version);
        Assert.Equal(0x66100000u, dense.Flags);
        Assert.Equal(39.5f, dense.Duration);
        Assert.Equal(24, dense.BoneTracks.Length);
        Assert.Equal(22_564, dense.BoneTracks.Sum(static track => track.RotationKeys.Length));
        Assert.Equal(22_564, dense.BoneTracks.Sum(static track => track.TranslationKeys.Length));

        var scene1 = ReadCutMember("Scene1.cut", 0x1BE55811);
        Assert.Equal(153_396, scene1.Length);
        var sparse = SkaFile.Parse(scene1);
        Assert.Equal(2u, sparse.Version);
        Assert.Equal(0x44000000u, sparse.Flags);
        Assert.Equal(55, sparse.BoneTracks.Length);
        Assert.Equal(7_316, sparse.BoneTracks.Sum(static track => track.RotationKeys.Length));
        Assert.Equal(371, sparse.BoneTracks.Sum(static track => track.TranslationKeys.Length));
    }

    [CorpusFact]
    public void EveryThugBareCutIntermediate_ParsesExactlyAndPairsWithCompiledMember()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var bareCuts = paths.FindSampleFiles(ThugPs2Build, "*.cut")
            .Where(static file => file.EndsWith(".cut", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();
        var compiledCuts = paths.FindSampleFiles(ThugPs2Build, "*.cut.ps2")
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(43, bareCuts.Length);
        Assert.Equal(43, compiledCuts.Length);

        var compiled = new Dictionary<string, CompiledHeader>(StringComparer.OrdinalIgnoreCase);
        foreach (var cut in compiledCuts)
        {
            var data = File.ReadAllBytes(cut);
            foreach (var entry in CutArchive.GetFileList(cut)
                         .Where(static entry => entry.Name.EndsWith(".ska", StringComparison.OrdinalIgnoreCase)))
            {
                var offset = checked((int)entry.Offset);
                compiled.Add(PairKey(cut, entry.Crc), new CompiledHeader(
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)),
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4)),
                    BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 8)),
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 12))));
            }
        }
        Assert.Equal(194, compiled.Count);

        var parsed = 0;
        var v2 = 0;
        var v3 = 0;
        long qTotal = 0;
        long tTotal = 0;
        var flagCounts = new Dictionary<(uint Version, uint Flags), int>();
        foreach (var cut in bareCuts)
        {
            var data = File.ReadAllBytes(cut);
            foreach (var entry in CutArchive.GetFileList(cut)
                         .Where(static entry => entry.Name.EndsWith(".ska", StringComparison.OrdinalIgnoreCase)))
            {
                var member = data.AsSpan(
                    checked((int)entry.Offset), checked((int)entry.Size)).ToArray();

                // Keep the corpus oracle independent of the production parser:
                // verify both skeleton headers, exact stream ownership, and EOF
                // from the raw grammar before asking SkaFile to interpret it.
                var rawVersion = BinaryPrimitives.ReadUInt32LittleEndian(member);
                var rawFlags = BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(4));
                var rawDuration = BinaryPrimitives.ReadSingleLittleEndian(member.AsSpan(8));
                var rawSkeletonChecksum = BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(12));
                var rawBoneCount = BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(16));
                var rawQCount = BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(20));
                var rawTCount = BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(24));
                Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(28)));
                Assert.Equal(rawSkeletonChecksum,
                    BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(32)));
                Assert.Equal(rawBoneCount,
                    BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(36)));
                Assert.Equal(member.Length, checked((int)(40L + 20L * rawBoneCount +
                    20L * rawQCount + 16L * rawTCount)));

                var rawQCountsOffset = checked((int)(40L + 12L * rawBoneCount));
                var rawQCountsTotal = 0L;
                for (var bone = 0; bone < rawBoneCount; bone++)
                {
                    rawQCountsTotal += BinaryPrimitives.ReadUInt32LittleEndian(
                        member.AsSpan(checked(rawQCountsOffset + bone * 4)));
                }
                Assert.Equal(rawQCount, checked((uint)rawQCountsTotal));

                var rawTCountsOffset = checked((int)(40L + 16L * rawBoneCount +
                                                       20L * rawQCount));
                var rawTCountsTotal = 0L;
                for (var bone = 0; bone < rawBoneCount; bone++)
                {
                    rawTCountsTotal += BinaryPrimitives.ReadUInt32LittleEndian(
                        member.AsSpan(checked(rawTCountsOffset + bone * 4)));
                }
                Assert.Equal(rawTCount, checked((uint)rawTCountsTotal));

                Assert.True(SkaFile.IsSkaFile(member));
                var animation = SkaFile.Parse(member);
                var metadata = Assert.IsType<SkaIntermediateMetadata>(animation.IntermediateMetadata);
                Assert.Equal(animation.BoneTracks.Length, metadata.BoneNameChecksums.Length);
                Assert.Equal(animation.BoneTracks.Length, metadata.ParentNameChecksums.Length);
                Assert.Equal(animation.BoneTracks.Length, metadata.FlipNameChecksums.Length);
                Assert.Empty(animation.CustomKeys);

                var qCount = animation.BoneTracks.Sum(static track => track.RotationKeys.Length);
                var tCount = animation.BoneTracks.Sum(static track => track.TranslationKeys.Length);
                Assert.Equal(rawVersion, animation.Version);
                Assert.Equal(rawFlags, animation.Flags);
                Assert.Equal(rawDuration, animation.Duration);
                Assert.Equal(rawBoneCount, checked((uint)animation.BoneTracks.Length));
                Assert.Equal(rawQCount, checked((uint)qCount));
                Assert.Equal(rawTCount, checked((uint)tCount));
                qTotal += rawQCount;
                tTotal += rawTCount;
                parsed++;
                if (rawVersion == 2) v2++;
                if (rawVersion == 3) v3++;
                var flagKey = (rawVersion, rawFlags);
                flagCounts[flagKey] = flagCounts.GetValueOrDefault(flagKey) + 1;

                Assert.True(compiled.Remove(PairKey(cut, entry.Crc), out var partner));
                Assert.Equal(1u, partner.Version);
                Assert.Equal(0x06800000u, partner.Flags);
                Assert.Equal(rawDuration, partner.Duration);
                Assert.Equal(rawBoneCount, partner.BoneCount);
            }
        }

        Assert.Empty(compiled);
        Assert.Equal(194, parsed);
        Assert.Equal(7, v2);
        Assert.Equal(187, v3);
        Assert.Equal(4_588_265, qTotal);
        Assert.Equal(6_079_925, tTotal);
        Assert.Equal(133, flagCounts[(3, 0x66100000)]);
        Assert.Equal(54, flagCounts[(3, 0x46100000)]);
        Assert.Equal(3, flagCounts[(2, 0x44000000)]);
        Assert.Equal(2, flagCounts[(2, 0x66000000)]);
        Assert.Equal(1, flagCounts[(2, 0x46000000)]);
        Assert.Equal(1, flagCounts[(2, 0x66100000)]);
    }

    private byte[] ReadCutMember(string fileName, uint crc)
    {
        var cut = paths.FindSampleFile(ThugPs2Build, fileName);
        Assert.SkipWhen(cut == null, $"{fileName} not found in sample builds");
        var entry = Assert.Single(
            CutArchive.GetFileList(cut!),
            entry => entry.Crc == crc &&
                     entry.Name.EndsWith(".ska", StringComparison.OrdinalIgnoreCase));
        var data = File.ReadAllBytes(cut!);
        return data.AsSpan(
            checked((int)entry.Offset), checked((int)entry.Size)).ToArray();
    }

    private static string PairKey(string cutPath, uint crc)
    {
        var name = Path.GetFileName(cutPath);
        var suffixLength = name.EndsWith(".cut.ps2", StringComparison.OrdinalIgnoreCase) ? 8 : 4;
        return $"{name[..^suffixLength]}/{crc:X8}";
    }

    private static byte[] BuildFixture()
    {
        const int boneCount = 2;
        const int qCount = 3;
        const int tCount = 3;
        var data = new byte[40 + 20 * boneCount + 20 * qCount + 16 * tCount];
        WriteU32(data, 0, 3);
        WriteU32(data, 4, SyntheticFlags);
        WriteF32(data, 8, 2f);
        WriteU32(data, 12, SkeletonChecksum);
        WriteU32(data, 16, boneCount);
        WriteU32(data, 20, qCount);
        WriteU32(data, 24, tCount);
        WriteU32(data, 28, 0);

        WriteU32(data, 32, SkeletonChecksum);
        WriteU32(data, 36, boneCount);
        WriteU32(data, 40, RootChecksum);
        WriteU32(data, 44, ChildChecksum);
        WriteU32(data, 48, 0);
        WriteU32(data, 52, RootChecksum);
        WriteU32(data, 56, ChildChecksum);
        WriteU32(data, 60, RootChecksum);
        WriteU32(data, 64, 2);
        WriteU32(data, 68, 1);

        var offset = 72;
        WriteQ(data, ref offset, 0, 0f, 0f, 0f, 1f);
        WriteQ(data, ref offset, 60, 0f, 0f, 0.6f, 0.8f);
        WriteQ(data, ref offset, 30, 0f, 0f, 0f, 1f);
        WriteU32(data, offset, 1);
        WriteU32(data, offset + 4, 2);
        offset += 8;
        WriteT(data, ref offset, 0, 1f, 2f, 3f);
        WriteT(data, ref offset, 0, 4f, 5f, 6f);
        WriteT(data, ref offset, 120, 7f, 8f, 9f);
        Assert.Equal(data.Length, offset);
        return data;
    }

    private static byte[] BuildThps4Skeleton()
    {
        var data = new byte[8 + 2 * 12];
        WriteU32(data, 0, SkeletonChecksum);
        WriteU32(data, 4, 2);
        WriteU32(data, 8, RootChecksum);
        WriteU32(data, 12, ChildChecksum);
        WriteU32(data, 16, 0);
        WriteU32(data, 20, RootChecksum);
        WriteU32(data, 24, ChildChecksum);
        WriteU32(data, 28, RootChecksum);
        return data;
    }

    private static byte[] BuildEmptyPs2Scene()
    {
        var data = new byte[24];
        WriteU32(data, 0, 3); // material version
        WriteU32(data, 4, 4); // mesh version
        WriteU32(data, 8, 1); // vertex version
        WriteU32(data, 12, 0); // materials
        WriteU32(data, 16, 0); // groups
        WriteU32(data, 20, 0); // total meshes
        return data;
    }

    private static void WriteQ(
        byte[] data, ref int offset, uint frame, float x, float y, float z, float w)
    {
        WriteU32(data, offset, frame);
        WriteF32(data, offset + 4, x);
        WriteF32(data, offset + 8, y);
        WriteF32(data, offset + 12, z);
        WriteF32(data, offset + 16, w);
        offset += 20;
    }

    private static void WriteT(
        byte[] data, ref int offset, uint frame, float x, float y, float z)
    {
        WriteU32(data, offset, frame);
        WriteF32(data, offset + 4, x);
        WriteF32(data, offset + 8, y);
        WriteF32(data, offset + 12, z);
        offset += 16;
    }

    private static void WriteU32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);

    private static void WriteF32(byte[] data, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value);

    private readonly record struct CompiledHeader(
        uint Version, uint Flags, float Duration, uint BoneCount);
}
