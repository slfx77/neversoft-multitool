using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class N64CompiledSfxAliasMapTests
{
    private const int LookupOffset = 16;
    private const int LookupLength = 8;
    private const int TableOffset = 64;
    private const int MaximumAlias = 4;
    private const int EffectCount = 3;

    [Fact]
    public void ResolveForEvidence_ValidatesHashesAndPreservesFlagsAndFailClosedStatuses()
    {
        var boot = BuildBoot([0x8401, 0xFFFF, 0x0002, 0x03FF, 0x0400]);
        var map = Resolve(boot, [3]);

        Assert.Equal("synthetic build", map.Build);
        Assert.Equal(5, map.StaticTableRaw.Count);
        Assert.Equal(EffectCount, map.EffectCount);
        Assert.Equal((uint)ushort.MaxValue, map.CueAliasMask);
        Assert.Equal([3u], map.DynamicOverrideAliases.Order());

        var flagged = map.Resolve(0);
        Assert.Equal(N64CompiledSfxAliasMapResolver.ResolvedStatus, flagged.Status);
        Assert.Equal(0x8401u, flagged.CompiledTargetRaw);
        Assert.Equal(1, flagged.EffectIndex);
        Assert.Equal(0x8400u, flagged.RoutingFlagsRaw);

        var unmapped = map.Resolve(1);
        Assert.Equal(N64CompiledSfxAliasMapResolver.ExplicitlyUnmappedStatus, unmapped.Status);
        Assert.Equal((uint)ushort.MaxValue, unmapped.CompiledTargetRaw);
        Assert.Null(unmapped.EffectIndex);
        Assert.Null(unmapped.RoutingFlagsRaw);

        var direct = map.Resolve(2);
        Assert.Equal(N64CompiledSfxAliasMapResolver.ResolvedStatus, direct.Status);
        Assert.Equal(2, direct.EffectIndex);
        Assert.Equal(0u, direct.RoutingFlagsRaw);

        var low16Alias = map.Resolve(0xABCD_0002);
        Assert.Equal(0xABCD_0002u, low16Alias.Alias);
        Assert.Equal(2u, low16Alias.LookupAlias);
        Assert.Equal(N64CompiledSfxAliasMapResolver.ResolvedStatus, low16Alias.Status);
        Assert.Equal(2, low16Alias.EffectIndex);

        var dynamic = map.Resolve(3);
        Assert.Equal(N64CompiledSfxAliasMapResolver.DynamicOverrideStatus, dynamic.Status);
        Assert.Null(dynamic.CompiledTargetRaw);
        Assert.Null(dynamic.EffectIndex);
        Assert.NotNull(dynamic.DynamicRule);
        Assert.Equal(3u, dynamic.DynamicRule.Alias);
        Assert.Equal("synthetic selector", dynamic.DynamicRule.SelectorBasis);
        Assert.Collection(
            dynamic.DynamicRule.Cases,
            item =>
            {
                Assert.Equal("selector == 0", item.Condition);
                Assert.Equal(0u, item.CompiledTargetRaw);
                Assert.Equal(0, map.DecodeEffectIndex(item.CompiledTargetRaw!.Value));
                Assert.Equal(0u, map.DecodeRoutingFlags(item.CompiledTargetRaw!.Value));
            },
            item =>
            {
                Assert.Equal("otherwise", item.Condition);
                Assert.Equal((uint)ushort.MaxValue, item.CompiledTargetRaw);
                Assert.Null(map.DecodeEffectIndex(item.CompiledTargetRaw!.Value));
                Assert.Null(map.DecodeRoutingFlags(item.CompiledTargetRaw!.Value));
            });

        var routedZero = map.Resolve(4);
        Assert.Equal(0, routedZero.EffectIndex);
        Assert.Equal(0x0400u, routedZero.RoutingFlagsRaw);

        var outside = map.Resolve(5);
        Assert.Equal(N64CompiledSfxAliasMapResolver.OutsidePinnedTableStatus, outside.Status);
        Assert.Null(outside.CompiledTargetRaw);
        Assert.Null(outside.EffectIndex);
    }

    [Fact]
    public void ResolvePackedForEvidence_U32SentinelMasksRoutingDomainAndExtraCodeArePinned()
    {
        var boot = BuildPackedBoot([0x0002_0001, 0x0000_0FA0, 0x0004_0002, 0x0008_0000, 0]);
        var additional = new N64CompiledSfxEvidenceRange(
            N64CompiledSfxAliasMapResolver.CodeEvidenceKind,
            "synthetic second consumer",
            32,
            4,
            Hash(boot.AsSpan(32, 4)));
        var allowedRouting = new uint[] { 0, 0x0002_0000, 0x0004_0000, 0x0008_0000 }
            .ToHashSet();

        var map = N64CompiledSfxAliasMapResolver.ResolvePackedForEvidence(
            boot,
            "synthetic packed build",
            Hash(boot),
            LookupOffset,
            LookupLength,
            Hash(boot.AsSpan(LookupOffset, LookupLength)),
            TableOffset,
            MaximumAlias,
            Hash(boot.AsSpan(TableOffset, (MaximumAlias + 1) * sizeof(uint))),
            EffectCount,
            EffectCount,
            sizeof(uint),
            uint.MaxValue,
            0x0000_0FA0,
            0x0000_FFFF,
            0x001F_0000,
            allowedRouting,
            [additional],
            new Dictionary<N64CompiledSfxCueContextKey, N64CompiledSfxCueContextResolution>(),
            new Dictionary<uint, N64CompiledSfxDynamicAliasRule>());

        Assert.Equal(sizeof(uint), map.TableEntrySize);
        Assert.Equal(uint.MaxValue, map.CueAliasMask);
        Assert.Equal(0x0000_0FA0u, map.ExplicitNoTargetRaw);
        Assert.Equal(additional, Assert.Single(map.PinnedEvidenceRanges));
        Assert.Equal(1, map.Resolve(0).EffectIndex);
        Assert.Equal(0x0002_0000u, map.Resolve(0).RoutingFlagsRaw);
        Assert.Equal(N64CompiledSfxAliasMapResolver.ExplicitlyUnmappedStatus, map.Resolve(1).Status);
        Assert.Equal(2, map.Resolve(2).EffectIndex);
        Assert.Equal(0, map.Resolve(3).EffectIndex);
        var full32Alias = map.Resolve(0xABCD_0002);
        Assert.Equal(0xABCD_0002u, full32Alias.Alias);
        Assert.Equal(0xABCD_0002u, full32Alias.LookupAlias);
        Assert.Equal(N64CompiledSfxAliasMapResolver.OutsidePinnedTableStatus,
            full32Alias.Status);

        var unsupportedRouting = BuildPackedBoot([0x0001_0001, 0x0000_0FA0, 2, 0, 0]);
        Assert.Throws<InvalidDataException>(() =>
            N64CompiledSfxAliasMapResolver.ResolvePackedForEvidence(
                unsupportedRouting,
                "synthetic packed build",
                Hash(unsupportedRouting),
                LookupOffset,
                LookupLength,
                Hash(unsupportedRouting.AsSpan(LookupOffset, LookupLength)),
                TableOffset,
                MaximumAlias,
                Hash(unsupportedRouting.AsSpan(TableOffset, (MaximumAlias + 1) * sizeof(uint))),
                EffectCount,
                EffectCount,
                sizeof(uint),
                uint.MaxValue,
                0x0000_0FA0,
                0x0000_FFFF,
                0x001F_0000,
                allowedRouting,
                [],
                new Dictionary<N64CompiledSfxCueContextKey, N64CompiledSfxCueContextResolution>(),
                new Dictionary<uint, N64CompiledSfxDynamicAliasRule>()));

        var changedEvidence = boot.ToArray();
        changedEvidence[32] ^= 1;
        Assert.Throws<InvalidDataException>(() =>
            N64CompiledSfxAliasMapResolver.ResolvePackedForEvidence(
                changedEvidence,
                "synthetic packed build",
                Hash(changedEvidence),
                LookupOffset,
                LookupLength,
                Hash(changedEvidence.AsSpan(LookupOffset, LookupLength)),
                TableOffset,
                MaximumAlias,
                Hash(changedEvidence.AsSpan(TableOffset, (MaximumAlias + 1) * sizeof(uint))),
                EffectCount,
                EffectCount,
                sizeof(uint),
                uint.MaxValue,
                0x0000_0FA0,
                0x0000_FFFF,
                0x001F_0000,
                allowedRouting,
                [additional],
                new Dictionary<N64CompiledSfxCueContextKey, N64CompiledSfxCueContextResolution>(),
                new Dictionary<uint, N64CompiledSfxDynamicAliasRule>()));
    }

    [Fact]
    public void Resolve_ContextSpecializationRequiresExactSourceHashAndAliasTuple()
    {
        const string source = "sfx/007.sfx.n64";
        var sha256 = new string('A', 64);
        var boot = BuildBoot([0x0000, 0x0001, 0x0002, 0x03FF, 0x0400]);
        const int descriptorOffset = 32;
        const uint ownerIndexAddress = 0x80002000;
        const uint descriptorAddress = 0x80001000;
        const int descriptorStride = 2;
        var selector = BinaryPrimitives.ReadUInt16BigEndian(
            boot.AsSpan(descriptorOffset, sizeof(ushort)));
        var ownerLayout = new N64CompiledSfxCueOwnerLayout(
            ownerIndexAddress,
            descriptorOffset,
            descriptorAddress,
            descriptorStride,
            null,
            null,
            null);
        var key = new N64CompiledSfxCueContextKey(source, sha256, 3);
        var contexts = new Dictionary<
            N64CompiledSfxCueContextKey,
            N64CompiledSfxCueContextResolution>
        {
            [key] = new(
                source,
                sha256,
                3,
                N64CompiledSfxAliasMapResolver.ContextualOwnerBranchBasis,
                0,
                selector,
                null,
                $"u32@{ownerIndexAddress:X8} == 0 and " +
                $"u16@({descriptorAddress:X8} + 0 * {descriptorStride}) == 0x{selector:X4}",
                0,
                null)
        };
        var map = Resolve(
            boot,
            [3, 4],
            contextualResolutions: contexts,
            cueOwnerLayout: ownerLayout);

        var exact = map.Resolve(3, source, sha256);
        Assert.Equal(N64CompiledSfxAliasMapResolver.ResolvedStatus, exact.Status);
        Assert.Equal(0, exact.EffectIndex);
        Assert.Equal(N64CompiledSfxAliasMapResolver.ContextualOwnerBranchBasis,
            exact.ResolutionBasis);

        var maskedExact = map.Resolve(0xABCD_0003, source, sha256);
        Assert.Equal(0xABCD_0003u, maskedExact.Alias);
        Assert.Equal(3u, maskedExact.LookupAlias);
        Assert.Equal(N64CompiledSfxAliasMapResolver.ResolvedStatus, maskedExact.Status);
        Assert.Equal(0, maskedExact.EffectIndex);

        Assert.Equal(N64CompiledSfxAliasMapResolver.DynamicOverrideStatus, map.Resolve(3).Status);
        Assert.Equal(N64CompiledSfxAliasMapResolver.DynamicOverrideStatus,
            map.Resolve(3, source, null).Status);
        Assert.Equal(N64CompiledSfxAliasMapResolver.DynamicOverrideStatus,
            map.Resolve(3, null, sha256).Status);
        Assert.Equal(N64CompiledSfxAliasMapResolver.DynamicOverrideStatus,
            map.Resolve(3, "SFX/007.sfx.n64", sha256).Status);
        Assert.Equal(N64CompiledSfxAliasMapResolver.DynamicOverrideStatus,
            map.Resolve(3, source, "B" + sha256[1..]).Status);
        Assert.Equal(N64CompiledSfxAliasMapResolver.DynamicOverrideStatus,
            map.Resolve(3, source, sha256.ToLowerInvariant()).Status);
        Assert.Equal(N64CompiledSfxAliasMapResolver.DynamicOverrideStatus,
            map.Resolve(4, source, sha256).Status);
    }

    [Fact]
    public void Json_ContextualResolutionsAreDeterministicAcrossHashKeyInsertionOrder()
    {
        const string source = "sfx/007.sfx.n64";
        const string firstSha256 =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string secondSha256 =
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        const int descriptorOffset = 32;
        const uint ownerIndexAddress = 0x80002000;
        const uint descriptorAddress = 0x80001000;
        const int descriptorStride = 2;
        var boot = BuildBoot([0x0000, 0x0001, 0x0002, 0x03FF, 0x0400]);
        var selector = BinaryPrimitives.ReadUInt16BigEndian(
            boot.AsSpan(descriptorOffset, sizeof(ushort)));
        var ownerLayout = new N64CompiledSfxCueOwnerLayout(
            ownerIndexAddress,
            descriptorOffset,
            descriptorAddress,
            descriptorStride,
            null,
            null,
            null);

        IReadOnlyDictionary<
            N64CompiledSfxCueContextKey,
            N64CompiledSfxCueContextResolution> BuildContexts(IEnumerable<string> hashes)
        {
            var contexts = new Dictionary<
                N64CompiledSfxCueContextKey,
                N64CompiledSfxCueContextResolution>();
            foreach (var hash in hashes)
            {
                var key = new N64CompiledSfxCueContextKey(source, hash, 3);
                contexts.Add(key, new(
                    source,
                    hash,
                    3,
                    N64CompiledSfxAliasMapResolver.ContextualOwnerBranchBasis,
                    0,
                    selector,
                    null,
                    $"u32@{ownerIndexAddress:X8} == 0 and " +
                    $"u16@({descriptorAddress:X8} + 0 * {descriptorStride}) == 0x{selector:X4}",
                    0,
                    null));
            }
            return contexts;
        }

        var firstMap = Resolve(
            boot,
            [3],
            contextualResolutions: BuildContexts([secondSha256, firstSha256]),
            cueOwnerLayout: ownerLayout);
        var secondMap = Resolve(
            boot,
            [3],
            contextualResolutions: BuildContexts([firstSha256, secondSha256]),
            cueOwnerLayout: ownerLayout);
        var binding = N64SfxCueEffectBankBindingProvenance.Create(
            "testFixture",
            "effects.bfx.n64",
            [0x01],
            "sounds.ptr.n64",
            [0x02]);

        string Serialize(N64CompiledSfxAliasMap map) =>
            N64SfxCueBankJsonExporter.Serialize(
                "game.z64",
                N64SfxCueBankJsonExporter.StrictRomStructuralScanSelection,
                [],
                map,
                binding);

        var firstJson = Serialize(firstMap);
        var secondJson = Serialize(secondMap);
        Assert.Equal(firstJson, secondJson);

        using var document = JsonDocument.Parse(firstJson);
        Assert.Equal(
            new[] { firstSha256, secondSha256 },
            document.RootElement.GetProperty("compiledAliasMap")
                .GetProperty("contextualResolutions")
                .EnumerateArray()
                .Select(static item => item.GetProperty("bankSha256").GetString()));
    }

    [Fact]
    public void ResolveForEvidence_AnyPinnedEvidenceOrTargetMismatchFailsClosed()
    {
        var boot = BuildBoot([0x0000, 0x0001, 0x0002, 0x03FF, 0xFFFF]);
        var valid = Resolve(boot, [3]);
        Assert.NotNull(valid);

        var badBootHash = Hash(boot);
        badBootHash = (badBootHash[0] == '0' ? "1" : "0") + badBootHash[1..];
        Assert.Throws<InvalidDataException>(() => Resolve(
            boot,
            [3],
            expectedBootHash: badBootHash));

        var changedLookup = boot.ToArray();
        changedLookup[LookupOffset] ^= 0x80;
        Assert.Throws<InvalidDataException>(() => Resolve(
            changedLookup,
            [3],
            expectedBootHash: Hash(changedLookup),
            expectedLookupHash: Hash(boot.AsSpan(LookupOffset, LookupLength))));

        var changedTable = boot.ToArray();
        changedTable[TableOffset] ^= 0x80;
        Assert.Throws<InvalidDataException>(() => Resolve(
            changedTable,
            [3],
            expectedBootHash: Hash(changedTable),
            expectedTableHash: Hash(boot.AsSpan(TableOffset, (MaximumAlias + 1) * 2))));

        Assert.Throws<InvalidDataException>(() => Resolve(
            boot,
            [3],
            actualEffectCount: EffectCount + 1));
        Assert.Throws<InvalidDataException>(() => Resolve(boot, [5]));

        var outOfRangeDynamicTarget = new Dictionary<uint, N64CompiledSfxDynamicAliasRule>
        {
            [3] = new(3, "synthetic selector", [new("always", EffectCount)])
        };
        Assert.Throws<InvalidDataException>(() => Resolve(
            boot,
            [3],
            dynamicOverrideRules: outOfRangeDynamicTarget));

        var outOfRangeTarget = BuildBoot([0x0000, 0x0001, 0x0002, 0x0003, 0xFFFF]);
        Assert.Throws<InvalidDataException>(() => Resolve(outOfRangeTarget, []));
    }

    private static N64CompiledSfxAliasMap Resolve(
        byte[] boot,
        IReadOnlyCollection<uint> dynamicAliases,
        string? expectedBootHash = null,
        string? expectedLookupHash = null,
        string? expectedTableHash = null,
        int actualEffectCount = EffectCount,
        IReadOnlyDictionary<uint, N64CompiledSfxDynamicAliasRule>? dynamicOverrideRules = null,
        IReadOnlyDictionary<N64CompiledSfxCueContextKey, N64CompiledSfxCueContextResolution>?
            contextualResolutions = null,
        N64CompiledSfxCueOwnerLayout? cueOwnerLayout = null) =>
        N64CompiledSfxAliasMapResolver.ResolveForEvidence(
            boot,
            "synthetic build",
            expectedBootHash ?? Hash(boot),
            LookupOffset,
            LookupLength,
            expectedLookupHash ?? Hash(boot.AsSpan(LookupOffset, LookupLength)),
            TableOffset,
            MaximumAlias,
            expectedTableHash ?? Hash(boot.AsSpan(TableOffset, (MaximumAlias + 1) * 2)),
            EffectCount,
            actualEffectCount,
            dynamicOverrideRules ?? dynamicAliases.ToDictionary(
                static alias => alias,
                static alias => new N64CompiledSfxDynamicAliasRule(
                    alias,
                    "synthetic selector",
                    [
                        new N64CompiledSfxDynamicAliasCase("selector == 0", 0),
                        new N64CompiledSfxDynamicAliasCase("otherwise", ushort.MaxValue)
                    ])),
            contextualResolutions,
            cueOwnerLayout: cueOwnerLayout);

    private static byte[] BuildBoot(ushort[] entries)
    {
        Assert.Equal(MaximumAlias + 1, entries.Length);
        var boot = new byte[96];
        for (var index = 0; index < boot.Length; index++)
            boot[index] = unchecked((byte)(index * 37 + 11));
        for (var index = 0; index < entries.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                boot.AsSpan(TableOffset + index * sizeof(ushort)),
                entries[index]);
        }
        return boot;
    }

    private static byte[] BuildPackedBoot(uint[] entries)
    {
        Assert.Equal(MaximumAlias + 1, entries.Length);
        var boot = new byte[96];
        for (var index = 0; index < boot.Length; index++)
            boot[index] = unchecked((byte)(index * 37 + 11));
        for (var index = 0; index < entries.Length; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(
                boot.AsSpan(TableOffset + index * sizeof(uint)),
                entries[index]);
        }
        return boot;
    }

    private static string Hash(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data));
}
