using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

public sealed class N64AnimationCorpusTests(TestPaths paths)
{
    private static readonly (string Build, string Rom, Counts Expected)[] Cases =
    [
        ("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
            "Tony Hawk's Pro Skater (USA).z64", new Counts(28, 4, 24, 28, 4, 24, 4, 4, 948)),
        ("Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)",
            "Tony Hawk's Pro Skater 2 (USA).z64", new Counts(48, 43, 5, 48, 43, 5, 43, 43, 333)),
        ("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
            "Tony Hawk's Pro Skater 3 (USA).z64", new Counts(23, 20, 3, 23, 20, 3, 20, 20, 339)),
        ("Spider-Man (2000-11-21, N64 - Final)",
            "Spider-Man (USA).z64", new Counts(56, 30, 26, 54, 29, 25, 735, 734, 794))
    ];

    [CorpusFact]
    public void FourRomCorpus_PinsDirectAndCompressedAnimationBoundaries()
    {
        var total = new Counts();
        var decodedClips = 0;
        var maxClipFrames = 0;
        var maxShellFrames = 0;
        var maxShellClips = 0;
        var decodedDirectClips = 0;
        var exactDirectPayloads = 0;
        var oneStoredFrameSlackPayloads = 0;
        var directSlackLocations = new List<string>();
        foreach (var (build, rom, expected) in Cases)
        {
            var romPath = paths.FindSampleFile(build, rom);
            Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
            var backend = ArchiveAssetBackend.TryOpen(romPath!);
            Assert.NotNull(backend);
            using var fileSystem = backend.FileSystem;

            var actual = new Counts();
            foreach (var entry in backend.Entries.Where(static entry =>
                         entry.Name.EndsWith(".psx.n64", StringComparison.OrdinalIgnoreCase)))
            {
                var source = new ArchiveAssetSource(backend, entry);
                var shellData = source.ReadBytes();
                var shell = PsxN64ShellFile.Parse(shellData);
                var renderData = N64ModelCompanions.TryReadRenderBank(source);
                if (shell == null || renderData == null)
                    continue;

                var meshes = N64RenderBankFile.Parse(renderData);
                if (!meshes.Any(static mesh => mesh.Triangles.Count > 0)
                    || !TryGetLastAnimationTag(shellData, out var tag))
                {
                    continue;
                }

                actual.Animated++;
                if (tag == PsxMeshFile.HierChunkV1Tag)
                {
                    actual.Direct++;
                    var rawEntries = ReadRawDirectEntries(shellData);
                    Assert.NotNull(rawEntries);
                    actual.DirectClips += rawEntries!.Length;
                    var directBank = N64CompressedAnimationBank.TryParse(shellData);
                    Assert.NotNull(directBank);
                    Assert.Equal(PsxMeshFile.HierChunkV1Tag, directBank!.ChunkTag);
                    Assert.Equal(rawEntries.Length, directBank.Entries.Count);
                    for (var index = 0; index < rawEntries.Length; index++)
                    {
                        var rawEntry = rawEntries[index];
                        var bankEntry = directBank.Entries[index];
                        Assert.Equal(rawEntry.PoolOffset, bankEntry.PoolOffset);
                        Assert.Equal(rawEntry.FrameCount, bankEntry.FrameCount);
                        Assert.Equal(rawEntry.TweenFlag, bankEntry.TweenFlag);
                        // Kept independent of the production stored-count
                        // helper so the corpus proves the recovered grammar,
                        // rather than asking the implementation to grade itself.
                        var interval = rawEntry.TweenFlag + 1;
                        var storedFrames = (rawEntry.FrameCount - 1) / interval + 1;
                        var required = checked(
                            storedFrames * shell.Objects.Count * PsxAnimDecoder.DirectMatrixStrideBytes);
                        var slack = rawEntry.OwnedPayloadLength - required;
                        if (slack == 0)
                            exactDirectPayloads++;
                        else
                        {
                            Assert.Equal(
                                shell.Objects.Count * PsxAnimDecoder.DirectMatrixStrideBytes,
                                slack);
                            oneStoredFrameSlackPayloads++;
                            directSlackLocations.Add(
                                $"{entry.Directory.Replace("models/", "", StringComparison.OrdinalIgnoreCase)}:{index}");
                        }

                        var animation = directBank.DecodeSlot(index, shell.Objects.Count);
                        Assert.Equal(bankEntry.FrameCount, animation.FrameCount);
                        decodedDirectClips++;
                    }
                }
                else
                    actual.Compressed++;

                if (!N64AnimatedModelGate.IsGeometryEligible(shell, meshes))
                    continue;

                actual.GeometryGated++;
                if (tag == PsxMeshFile.HierChunkV1Tag)
                {
                    actual.DirectGated++;
                    var eligibleDirectBank = N64CompressedAnimationBank.TryParse(shellData);
                    Assert.NotNull(eligibleDirectBank);
                    actual.DirectEligibleClips += eligibleDirectBank!.Entries.Count;
                    continue;
                }

                actual.CompressedGated++;
                var bank = N64CompressedAnimationBank.TryParse(shellData);
                Assert.NotNull(bank);
                actual.CompressedClips += bank.Entries.Count;
                var shellFrames = 0;
                for (var index = 0; index < bank.Entries.Count; index++)
                {
                    // Decode every owned slice, not just its table framing. In
                    // particular this protects the two-byte zero sentinel that
                    // permits a final bit-window peek without exposing the next
                    // slot's first byte.
                    var animation = bank.DecodeSlot(index, shell.Objects.Count);
                    Assert.Equal(bank.Entries[index].FrameCount, animation.FrameCount);
                    decodedClips++;
                    shellFrames += animation.FrameCount;
                    maxClipFrames = Math.Max(maxClipFrames, animation.FrameCount);
                }

                maxShellFrames = Math.Max(maxShellFrames, shellFrames);
                maxShellClips = Math.Max(maxShellClips, bank.Entries.Count);
            }

            Assert.Equal(expected, actual);
            total += actual;
        }

        Assert.Equal(new Counts(
            Animated: 155,
            Direct: 97,
            Compressed: 58,
            GeometryGated: 153,
            DirectGated: 96,
            CompressedGated: 57,
            DirectClips: 802,
            DirectEligibleClips: 801,
            CompressedClips: 2414), total);
        Assert.Equal(802, decodedDirectClips);
        Assert.Equal(798, exactDirectPayloads);
        Assert.Equal(4, oneStoredFrameSlackPayloads);
        Assert.Equal(["145:43", "145:50", "263:43", "263:50"], directSlackLocations);
        Assert.Equal(2414, decodedClips);
        Assert.Equal(150, maxClipFrames);
        Assert.Equal(6922, maxShellFrames);
        Assert.Equal(300, maxShellClips);
    }

