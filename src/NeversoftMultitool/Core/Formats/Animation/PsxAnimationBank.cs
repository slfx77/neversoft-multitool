using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Animation;

internal static class PsxAnimationBank
{
    /// <summary>
    ///     Engine playback rate. <c>CSuper::UpdateFrame</c> (OB.cpp:1034)
    ///     advances the 16.16 frame accumulator by
    ///     <c>AdjustedSpeed = mAnimSpeed × TimeScale >> 8</c>, and the default
    ///     <c>mAnimSpeed</c> is 0x10000 (1.0), so at nominal speed it is one
    ///     anim frame per GAME TICK.
    ///     <para>
    ///         The tick is <b>30 Hz, not 60</b>. <c>FPS_Display</c>
    ///         (MAIN.cpp:1649-1690) sets
    ///         <c>TimeScale = 256 × Total / (2 × 3)</c>, where <c>Total</c> is
    ///         <c>Xblanks</c> summed over three game frames. <c>TimeScale</c>
    ///         therefore reaches its neutral 256 (1.0 in 8.8) at
    ///         <c>Total == 6</c> — two vblanks per game frame — which is the
    ///         rate the whole time base is normalised against. The same
    ///         function's own <c>averageFPS = 60 × 3 / Total</c> reads 30 at
    ///         that point, and its <c>XFramesShifted += TimeScale × 2</c>
    ///         keeps the separate <b>60 Hz</b> Xblank counter that surface
    ///         animation (UV wibbles, colour pulses) runs on. Two clocks, two
    ///         purposes: do not collapse them.
    ///     </para>
    ///     <para>
    ///         The matched decomp's converter handoff
    ///         (<c>docs/anim_metadata_tables.md</c> §1) advises "1 anim-frame
    ///         per game tick at 60 Hz". That guidance is <b>wrong</b> — it
    ///         conflates the vblank interrupt rate with the game tick rate.
    ///         Adopting it would double every exported clip's duration.
    ///     </para>
    ///     (The old 10 fps default predates tween keyframe expansion, when v1
    ///     anims decoded to a fraction of their real frame counts and looked
    ///     like flicker at engine speed.)
    /// </summary>
    public const float DefaultPreviewFps = 30f;

    public static PsxAnimationBankInfo? TryProbe(
        AssetSource source, int? targetBoneCount)
    {
        try
        {
            return TryProbe(source, source.ReadBytes(), targetBoneCount);
        }
        catch
        {
            return null;
        }
    }

