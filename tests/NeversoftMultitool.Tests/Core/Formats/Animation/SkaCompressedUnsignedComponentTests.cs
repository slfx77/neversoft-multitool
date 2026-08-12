using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class SkaCompressedUnsignedComponentTests(TestPaths paths)
{
    private const string ThugPs2Build = "Tony Hawk's Underground (2003-10-2, PS2 - Final)";
    private const uint ExtSka = 0xEAB51346;
    private const uint FlagPreRotatedRoot = 1u << 25;

    [Fact]
    public void DecodeCompressedQKeys_ByteWidthComponentsUseUnsignedCharSemantics()
    {
        // Compressed key, all three components byte-width, timestamp 5.
        // THUG reads these via unsigned char*, so high-bit values remain
        // +128/+254/+255 when promoted to the component's signed short.
        byte[] data = [0x05, 0x78, 0x80, 0xFE, 0xFF];
        var offset = 0;

        var key = Assert.Single(SkaCompressedKeyDecoders.DecodeCompressedQKeys(
            data, ref offset, data.Length, null));

        Assert.Equal(data.Length, offset);
        Assert.Equal(5f / 60f, key.Time);
        // The shared SKA IR conjugates source quaternions, hence -X/-Y/-Z.
        Assert.Equal(-128f / 16384f, key.Rotation.X);
        Assert.Equal(-254f / 16384f, key.Rotation.Y);
        Assert.Equal(-255f / 16384f, key.Rotation.Z);
    }

    [Fact]
    public void DecodeCompressedQKeys_WideLiteralsStaySignedWhileNarrowLiteralIsUnsigned()
    {
        // Only Y is byte-width. X=-128 and Z=-256 remain signed s16 values;
        // Y=0x80 promotes to positive 128 under the runtime grammar.
        byte[] data = [0x07, 0x50, 0x80, 0xFF, 0x80, 0x00, 0xFF];
        var offset = 0;

        var key = Assert.Single(SkaCompressedKeyDecoders.DecodeCompressedQKeys(
            data, ref offset, data.Length, null));

        Assert.Equal(data.Length, offset);
        Assert.Equal(128f / 16384f, key.Rotation.X);
        Assert.Equal(-128f / 16384f, key.Rotation.Y);
        Assert.Equal(256f / 16384f, key.Rotation.Z);
    }

    [Fact]
    public void CompressedLookupBytesRemainUnsignedIndicesWithSignedTableComponents()
    {
        var qEntries = new SkaCompressEntry[256];
        var tEntries = new SkaCompressEntry[256];
        qEntries[0xFE] = new SkaCompressEntry(-128, 256, -512);
        tEntries[0xFE] = new SkaCompressEntry(-32, 64, -96);
        var table = new SkaCompressTable { Q48 = qEntries, T48 = tEntries };

        byte[] qData = [0x09, 0x40, 0xFE];
        var qOffset = 0;
        var q = Assert.Single(SkaCompressedKeyDecoders.DecodeCompressedQKeys(
            qData, ref qOffset, qData.Length, table));
        Assert.Equal(128f / 16384f, q.Rotation.X);
        Assert.Equal(-256f / 16384f, q.Rotation.Y);
        Assert.Equal(512f / 16384f, q.Rotation.Z);

        // T has no byte-width literal component branch. Its high-bit byte is
        // only an unsigned table index; table entries themselves remain s16.
        byte[] tData = [0xC9, 0xFE];
        var tOffset = 0;
        var t = Assert.Single(SkaCompressedKeyDecoders.DecodeCompressedTKeys(
            tData, ref tOffset, tData.Length, table));
        Assert.Equal(new Vector3(-1f, 2f, -3f), t.Translation);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void DecodeCompressedQKeys_DirectKeyCannotCrossDeclaredTrackEnd(int end)
    {
        byte[] data = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        var exception = Assert.Throws<InvalidDataException>(() => DecodeQ(data, 0, end));

        Assert.Contains("track boundary", exception.Message);
    }

    [Fact]
    public void DecodeCompressedQKeys_NarrowComponentCannotCrossDeclaredTrackEnd()
    {
        // All components are byte-width; the declared range ends before Z,
        // while the backing buffer deliberately contains a tempting next byte.
        byte[] data = [0x00, 0x78, 0x01, 0x02, 0x03];

        var exception = Assert.Throws<InvalidDataException>(() => DecodeQ(data, 0, 4));

        Assert.Contains("Q Z component", exception.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void DecodeCompressedTKeys_DirectKeyCannotCrossDeclaredTrackEnd(int end)
    {
        // Full timestamp plus three direct s16 components.
        byte[] data = [0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

        var exception = Assert.Throws<InvalidDataException>(() => DecodeT(data, 0, end));

        Assert.Contains("track boundary", exception.Message);
    }

    [Fact]
    public void CompressedLookupIndices_CannotCrossDeclaredTrackEnd()
    {
        byte[] qData = [0x00, 0x40, 0x7F];
        byte[] tData = [0xC0, 0x7F];

        var qException = Assert.Throws<InvalidDataException>(() => DecodeQ(qData, 0, 2));
        var tException = Assert.Throws<InvalidDataException>(() => DecodeT(tData, 0, 1));

        Assert.Contains("Q lookup index", qException.Message);
        Assert.Contains("T lookup index", tException.Message);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 2)]
    public void CompressedKeyDecoders_RejectInvalidInitialRanges(int offset, int end)
    {
        byte[] data = [0x00];

        Assert.Throws<InvalidDataException>(() => DecodeQ(data, offset, end));
        Assert.Throws<InvalidDataException>(() => DecodeT(data, offset, end));
    }

    [Fact]
    public void CompressedDirectKeys_ExactDeclaredRangesRemainValid()
    {
        byte[] qData = [0x05, 0x00, 0x00, 0x10, 0x00, 0xE0, 0x00, 0x08];
        var qOffset = 0;
        var q = Assert.Single(SkaCompressedKeyDecoders.DecodeCompressedQKeys(
            qData, ref qOffset, qData.Length, null));

        Assert.Equal(qData.Length, qOffset);
        Assert.Equal(5f / 60f, q.Time);
        Assert.Equal(new Vector3(-0.25f, 0.5f, -0.125f),
            new Vector3(q.Rotation.X, q.Rotation.Y, q.Rotation.Z));

        byte[] tData = [0x45, 0x20, 0x00, 0xC0, 0xFF, 0x60, 0x00];
        var tOffset = 0;
        var t = Assert.Single(SkaCompressedKeyDecoders.DecodeCompressedTKeys(
            tData, ref tOffset, tData.Length, null));

        Assert.Equal(tData.Length, tOffset);
        Assert.Equal(5f / 60f, t.Time);
        Assert.Equal(new Vector3(1f, -2f, 3f), t.Translation);
    }

    [CorpusFact]
    public void ThugCompiledCut_QKeysMatchBareAuthoringValues_WithUnsignedByteLiterals()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var buildRoot = Path.Combine(paths.SampleBuildsDir!, ThugPs2Build);
        var qTablePath = Path.Combine(buildRoot, "SKATE5", "Pre", "Bits", "anims", "standardkeyQ.bin");
        var tTablePath = Path.Combine(buildRoot, "SKATE5", "Pre", "Bits", "anims", "standardkeyT.bin");
        var table = SkaCompressTable.TryLoad(qTablePath, tTablePath);
        Assert.NotNull(table);

        var bareCuts = paths.FindSampleFiles(ThugPs2Build, "*.cut")
            .Where(static path => path.EndsWith(".cut", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(43, bareCuts.Length);

        long retainedKeys = 0;
        long comparedKeys = 0;
        long memberCount = 0;
        long tableLookupKeys = 0;
        long variableKeys = 0;
        long directKeys = 0;
        long narrowComponents = 0;
        long highBitNarrowComponents = 0;
        var maxXyzDelta = 0f;

        foreach (var bareCut in bareCuts)
        {
            var compiledCut = bareCut + ".ps2";
            Assert.True(File.Exists(compiledCut), $"missing partner for {bareCut}");
            var bareMembers = ReadSkaMembers(bareCut);
            var compiledMembers = ReadSkaMembers(compiledCut);
            Assert.Equal(bareMembers.Keys.Order(), compiledMembers.Keys.Order());

            foreach (var (nameChecksum, bareData) in bareMembers)
            {
                memberCount++;
                var compiledData = compiledMembers[nameChecksum];
                var bare = ReadBareRotations(bareData);
                var compiled = SkaFile.Parse(compiledData, table);
                Assert.Equal(bare.Tracks.Length, compiled.BoneTracks.Length);

                CountCompiledQEncodings(
                    compiledData,
                    ref tableLookupKeys,
                    ref variableKeys,
                    ref directKeys,
                    ref narrowComponents,
                    ref highBitNarrowComponents);

                for (var bone = 0; bone < compiled.BoneTracks.Length; bone++)
                {
                    var rawByFrame = bare.Tracks[bone].ToDictionary(
                        static key => key.Frame, static key => key.Rotation);
                    foreach (var key in compiled.BoneTracks[bone].RotationKeys)
                    {
                        retainedKeys++;
                        var frame = checked((uint)Math.Round((double)key.Time * 60d));
                        Assert.True(rawByFrame.TryGetValue(frame, out var raw),
                            $"compiled frame {frame} missing from bare track {bone}");

                        // Three v2 members lack PREROTATEDROOT; compilation adds
                        // the root conversion, so their single root Q key is not
                        // a representation-level quantization comparison.
                        if ((bare.Flags & FlagPreRotatedRoot) == 0 && bone == 0)
                            continue;

                        comparedKeys++;
                        var decoded = key.Rotation;
                        // SkaFile's ordinary IR conjugates the source quaternion.
                        maxXyzDelta = Math.Max(maxXyzDelta,
                            Math.Max(Math.Abs(raw.X + decoded.X),
                                Math.Max(Math.Abs(raw.Y + decoded.Y),
                                    Math.Abs(raw.Z + decoded.Z))));
                    }
                }
            }
        }

        Assert.Equal(194, memberCount);
        Assert.Equal(1_656_018, retainedKeys);
        Assert.Equal(1_656_015, comparedKeys);
        Assert.Equal(47_605, tableLookupKeys);
        Assert.Equal(749_927, variableKeys);
        Assert.Equal(858_486, directKeys);
        Assert.Equal(1_412_381, narrowComponents);
        Assert.Equal(99_562, highBitNarrowComponents);
        Assert.InRange(maxXyzDelta, 0.000068f, 0.000070f);
    }

    private static Dictionary<uint, byte[]> ReadSkaMembers(string cutPath)
    {
        var data = File.ReadAllBytes(cutPath);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(data));
        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        var members = new Dictionary<uint, byte[]>();
        for (var i = 0; i < count; i++)
        {
            var toc = 8 + 16 * i;
            var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(toc)));
            var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(toc + 4)));
            var nameChecksum = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(toc + 8));
            var extensionChecksum = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(toc + 12));
            if (extensionChecksum == ExtSka)
                members.Add(nameChecksum, data.AsSpan(offset, size).ToArray());
        }

        return members;
    }

    private static void DecodeQ(byte[] data, int offset, int end)
    {
        _ = SkaCompressedKeyDecoders.DecodeCompressedQKeys(data, ref offset, end, null);
    }

    private static void DecodeT(byte[] data, int offset, int end)
    {
        _ = SkaCompressedKeyDecoders.DecodeCompressedTKeys(data, ref offset, end, null);
    }

    private static BareRotations ReadBareRotations(byte[] data)
    {
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var boneCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)));
        var totalQKeys = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20)));
        var offset = checked(40 + 12 * boneCount);
        var counts = new int[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
            counts[bone] = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                data.AsSpan(offset + 4 * bone)));
        offset += 4 * boneCount;

        var tracks = new BareRotationKey[boneCount][];
        var parsed = 0;
        for (var bone = 0; bone < boneCount; bone++)
        {
            var keys = new BareRotationKey[counts[bone]];
            for (var key = 0; key < keys.Length; key++, offset += 20)
            {
                keys[key] = new BareRotationKey(
                    BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)),
                    new Vector3(
                        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 4)),
                        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 8)),
                        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset + 12))));
            }

            parsed += keys.Length;
            tracks[bone] = keys;
        }

        Assert.Equal(totalQKeys, parsed);
        return new BareRotations(flags, tracks);
    }

    private static void CountCompiledQEncodings(
        byte[] data,
        ref long tableLookups,
        ref long variableKeys,
        ref long directKeys,
        ref long narrowComponents,
        ref long highBitNarrowComponents)
    {
        var boneCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)));
        ReadOnlySpan<ushort> componentBits = [0x2000, 0x1000, 0x0800];
        var offset = 36;
        var sizes = new int[boneCount];
        for (var bone = 0; bone < boneCount; bone++)
            sizes[bone] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2 * bone));
        offset = checked(offset + 4 * boneCount); // skip Q and T size tables
        offset = (offset + 3) & ~3;

        foreach (var size in sizes)
        {
            var end = checked(offset + size);
            while (offset < end)
            {
                var header = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
                offset += 2;
                if ((header & 0x4000) == 0)
                {
                    directKeys++;
                    offset += 6;
                    continue;
                }

                if ((header & 0x3800) == 0)
                {
                    tableLookups++;
                    offset++;
                    continue;
                }

                variableKeys++;
                foreach (var bit in componentBits)
                {
                    if ((header & bit) != 0)
                    {
                        narrowComponents++;
                        if ((data[offset] & 0x80) != 0)
                            highBitNarrowComponents++;
                        offset++;
                    }
                    else
                    {
                        offset += 2;
                    }
                }
            }

            Assert.Equal(end, offset);
        }
    }

    private readonly record struct BareRotations(uint Flags, BareRotationKey[][] Tracks);
    private readonly record struct BareRotationKey(uint Frame, Vector3 Rotation);
}
