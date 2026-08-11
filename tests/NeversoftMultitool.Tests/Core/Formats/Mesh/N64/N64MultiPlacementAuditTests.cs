using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Independent raw-geometry census for the global-G_MTX animation gate.
///     The test derives both address modes directly from shell placements and
///     display-list corners instead of asking the production plan to classify
///     itself, then verifies the production verdict shell by shell.
/// </summary>
public sealed class N64MultiPlacementAuditTests(TestPaths paths)
{
    private static readonly (string Label, string Build, string Rom, Expected Expected)[] Cases =
    [
        ("THPS1", "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
            "Tony Hawk's Pro Skater (USA).z64",
            new Expected(28, 24, 28, 4, 24, 4, 948, 0, 952)),
        ("THPS2", "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)",
            "Tony Hawk's Pro Skater 2 (USA).z64",
            new Expected(48, 7, 48, 43, 5, 43, 333, 0, 376)),
        ("THPS3", "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
            "Tony Hawk's Pro Skater 3 (USA).z64",
            new Expected(23, 5, 23, 20, 3, 20, 339, 0, 359)),
        ("SM", "Spider-Man (2000-11-21, N64 - Final)",
            "Spider-Man (USA).z64",
            new Expected(56, 52, 54, 29, 25, 734, 794, 2, 1572))
    ];

    [CorpusFact]
    public void FourRomCorpus_PinsConservativeGlobalBindingBoundary()
    {
        var total = new Expected();
        var residualGlobalAndUnique = 0;
        var residualProvenGlobal = 0;
        var ambiguousControls = new List<(string Identity, int ClipCount)>();
        foreach (var (label, build, rom, expected) in Cases)
        {
            var romPath = paths.FindSampleFile(build, rom);
            Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
            var backend = ArchiveAssetBackend.TryOpen(romPath!);
            Assert.NotNull(backend);
            using var fileSystem = backend!.FileSystem;

            var actual = new Expected();
            foreach (var entry in backend.Entries.Where(static entry =>
                         entry.Name.EndsWith(".psx.n64", StringComparison.OrdinalIgnoreCase)))
            {
                var source = new ArchiveAssetSource(backend, entry);
                var shellData = source.ReadBytes();
                var shell = PsxN64ShellFile.Parse(shellData);
                var renderData = N64ModelCompanions.TryReadRenderBank(source);
                if (shell == null || renderData == null
                    || !TryGetLastAnimationHeader(shellData, out var tag, out var rawClipCount))
                    continue;

                var meshes = N64RenderBankFile.Parse(renderData);
                var byNode = meshes.GroupBy(static mesh => mesh.NodeIndex)
                    .ToDictionary(static group => group.Key, static group => group.ToArray());
                var placements = shell.Objects.Select((obj, index) => (Object: obj, Index: index))
                    .Where(item => byNode.TryGetValue(item.Object.MeshIndex, out var matches)
                                   && matches.Length == 1
                                   && matches[0].Triangles.Count > 0)
                    .Select(item => (item.Index, Mesh: byNode[item.Object.MeshIndex][0]))
                    .ToArray();
                if (placements.Length == 0)
                    continue;

                actual.Animated++;
                actual.RawClips += rawClipCount;
                var uniqueNodes = placements.Select(static placement => placement.Mesh.NodeIndex)
                    .Distinct().Count() == placements.Length;
                var globalInRange = true;
                var interpretationsCoincide = true;
                var relativeLookupFails = false;
                foreach (var (objectIndex, mesh) in placements)
                {
                    foreach (var triangle in mesh.Triangles)
                    {
                        Check(triangle.C0.MatrixIndex);
                        Check(triangle.C1.MatrixIndex);
                        Check(triangle.C2.MatrixIndex);
                    }

                    void Check(int matrixIndex)
                    {
                        globalInRange &= (uint)matrixIndex < (uint)shell.Objects.Count;
                        var relativeIndex = (long)objectIndex + matrixIndex;
                        if (relativeIndex < 0 || relativeIndex >= shell.Objects.Count)
                        {
                            relativeLookupFails = true;
                            interpretationsCoincide = false;
                        }
                        else if (relativeIndex != matrixIndex)
                        {
                            interpretationsCoincide = false;
                        }
                    }
                }

                var legacyCoincident = shell.IsSuperModel && uniqueNodes
                                       && globalInRange && interpretationsCoincide;
                var eligible = shell.IsSuperModel && uniqueNodes && globalInRange
                               && (interpretationsCoincide || relativeLookupFails);
                Assert.Equal(eligible, N64AnimatedModelGate.IsGeometryEligible(shell, meshes));

                if (legacyCoincident)
                    actual.LegacyCoincident++;
                else if (uniqueNodes && globalInRange)
                {
                    residualGlobalAndUnique++;
                    if (relativeLookupFails)
                        residualProvenGlobal++;
                }

                if (eligible)
                {
                    actual.Eligible++;
                    if (tag == PsxMeshFile.HierChunkV1Tag)
                    {
                        actual.DirectEligible++;
                        actual.DirectClips += rawClipCount;
                    }
                    else
                    {
                        actual.CompressedEligible++;
                        actual.CompressedClips += rawClipCount;
                    }
                }
                else if (uniqueNodes && globalInRange && !interpretationsCoincide
                         && !relativeLookupFails)
                {
                    actual.Ambiguous++;
                    var slot = entry.Directory.Replace(
                        "models/", "", StringComparison.OrdinalIgnoreCase);
                    ambiguousControls.Add(($"{label}:{slot}:{tag:X2}", rawClipCount));
                }

                if (label == "THPS2"
                    && entry.Directory.Equals("models/046", StringComparison.OrdinalIgnoreCase))
                {
                    AssertSk2DefRawBindingEvidence(shell, placements);
                }
            }

            Assert.Equal(expected, actual);
            total += actual;
        }

        Assert.Equal(new Expected(
            Animated: 155,
            LegacyCoincident: 88,
            Eligible: 153,
            DirectEligible: 96,
            CompressedEligible: 57,
            DirectClips: 801,
            CompressedClips: 2414,
            Ambiguous: 2,
            RawClips: 3259), total);
        Assert.Equal(67, residualGlobalAndUnique);
        Assert.Equal(65, residualProvenGlobal);
        Assert.Equal(
            [("SM:007:2C", 43), ("SM:108:2A", 1)],
            ambiguousControls);
    }

