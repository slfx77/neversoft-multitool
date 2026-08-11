using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Authoring-only metadata carried by THUG's intermediate SKA files. The
///     embedded skeleton is the old checksum/name-table form: it identifies
///     tracks and their hierarchy, but contains no neutral-pose matrices.
/// </summary>
internal sealed class SkaIntermediateMetadata
{
    internal required uint SkeletonChecksum { get; init; }
    internal required uint[] BoneNameChecksums { get; init; }
    internal required uint[] ParentNameChecksums { get; init; }
    internal required uint[] FlipNameChecksums { get; init; }

    /// <summary>
    ///     Raw integer frame indices retained alongside the seconds-based SKA
    ///     IR so JSON inspection does not have to reconstruct them from floats.
    /// </summary>
    internal required uint[][] RotationFrames { get; init; }
    internal required uint[][] TranslationFrames { get; init; }

    /// <summary>
    ///     The exact XYZW authoring floats. <see cref="SkaRotationKey"/> uses
    ///     the engine-facing conjugated convention, so inspection retains both.
    /// </summary>
    internal required Vector4[][] SourceRotations { get; init; }
}