    [Fact]
    public void SevenPsxRosettaPairs_DirectTablesAndSwappedPayloadsMatchExactly()
    {
        const string thps2N64 = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
        const string thps2Psx = "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)";
        const string spiderN64 = "Spider-Man (2000-11-21, N64 - Final)";
        const string spiderPsx = "Spider-Man (2000-9-1, PSX - Final)";
        var pairs = new[]
        {
            new RosettaPair(thps2N64, "Tony Hawk's Pro Skater 2 (USA).z64", thps2Psx, "burnq2", "003"),
            new RosettaPair(thps2N64, "Tony Hawk's Pro Skater 2 (USA).z64", thps2Psx, "sk2def", "046"),
            new RosettaPair(spiderN64, "Spider-Man (USA).z64", spiderPsx, "brock", "002"),
            new RosettaPair(spiderN64, "Spider-Man (USA).z64", spiderPsx, "police", "112"),
            new RosettaPair(spiderN64, "Spider-Man (USA).z64", spiderPsx, "swat", "118"),
            new RosettaPair(spiderN64, "Spider-Man (USA).z64", spiderPsx, "thug", "120"),
            new RosettaPair(spiderN64, "Spider-Man (USA).z64", spiderPsx, "hostage", "011")
        };
        var backends = new Dictionary<string, ArchiveAssetBackend>(StringComparer.Ordinal);
        try
        {
            var comparedPayloadBytes = 0;
            var sawTweenedClip = false;
            foreach (var pair in pairs)
            {
                if (!backends.TryGetValue(pair.N64Build, out var backend))
                {
                    var romPath = paths.FindSampleFile(pair.N64Build, pair.Rom);
                    Assert.SkipWhen(romPath == null, $"{pair.N64Build} ROM sample not available");
                    backend = ArchiveAssetBackend.TryOpen(romPath!)!;
                    Assert.NotNull(backend);
                    backends.Add(pair.N64Build, backend);
                }

                var psxPath = paths.FindSampleFile(pair.PsxBuild, pair.Stem + ".psx");
                Assert.SkipWhen(psxPath == null, $"{pair.Stem}.psx sample not available");
                var psxBytes = File.ReadAllBytes(psxPath!);
                var psxShell = PsxMeshFile.ParseHeaderOnly(psxBytes);
                Assert.NotNull(psxShell);
                var psxBank = PsxAnimFile.Parse(psxBytes, psxShell!.Objects.Count);
                Assert.NotNull(psxBank);
                Assert.True(psxBank!.IsDirectMatrix);

                // Slot identity is pinned independently of the payload oracle:
                // THPS2 has duplicate character payloads and two entries named
                // sk2def, so selecting by byte equality would be circular.
                var n64Entry = N64Bundles.FindBundle(backend, pair.N64Slot);
                Assert.Contains("_" + pair.Stem, n64Entry.Name, StringComparison.OrdinalIgnoreCase);
                var n64Bytes = backend.ReadEntryBytes(n64Entry);
                var n64Shell = PsxN64ShellFile.Parse(n64Bytes);
                Assert.NotNull(n64Shell);
                var n64Bank = N64CompressedAnimationBank.TryParse(n64Bytes);
                Assert.NotNull(n64Bank);
                Assert.Equal(PsxMeshFile.HierChunkV1Tag, n64Bank!.ChunkTag);

                Assert.Equal(psxShell!.Objects.Count, n64Shell!.Objects.Count);
                Assert.Equal(psxBank.Entries.Count, n64Bank.Entries.Count);
                Assert.True(TryGetLastAnimationChunk(
                    n64Bytes, out var n64Tag, out var n64Chunk));
                Assert.Equal(PsxMeshFile.HierChunkV1Tag, n64Tag);
                var rawEntries = ReadRawDirectEntries(n64Bytes);
                Assert.NotNull(rawEntries);
                Assert.Equal(n64Bank.Entries.Count, rawEntries!.Length);

                for (var index = 0; index < rawEntries.Length; index++)
                {
                    var n64Animation = rawEntries[index];
                    Assert.Equal(n64Animation.PoolOffset, n64Bank.Entries[index].PoolOffset);
                    Assert.Equal(n64Animation.FrameCount, n64Bank.Entries[index].FrameCount);
                    Assert.Equal(n64Animation.TweenFlag, n64Bank.Entries[index].TweenFlag);
                    var psxAnimation = psxBank.Entries[index];
                    Assert.Equal(psxAnimation.FrameCount, n64Animation.FrameCount);
                    Assert.Equal(psxAnimation.TweenFlag, n64Animation.TweenFlag);
                    sawTweenedClip |= n64Animation.TweenFlag != 0;

                    var storedFrames = (n64Animation.FrameCount - 1) /
                        (n64Animation.TweenFlag + 1) + 1;
                    var required = checked(
                        storedFrames * n64Shell.Objects.Count * PsxAnimDecoder.DirectMatrixStrideBytes);
                    var n64Payload = n64Chunk.Slice(n64Animation.PoolOffset, required);
                    var psxPayload = psxBank.Pool.Span.Slice(psxAnimation.PoolOffset, required);
                    for (var offset = 0; offset < required; offset += 2)
                    {
                        Assert.Equal(psxPayload[offset], n64Payload[offset + 1]);
                        Assert.Equal(psxPayload[offset + 1], n64Payload[offset]);
                    }

                    // Decode parity makes the shared direct decoder part of the
                    // oracle too, including the established CycleAnim tween
                    // expansion used by export.
                    var n64Decoded = n64Bank.DecodeSlot(index, n64Shell.Objects.Count);
                    var psxDecoded = PsxAnimDecoder.DecodeDirectMatrix(
                        psxPayload, psxShell.Objects.Count,
                        psxAnimation.FrameCount, psxAnimation.TweenFlag);
                    Assert.Equal(psxDecoded.Channels, n64Decoded.Channels);
                    Assert.Equal(psxDecoded.DirectRotations, n64Decoded.DirectRotations);
                    comparedPayloadBytes += required;
                }
            }

            Assert.True(sawTweenedClip);
            Assert.Equal(585_144, comparedPayloadBytes);
        }
        finally
        {
            foreach (var backend in backends.Values)
                backend.FileSystem.Dispose();
        }
    }