    public static PsxAnimationBankInfo? TryProbe(
        AssetSource source, byte[] data, int? targetBoneCount)
    {
        try
        {
            var psxFile = PsxMeshFile.ParseHeaderOnly(data);
            if (psxFile == null) return null;

            var sourceBoneCount = psxFile.Objects.Count;
            var animFile = PsxAnimFile.Parse(data, targetBoneCount ?? sourceBoneCount);
            if (animFile == null || animFile.Entries.Count == 0) return null;

            var matches = !targetBoneCount.HasValue || sourceBoneCount == targetBoneCount.Value;
            return new PsxAnimationBankInfo(source, animFile, sourceBoneCount, matches);
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<AnimationProbe> CreateProbes(
        AssetSource source,
        int? targetBoneCount,
        Func<int, string>? displayNameFactory = null,
        PsxAnimationBoneRemap? boneRemap = null)
    {
        var bank = TryProbe(source, targetBoneCount);
        if (bank == null) return [];

        var decodeBoneCount = targetBoneCount ?? bank.BoneCount;
        var results = new List<AnimationProbe>(bank.AnimFile.Entries.Count);
        for (var i = 0; i < bank.AnimFile.Entries.Count; i++)
        {
            var entry = bank.AnimFile.Entries[i];
            var displayName = displayNameFactory?.Invoke(i)
                              ?? $"{Path.GetFileName(source.EntryName)}::anim_{i}";
            var animSource = new PsxAnimationSource(
                source, i, entry.FrameCount, decodeBoneCount, displayName, boneRemap);
            results.Add(new AnimationProbe(
                animSource,
                displayName,
                entry.FrameCount / DefaultPreviewFps,
                bank.BoneCount,
                bank.MatchesTargetBoneCount,
                entry.FrameCount));
        }

        return results;
    }

    public static IReadOnlyList<PsxAnimationBankSelection> ResolveSelections(
        PsxAnimFile animFile,
        int animIndex,
        string? animName,
        string? namePrefix)
    {
        var usePrefix = !string.IsNullOrWhiteSpace(namePrefix);
        if (animIndex == -1)
        {
            return Enumerable.Range(0, animFile.Entries.Count)
                .Select(i => new PsxAnimationBankSelection(i, BuildName(i)))
                .ToList();
        }

        if (animIndex < -1 || animIndex >= animFile.Entries.Count)
            return [];

        return [new PsxAnimationBankSelection(animIndex, BuildName(animIndex))];

        string BuildName(int index)
        {
            if (usePrefix)
                return $"{namePrefix}_anim_{index}";

            return string.IsNullOrWhiteSpace(animName)
                ? $"anim_{index}"
                : animName;
        }
    }

    public static PsxAnimationBankDecodeResult Decode(
        PsxAnimationBankInfo bank,
        int targetBoneCount,
        IReadOnlyList<PsxAnimationBankSelection> selected,
        PsxAnimationBoneRemap? boneRemap = null,
        bool oneShot = false)
    {
        var decoded = new List<(string Name, PsxAnimation Animation)>(selected.Count);
        var diagnostics = new List<PsxAnimationDecodeDiagnostic>(selected.Count);

        if (bank.BoneCount != targetBoneCount)
        {
            foreach (var selection in selected)
            {
                diagnostics.Add(new PsxAnimationDecodeDiagnostic(
                    selection.Index,
                    selection.Name,
                    GetFrameCountOrZero(bank.AnimFile, selection.Index),
                    null,
                    $"bank has {bank.BoneCount} bones; target has {targetBoneCount}"));
            }

            return new PsxAnimationBankDecodeResult(bank, decoded, diagnostics);
        }

        foreach (var selection in selected)
            DecodeOne(bank, targetBoneCount, selection, boneRemap, decoded, diagnostics, oneShot);

        return new PsxAnimationBankDecodeResult(bank, decoded, diagnostics);
    }

    /// <summary>
    ///     Builds the translation parent table for anims decoded from
    ///     <paramref name="bankSource" />, remapped into target bone order. The
    ///     engine composes anim translations through the hierarchy that ships
    ///     with the anim data (pHierarchy), so clips from an external bank must
    ///     chain through the bank's parents, not the target character's. Null
    ///     when the bank carries no object hierarchy (or fails to parse) — the
    ///     caller then falls back to the character's own hierarchy.
    /// </summary>
    public static int[]? TryBuildSourceParentIndices(
        AssetSource bankSource,
        int targetBoneCount,
        PsxAnimationBoneRemap? remap)
    {
        PsxMeshFile? sourceHeader;
        try
        {
            sourceHeader = PsxMeshFile.ParseHeaderOnly(bankSource.ReadBytes());
        }
        catch
        {
            sourceHeader = null;
        }

        if (sourceHeader == null || sourceHeader.Objects.Count == 0)
            return null;

        var sourceLimit = Math.Min(targetBoneCount, sourceHeader.Objects.Count);
        var sourceToTarget = new int[sourceLimit];
        for (var source = 0; source < sourceToTarget.Length; source++)
        {
            sourceToTarget[source] = remap != null && source < remap.SourceToTarget.Count
                ? remap.SourceToTarget[source]
                : source;
        }

        var targetParents = new int[targetBoneCount];
        Array.Fill(targetParents, -1);
        for (var source = 0; source < sourceLimit; source++)
        {
            var target = sourceToTarget[source];
            if (target < 0 || target >= targetBoneCount)
                continue;

            var sourceParent = sourceHeader.Objects[source].ParentIndex;
            var targetParent = -1;
            if (sourceParent >= 0 && sourceParent < sourceToTarget.Length)
                targetParent = sourceToTarget[sourceParent];

            if (targetParent < 0 || targetParent >= targetBoneCount || targetParent == target)
                targetParent = -1;

            targetParents[target] = targetParent;
        }

        return targetParents;
    }

    /// <summary>
    ///     Decodes a single slot. <paramref name="oneShot" /> selects the
    ///     end-of-clip expansion branch for tween-compressed clips exactly as
    ///     <see cref="Decode" /> does — see <see cref="PsxAnimationOptions" />
    ///     for why the file cannot record which branch the engine used.
    /// </summary>
    public static PsxAnimation DecodeSlot(
        AssetSource source,
        int targetBoneCount,
        int animIndex,
        PsxAnimationBoneRemap? boneRemap = null,
        bool oneShot = false)
    {
        var bank = TryProbe(source, targetBoneCount)
                   ?? throw new InvalidDataException(
                       $"PSX file has no recognizable animation table: {source.DisplayName}");

        if (bank.BoneCount != targetBoneCount)
        {
            throw new InvalidDataException(
                $"PSX animation bank has {bank.BoneCount} bones; target has {targetBoneCount}: " +
                source.DisplayName);
        }

        if (animIndex < 0 || animIndex >= bank.AnimFile.Entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(animIndex),
                $"Anim index {animIndex} out of range (0..{bank.AnimFile.Entries.Count - 1}).");
        }

        var result = Decode(
            bank,
            targetBoneCount,
            [new PsxAnimationBankSelection(animIndex, $"anim_{animIndex}")],
            boneRemap,
            oneShot);
        if (result.Animations.Count == 1)
            return result.Animations[0].Animation;

        var error = result.Diagnostics.Count > 0
            ? result.Diagnostics[0].Error
            : null;
        error ??=
            $"Anim index {animIndex} could not be decoded.";
        throw new InvalidDataException(error);
    }

    private static void DecodeOne(
        PsxAnimationBankInfo bank,
        int targetBoneCount,
        PsxAnimationBankSelection selection,
        PsxAnimationBoneRemap? boneRemap,
        List<(string Name, PsxAnimation Animation)> decoded,
        List<PsxAnimationDecodeDiagnostic> diagnostics,
        bool oneShot = false)
    {
        if (selection.Index < 0 || selection.Index >= bank.AnimFile.Entries.Count)
        {
            diagnostics.Add(new PsxAnimationDecodeDiagnostic(
                selection.Index,
                selection.Name,
                0,
                null,
                "animation index out of range"));
            return;
        }

        var entry = bank.AnimFile.Entries[selection.Index];
        try
        {
            var slice = bank.AnimFile.Pool.Span[entry.PoolOffset..];
            PsxAnimation animation;
            int consumed;
            if (bank.AnimFile.IsDirectMatrix)
            {
                animation = PsxAnimDecoder.DecodeDirectMatrix(
                    slice, bank.BoneCount, entry.FrameCount, entry.TweenFlag, oneShot);
                consumed = bank.BoneCount *
                           PsxAnimDecoder.GetDirectMatrixStoredFrameCount(
                               entry.FrameCount, entry.TweenFlag) *
                           PsxAnimDecoder.DirectMatrixStrideBytes;
            }
            else
            {
                animation = PsxAnimDecoder.Decode(
                    slice, bank.BoneCount, entry.FrameCount, out consumed);
            }

            animation = PsxAnimationBoneMap.Remap(animation, boneRemap, targetBoneCount);

            if (animation.BoneCount != targetBoneCount)
            {
                diagnostics.Add(new PsxAnimationDecodeDiagnostic(
                    selection.Index,
                    selection.Name,
                    entry.FrameCount,
                    consumed,
                    $"decoded {animation.BoneCount} bones; target has {targetBoneCount}"));
                return;
            }

            decoded.Add((selection.Name, animation));
            diagnostics.Add(new PsxAnimationDecodeDiagnostic(
                selection.Index,
                selection.Name,
                entry.FrameCount,
                consumed,
                null));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new PsxAnimationDecodeDiagnostic(
                selection.Index,
                selection.Name,
                entry.FrameCount,
                null,
                ex.Message));
        }
    }

    private static int GetFrameCountOrZero(PsxAnimFile animFile, int index)
    {
        return index >= 0 && index < animFile.Entries.Count
            ? animFile.Entries[index].FrameCount
            : 0;
    }
}
