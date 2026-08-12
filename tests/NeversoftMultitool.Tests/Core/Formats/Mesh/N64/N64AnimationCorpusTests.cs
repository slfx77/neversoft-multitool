using System.Buffers.Binary;
using System.Security.Cryptography;
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
            "Spider-Man (USA).z64", new Counts(56, 30, 26, 56, 30, 26, 735, 735, 837))
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
                var renderBankId = N64ModelCompanions.TryReadRenderBankId(source);
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

                if (!N64AnimatedModelGate.IsGeometryEligible(
                        shellData, shell, renderData, renderBankId, meshes))
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
            GeometryGated: 155,
            DirectGated: 97,
            CompressedGated: 58,
            DirectClips: 802,
            DirectEligibleClips: 802,
            CompressedClips: 2457), total);
        Assert.Equal(3_259, total.DirectEligibleClips + total.CompressedClips);
        Assert.Equal(802, decodedDirectClips);
        Assert.Equal(798, exactDirectPayloads);
        Assert.Equal(4, oneStoredFrameSlackPayloads);
        Assert.Equal(["145:43", "145:50", "263:43", "263:50"], directSlackLocations);
        Assert.Equal(2457, decodedClips);
        Assert.Equal(150, maxClipFrames);
        Assert.Equal(6922, maxShellFrames);
        Assert.Equal(300, maxShellClips);
    }

    [Fact]
    public void SpiderDocOck_CompressedClipsAndGlobalPartsMatchPsxSiblingExactly()
    {
        const string n64Build = "Spider-Man (2000-11-21, N64 - Final)";
        const string psxBuild = "Spider-Man (2000-9-1, PSX - Final)";
        var romPath = paths.FindSampleFile(n64Build, "Spider-Man (USA).z64");
        var psxPath = paths.FindSampleFile(psxBuild, "docock.psx");
        Assert.SkipWhen(romPath == null || psxPath == null,
            "Spider-Man N64 ROM and PSX docock sibling are required");

        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = N64Bundles.FindBundle(backend, "007");
        Assert.Contains("_docock", entry.Name, StringComparison.OrdinalIgnoreCase);
        var source = new ArchiveAssetSource(backend, entry);
        var n64Bytes = source.ReadBytes();
        var n64Shell = PsxN64ShellFile.Parse(n64Bytes);
        Assert.NotNull(n64Shell);

        var psxBytes = File.ReadAllBytes(psxPath!);
        var psxShell = PsxMeshFile.Parse(psxBytes);
        Assert.NotNull(psxShell);
        Assert.True(PsxMeshSemantics.UsesCharacterObjectOrder(psxShell!));
        Assert.True(n64Shell!.HasHierarchy);
        Assert.True(n64Shell.IsSuperModel);
        Assert.Equal(46, n64Shell.Objects.Count);
        Assert.Equal(psxShell!.Objects.Count, n64Shell.Objects.Count);
        Assert.Equal(psxShell.MeshNameHashes, n64Shell.MeshNameHashes);
        for (var index = 0; index < n64Shell.Objects.Count; index++)
        {
            var expected = psxShell.Objects[index];
            var actual = n64Shell.Objects[index];
            Assert.Equal(expected.Flags, actual.Flags);
            Assert.Equal(expected.RawX, actual.RawX);
            Assert.Equal(expected.RawY, actual.RawY);
            Assert.Equal(expected.RawZ, actual.RawZ);
            Assert.Equal(expected.MeshIndex, actual.MeshIndex);
            Assert.Equal(expected.ParentIndex, actual.ParentIndex);
        }

        var renderData = N64ModelCompanions.TryReadRenderBank(source);
        Assert.NotNull(renderData);
        var renderMesh = Assert.Single(N64RenderBankFile.Parse(renderData!));
        Assert.Equal(0, renderMesh.NodeIndex);
        Assert.Equal(504, renderMesh.Triangles.Count);
        var placement = Assert.Single(
            n64Shell.Objects.Select((obj, objectIndex) =>
                (Object: obj, ObjectIndex: objectIndex)),
            item => item.Object.MeshIndex == renderMesh.NodeIndex);
        Assert.Equal(14, placement.ObjectIndex);

        // A HIER super renders mesh/animation parts positionally. The shell's
        // item MeshIndex fields are deliberately permuted, so matching through
        // them would be circular and wrong. Every N64 G_MTX m vertex instead
        // lands in PSX positional mesh m after the port's exact trunc(raw/8).
        var referencedVertices = 0;
        var referencedCorners = 0;
        var placementRelativeMatches = 0;
        foreach (var group in renderMesh.Triangles
                     .SelectMany(static triangle => new[]
                     {
                         triangle.C0,
                         triangle.C1,
                         triangle.C2
                     })
                     .GroupBy(static corner => corner.MatrixIndex)
                     .OrderBy(static group => group.Key))
        {
            Assert.InRange(group.Key, 0, psxShell.Meshes.Count - 1);
            var n64Positions = group
                .Select(corner => renderMesh.Vertices[corner.Vertex])
                .Select(static vertex => (
                    X: (int)vertex.X,
                    Y: (int)vertex.Y,
                    Z: (int)vertex.Z))
                .Distinct()
                .ToArray();
            var psxPositions = psxShell.Meshes[group.Key].Vertices
                .Select(static vertex => (
                    X: vertex.RawX / 8,
                    Y: vertex.RawY / 8,
                    Z: vertex.RawZ / 8))
                .ToHashSet();
            Assert.All(n64Positions, position => Assert.Contains(position, psxPositions));
            var relativePart = placement.ObjectIndex + group.Key;
            Assert.InRange(relativePart, 0, psxShell.Meshes.Count - 1);
            var relativePositions = psxShell.Meshes[relativePart].Vertices
                .Select(static vertex => (
                    X: vertex.RawX / 8,
                    Y: vertex.RawY / 8,
                    Z: vertex.RawZ / 8))
                .ToHashSet();
            foreach (var corner in group)
            {
                var vertex = renderMesh.Vertices[corner.Vertex];
                var position = ((int)vertex.X, (int)vertex.Y, (int)vertex.Z);
                Assert.Contains(position, psxPositions);
                if (relativePositions.Contains(position))
                    placementRelativeMatches++;
                referencedCorners++;
            }
            referencedVertices += n64Positions.Length;
        }

        Assert.Equal(Enumerable.Range(0, 17), renderMesh.Triangles
            .SelectMany(static triangle => new[]
            {
                triangle.C0.MatrixIndex,
                triangle.C1.MatrixIndex,
                triangle.C2.MatrixIndex
            })
            .Distinct()
            .Order());
        Assert.Equal(256, referencedVertices);
        Assert.Equal(1_512, referencedCorners);
        Assert.Equal(0, placementRelativeMatches);

        var n64Bank = N64CompressedAnimationBank.TryParse(n64Bytes);
        var psxBank = PsxAnimFile.Parse(psxBytes, psxShell.Objects.Count);
        Assert.NotNull(n64Bank);
        Assert.NotNull(psxBank);
        Assert.Equal(PsxMeshFile.HierChunkV2Tag, n64Bank!.ChunkTag);
        Assert.Equal(43, n64Bank.Entries.Count);
        Assert.Equal(psxBank!.Entries.Count, n64Bank.Entries.Count);
        long comparedSamples = 0;
        for (var index = 0; index < n64Bank.Entries.Count; index++)
        {
            var n64Animation = n64Bank.DecodeSlot(index, n64Shell.Objects.Count);
            var psxEntry = psxBank.Entries[index];
            var psxAnimation = PsxAnimDecoder.Decode(
                psxBank.Pool.Span[psxEntry.PoolOffset..],
                psxShell.Objects.Count,
                psxEntry.FrameCount,
                out _);
            Assert.Equal(psxEntry.FrameCount, n64Bank.Entries[index].FrameCount);
            Assert.Equal(psxAnimation.Channels, n64Animation.Channels);
            comparedSamples += (long)n64Animation.BoneCount
                               * PsxAnimation.ChannelsPerBone
                               * n64Animation.FrameCount;
        }

        Assert.Equal(536_820, comparedSamples);
    }

    [Fact]
    public void SpiderMap_ExactPayloadAdmitsRelativeK1AndRejectsGlobalInterpretation()
    {
        const string n64Build = "Spider-Man (2000-11-21, N64 - Final)";
        const string pcBuild = "Spider-Man (2001-9-17, PC - Final)";
        var romPath = paths.FindSampleFile(n64Build, "Spider-Man (USA).z64");
        var pcPath = paths.FindSampleFile(pcBuild, "map.psx");
        Assert.SkipWhen(romPath == null || pcPath == null,
            "Spider-Man N64 ROM and PC map sibling are required");

        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = N64Bundles.FindBundle(backend, "108");
        Assert.Contains("_map", entry.Name, StringComparison.OrdinalIgnoreCase);
        var source = new ArchiveAssetSource(backend, entry);
        var n64Bytes = source.ReadBytes();
        var n64Shell = PsxN64ShellFile.Parse(n64Bytes);
        var pcBytes = File.ReadAllBytes(pcPath!);
        var pcShell = PsxMeshFile.Parse(pcBytes);
        Assert.NotNull(n64Shell);
        Assert.NotNull(pcShell);
        Assert.False(n64Shell!.HasHierarchy);
        Assert.True(n64Shell.IsSuperModel);
        Assert.False(pcShell!.HasHierarchy);
        Assert.Equal(1_776, n64Bytes.Length);
        Assert.Equal(
            N64ModelPayloadProfile.SpiderMapShellSha256,
            Convert.ToHexString(SHA256.HashData(n64Bytes)));
        Assert.Equal(32_536, pcBytes.Length);
        Assert.Equal(
            "75EF75D6AD918D016D92800B93B60E0430A16D3FA72588B3069D03D304DE56B0",
            Convert.ToHexString(SHA256.HashData(pcBytes)));
        Assert.Equal(12, n64Shell.Objects.Count);
        Assert.Equal(pcShell.MeshNameHashes, n64Shell.MeshNameHashes);
        for (var index = 0; index < n64Shell.Objects.Count; index++)
        {
            var expected = pcShell.Objects[index];
            var actual = n64Shell.Objects[index];
            Assert.Equal(expected.Flags, actual.Flags);
            Assert.Equal(expected.RawX, actual.RawX);
            Assert.Equal(expected.RawY, actual.RawY);
            Assert.Equal(expected.RawZ, actual.RawZ);
            Assert.Equal(expected.MeshIndex, actual.MeshIndex);
            Assert.Equal(expected.ParentIndex, actual.ParentIndex);
        }

        var renderData = N64ModelCompanions.TryReadRenderBank(source);
        Assert.NotNull(renderData);
        var renderBankId = N64ModelCompanions.TryReadRenderBankId(source);
        Assert.Equal(N64ModelPayloadProfile.SpiderMapRenderBankId, renderBankId);
        Assert.Equal(N64ModelPayloadProfile.SpiderMapRenderBankLength, renderData!.Length);
        Assert.Equal(
            N64ModelPayloadProfile.SpiderMapRenderBankSha256,
            Convert.ToHexString(SHA256.HashData(renderData)));
        var profile = N64ModelPayloadProfile.TryResolve(
            n64Bytes, renderData, renderBankId);
        Assert.NotNull(profile);
        Assert.Equal(N64GeometryBindingMode.AnimatedRelative, profile!.AnimatedBindingMode);
        Assert.Equal(1f, profile.VertexScaleFactor);

        var mutatedShell = (byte[])n64Bytes.Clone();
        mutatedShell[^1] ^= 1;
        Assert.Null(N64ModelPayloadProfile.TryResolve(
            mutatedShell, renderData, renderBankId));
        var mutatedRender = (byte[])renderData.Clone();
        mutatedRender[^1] ^= 1;
        Assert.Null(N64ModelPayloadProfile.TryResolve(
            n64Bytes, mutatedRender, renderBankId));
        Assert.Null(N64ModelPayloadProfile.TryResolve(
            n64Bytes, renderData, renderBankId!.Value + 1u));

        var renderMeshes = N64RenderBankFile.Parse(renderData!)
            .OrderBy(static mesh => mesh.NodeIndex)
            .ToArray();
        Assert.Equal(12, renderMeshes.Length);
        Assert.Equal([168, 19, 19, 176, 19, 19, 184, 176, 8, 8, 8, 8],
            renderMeshes.Select(static mesh => mesh.Vertices
                .Select(static vertex => (vertex.X, vertex.Y, vertex.Z))
                .Distinct()
                .Count()));

        var comparedPositions = 0;
        for (var index = 0; index < renderMeshes.Length; index++)
        {
            var renderMesh = renderMeshes[index];
            Assert.Equal(index, renderMesh.NodeIndex);
            Assert.Equal(index, n64Shell.Objects[index].MeshIndex);
            Assert.All(renderMesh.Triangles.SelectMany(static triangle => new[]
            {
                triangle.C0.MatrixIndex,
                triangle.C1.MatrixIndex,
                triangle.C2.MatrixIndex
            }), static matrixIndex => Assert.Equal(0, matrixIndex));

            var n64Positions = renderMesh.Vertices
                .Select(static vertex => (vertex.X, vertex.Y, vertex.Z))
                .ToHashSet();
            var pcPositions = pcShell.Meshes[index].Vertices
                .Select(static vertex => (vertex.RawX, vertex.RawY, vertex.RawZ))
                .ToHashSet();
            Assert.True(n64Positions.SetEquals(pcPositions));
            comparedPositions += n64Positions.Count;
        }

        Assert.Equal(812, comparedPositions);
        Assert.NotEqual(n64Shell.Objects[0].RawX, n64Shell.Objects[1].RawX);
        var relative = N64GeometryBindingPlan.StaticRelative(n64Shell.Objects.Count, 1f);
        var global = N64GeometryBindingPlan.AnimatedGlobal(n64Shell.Objects.Count, 8f);
        Assert.Equal(1, relative.ResolveOffsetObjectIndexOrDefault(1, 0));
        Assert.Equal(0, global.ResolveOffsetObjectIndexOrDefault(1, 0));
        Assert.False(N64AnimatedModelGate.IsGeometryEligible(n64Shell, renderMeshes));
        Assert.True(N64AnimatedModelGate.IsGeometryEligible(
            n64Bytes, n64Shell, renderData, renderBankId, renderMeshes));
        var plan = N64AnimatedModelGate.TryOpen(
            n64Bytes, n64Shell, renderData, renderBankId, renderMeshes);
        Assert.NotNull(plan);
        Assert.Equal(N64GeometryBindingMode.AnimatedRelative, plan!.Geometry.Mode);
        Assert.Equal(1f, plan.Geometry.VertexScaleFactor);
        var exactStatic = N64AnimatedModelGate.CreateStaticBindingPlan(
            n64Bytes, n64Shell, renderData, renderBankId, renderMeshes);
        Assert.Equal(N64GeometryBindingMode.StaticRelative, exactStatic.Mode);
        Assert.Equal(1f, exactStatic.VertexScaleFactor);

        Assert.Null(N64AnimatedModelGate.TryOpen(
            mutatedShell, n64Shell, renderData, renderBankId, renderMeshes));
        Assert.Equal(8f, N64AnimatedModelGate.CreateStaticBindingPlan(
            mutatedShell, n64Shell, renderData, renderBankId, renderMeshes)
            .VertexScaleFactor);
        Assert.Null(N64AnimatedModelGate.TryOpen(
            n64Bytes, n64Shell, mutatedRender, renderBankId, renderMeshes));
        Assert.Equal(8f, N64AnimatedModelGate.CreateStaticBindingPlan(
            n64Bytes, n64Shell, mutatedRender, renderBankId, renderMeshes)
            .VertexScaleFactor);
        Assert.Null(N64AnimatedModelGate.TryOpen(
            n64Bytes, n64Shell, renderData, renderBankId.Value + 1u, renderMeshes));
        Assert.Equal(8f, N64AnimatedModelGate.CreateStaticBindingPlan(
            n64Bytes, n64Shell, renderData, renderBankId.Value + 1u, renderMeshes)
            .VertexScaleFactor);
        Assert.Null(N64AnimatedModelGate.TryOpen(
            n64Bytes, n64Shell, renderData, renderBankId, renderMeshes[..^1]));
        Assert.Equal(8f, N64AnimatedModelGate.CreateStaticBindingPlan(
            n64Bytes, n64Shell, renderData, renderBankId, renderMeshes[..^1])
            .VertexScaleFactor);
        for (var objectIndex = 0; objectIndex < renderMeshes.Length; objectIndex++)
        {
            Assert.All(renderMeshes[objectIndex].Triangles.SelectMany(static triangle => new[]
            {
                triangle.C0,
                triangle.C1,
                triangle.C2
            }), corner => Assert.Equal(
                objectIndex,
                plan.Geometry.ResolveSkinJoint(objectIndex, corner.MatrixIndex)));
        }

        var n64Bank = N64CompressedAnimationBank.TryParse(n64Bytes);
        var pcBank = PsxAnimFile.Parse(pcBytes, pcShell.Objects.Count);
        Assert.NotNull(n64Bank);
        Assert.NotNull(pcBank);
        Assert.Equal(PsxMeshFile.HierChunkV1Tag, n64Bank!.ChunkTag);
        var n64Animation = n64Bank.DecodeSlot(0, n64Shell.Objects.Count);
        var pcEntry = Assert.Single(pcBank!.Entries);
        var pcAnimation = PsxAnimDecoder.DecodeDirectMatrix(
            pcBank.Pool.Span[pcEntry.PoolOffset..],
            pcShell.Objects.Count,
            pcEntry.FrameCount,
            pcEntry.TweenFlag);
        Assert.Equal(1, n64Animation.FrameCount);
        Assert.Equal(pcAnimation.Channels, n64Animation.Channels);
        Assert.Equal(pcAnimation.DirectRotations, n64Animation.DirectRotations);
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