    private static RawDirectEntry[]? ReadRawDirectEntries(byte[] data)
    {
        if (!TryGetLastAnimationChunk(data, out var tag, out var chunk)
            || tag != PsxMeshFile.HierChunkV1Tag
            || chunk.Length < 4)
        {
            return null;
        }

        var countValue = BinaryPrimitives.ReadUInt32BigEndian(chunk);
        if (countValue is 0 or > 4096)
            return null;
        var count = (int)countValue;
        var tableLength = checked(4 + count * 8);
        if (tableLength > chunk.Length)
            return null;

        var offsets = new int[count];
        var frames = new int[count];
        var tweens = new int[count];
        var previousOffset = -1;
        for (var index = 0; index < count; index++)
        {
            var entry = chunk.Slice(4 + index * 8, 8);
            var offsetValue = BinaryPrimitives.ReadUInt32BigEndian(entry);
            if (offsetValue > int.MaxValue)
                return null;
            offsets[index] = (int)offsetValue;
            frames[index] = BinaryPrimitives.ReadUInt16BigEndian(entry[4..]);
            tweens[index] = BinaryPrimitives.ReadUInt16BigEndian(entry[6..]);
            if (frames[index] is 0 or > 4096
                || offsets[index] < tableLength
                || offsets[index] >= chunk.Length
                || offsets[index] <= previousOffset)
            {
                return null;
            }

            previousOffset = offsets[index];
        }

        var entries = new RawDirectEntry[count];
        for (var index = 0; index < count; index++)
        {
            entries[index] = new RawDirectEntry(
                offsets[index],
                frames[index],
                tweens[index],
                (index + 1 < count ? offsets[index + 1] : chunk.Length) - offsets[index]);
        }

        return entries;
    }