    private static void AssertSk2DefRawBindingEvidence(
        PsxMeshFile shell,
        (int Index, N64RenderBankFile.N64RenderMesh Mesh)[] placements)
    {
        Assert.Equal(110, shell.Objects.Count);
        Assert.Equal(33, placements.Length);
        Assert.Equal(4154, placements.Sum(static placement => placement.Mesh.Triangles.Count));

        var globalIndices = new HashSet<int>();
        var relativeIndices = new HashSet<int>();
        foreach (var (objectIndex, mesh) in placements)
        {
            foreach (var triangle in mesh.Triangles)
            {
                Add(triangle.C0.MatrixIndex);
                Add(triangle.C1.MatrixIndex);
                Add(triangle.C2.MatrixIndex);
            }

            void Add(int matrixIndex)
            {
                globalIndices.Add(matrixIndex);
                relativeIndices.Add(objectIndex + matrixIndex);
            }
        }

        Assert.Equal(Enumerable.Range(0, 110), globalIndices.Order());
        Assert.Equal(101, relativeIndices.Count);
        Assert.Equal(20, relativeIndices.Count(index => (uint)index >= 110u));
        Assert.Equal(23, placements.SelectMany(placement => placement.Mesh.Triangles
            .SelectMany(static triangle => new[]
            {
                triangle.C0.MatrixIndex,
                triangle.C1.MatrixIndex,
                triangle.C2.MatrixIndex
            })
            .Select(matrixIndex => (placement.Index, MatrixIndex: matrixIndex)))
            .Distinct()
            .Count(pair => (uint)(pair.Index + pair.MatrixIndex) >= 110u));
    }

    private static bool TryGetLastAnimationHeader(
        byte[] data,
        out uint lastTag,
        out int lastClipCount)
    {
        lastTag = 0;
        lastClipCount = 0;
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
                if (length < 4)
                    return false;
                var countValue = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor + 8));
                if (countValue is 0 or > 4096)
                    return false;
                lastTag = tag;
                lastClipCount = (int)countValue;
                found = true;
            }
            cursor = (int)next;
        }
        return false;
    }

    private sealed record Expected(
        int Animated = 0,
        int LegacyCoincident = 0,
        int Eligible = 0,
        int DirectEligible = 0,
        int CompressedEligible = 0,
        int DirectClips = 0,
        int CompressedClips = 0,
        int Ambiguous = 0,
        int RawClips = 0)
    {
        public static Expected operator +(Expected left, Expected right) => new(
            left.Animated + right.Animated,
            left.LegacyCoincident + right.LegacyCoincident,
            left.Eligible + right.Eligible,
            left.DirectEligible + right.DirectEligible,
            left.CompressedEligible + right.CompressedEligible,
            left.DirectClips + right.DirectClips,
            left.CompressedClips + right.CompressedClips,
            left.Ambiguous + right.Ambiguous,
            left.RawClips + right.RawClips);

        public int Animated { get; set; } = Animated;
        public int LegacyCoincident { get; set; } = LegacyCoincident;
        public int Eligible { get; set; } = Eligible;
        public int DirectEligible { get; set; } = DirectEligible;
        public int CompressedEligible { get; set; } = CompressedEligible;
        public int DirectClips { get; set; } = DirectClips;
        public int CompressedClips { get; set; } = CompressedClips;
        public int Ambiguous { get; set; } = Ambiguous;
        public int RawClips { get; set; } = RawClips;
    }
}