    private static bool TryGetLastAnimationChunk(
        byte[] data,
        out uint chunkTag,
        out ReadOnlySpan<byte> chunk)
    {
        chunkTag = 0;
        chunk = default;
        if (data.Length < 12)
            return false;

        var cursorValue = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        if (cursorValue > int.MaxValue)
            return false;
        var cursor = (int)cursorValue;
        var found = false;
        for (var chunks = 0; chunks < 64 && cursor + 4 <= data.Length; chunks++)
        {
            var tag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor));
            if (tag == uint.MaxValue)
                return found;
            if (cursor + 8 > data.Length)
                return false;

            var lengthValue = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor + 4));
            if (lengthValue > int.MaxValue)
                return false;
            var length = (int)lengthValue;
            var start = cursor + 8;
            if ((long)start + length > data.Length)
                return false;
            if (tag is PsxMeshFile.HierChunkV1Tag or PsxMeshFile.HierChunkV2Tag)
            {
                chunkTag = tag;
                chunk = data.AsSpan(start, length);
                found = true;
            }

            cursor = start + length;
        }

        return false;
    }

    private static bool TryGetLastAnimationTag(byte[] data, out uint lastTag)
    {
        lastTag = 0;
        if (data.Length < 12)
            return false;

        var cursorValue = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        if (cursorValue > int.MaxValue)
            return false;
        var cursor = (int)cursorValue;
        var found = false;
        for (var chunk = 0; chunk < 64 && cursor + 4 <= data.Length; chunk++)
        {
            var tag = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor));
            if (tag == uint.MaxValue)
                return found;
            if (cursor + 8 > data.Length)
                return false;

            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor + 4));
            var next = (long)cursor + 8 + length;
            if (next > data.Length)
                return false;
            if (tag is PsxMeshFile.HierChunkV1Tag or PsxMeshFile.HierChunkV2Tag)
            {
                lastTag = tag;
                found = true;
            }

            cursor = (int)next;
        }

        return false;
    }

    private sealed record Counts(
        int Animated = 0,
        int Direct = 0,
        int Compressed = 0,
        int GeometryGated = 0,
        int DirectGated = 0,
        int CompressedGated = 0,
        int DirectClips = 0,
        int DirectEligibleClips = 0,
        int CompressedClips = 0)
    {
        public static Counts operator +(Counts left, Counts right) => new(
            left.Animated + right.Animated,
            left.Direct + right.Direct,
            left.Compressed + right.Compressed,
            left.GeometryGated + right.GeometryGated,
            left.DirectGated + right.DirectGated,
            left.CompressedGated + right.CompressedGated,
            left.DirectClips + right.DirectClips,
            left.DirectEligibleClips + right.DirectEligibleClips,
            left.CompressedClips + right.CompressedClips);

        public int Animated { get; set; } = Animated;
        public int Direct { get; set; } = Direct;
        public int Compressed { get; set; } = Compressed;
        public int GeometryGated { get; set; } = GeometryGated;
        public int DirectGated { get; set; } = DirectGated;
        public int CompressedGated { get; set; } = CompressedGated;
        public int DirectClips { get; set; } = DirectClips;
        public int DirectEligibleClips { get; set; } = DirectEligibleClips;
        public int CompressedClips { get; set; } = CompressedClips;
    }

    private sealed record RosettaPair(
        string N64Build,
        string Rom,
        string PsxBuild,
        string Stem,
        string N64Slot);

    private readonly record struct RawDirectEntry(
        int PoolOffset,
        int FrameCount,
        int TweenFlag,
        int OwnedPayloadLength);
}
