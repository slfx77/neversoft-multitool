using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Stable, lossless-enough inspection view of a THUG intermediate SKA.
///     This is deliberately separate from glTF export: the embedded skeleton
///     has hierarchy/name tables but no proven neutral pose for skin binding.
/// </summary>
internal static class SkaIntermediateJsonExporter
{
    internal const string SchemaName = "neversoft.ska.intermediate";
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    internal static void Write(string outputPath, string sourceFile, SkaAnimation animation)
    {
        var json = Serialize(sourceFile, animation);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(outputPath, json);
    }

    internal static string Serialize(string sourceFile, SkaAnimation animation)
    {
        var metadata = animation.IntermediateMetadata
                       ?? throw new InvalidOperationException(
                           "SKA intermediate JSON requires intermediate metadata");
        if (animation.BoneTracks.Length != metadata.BoneNameChecksums.Length ||
            animation.BoneTracks.Length != metadata.ParentNameChecksums.Length ||
            animation.BoneTracks.Length != metadata.FlipNameChecksums.Length ||
            animation.BoneTracks.Length != metadata.RotationFrames.Length ||
            animation.BoneTracks.Length != metadata.TranslationFrames.Length ||
            animation.BoneTracks.Length != metadata.SourceRotations.Length)
        {
            throw new InvalidDataException(
                "SKA intermediate metadata arrays do not match the track count");
        }

        var bones = new IntermediateBone[animation.BoneTracks.Length];
        for (var i = 0; i < bones.Length; i++)
        {
            var track = animation.BoneTracks[i];
            var qFrames = metadata.RotationFrames[i];
            var tFrames = metadata.TranslationFrames[i];
            var sourceRotations = metadata.SourceRotations[i];
            if (track.BoneIndex != i ||
                track.RotationKeys.Length != qFrames.Length ||
                track.RotationKeys.Length != sourceRotations.Length ||
                track.TranslationKeys.Length != tFrames.Length)
            {
                throw new InvalidDataException(
                    $"SKA intermediate metadata does not match track {i}");
            }

            bones[i] = new IntermediateBone
            {
                Index = i,
                NameChecksum = Hex(metadata.BoneNameChecksums[i]),
                Name = QbKey.QbKey.TryResolve(metadata.BoneNameChecksums[i]),
                ParentChecksum = Hex(metadata.ParentNameChecksums[i]),
                ParentName = ResolveNonzero(metadata.ParentNameChecksums[i]),
                FlipChecksum = Hex(metadata.FlipNameChecksums[i]),
                FlipName = ResolveNonzero(metadata.FlipNameChecksums[i]),
                RotationKeys = track.RotationKeys
                    .Select((key, keyIndex) => ToRotationKey(
                        qFrames[keyIndex], sourceRotations[keyIndex], key))
                    .ToArray(),
                TranslationKeys = track.TranslationKeys
                    .Select((key, keyIndex) => ToTranslationKey(tFrames[keyIndex], key))
                    .ToArray()
            };
        }

        var manifest = new IntermediateManifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Source = Path.GetFileName(sourceFile),
            FileVersion = animation.Version,
            Flags = Hex(animation.Flags),
            DurationSeconds = animation.Duration,
            TimebaseHz = 60,
            SkeletonChecksum = Hex(metadata.SkeletonChecksum),
            BoneCount = bones.Length,
            RotationKeyCount = bones.Sum(static bone => bone.RotationKeys.Length),
            TranslationKeyCount = bones.Sum(static bone => bone.TranslationKeys.Length),
            CustomKeyCount = animation.CustomKeys.Length,
            Bones = bones
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static IntermediateRotationKey ToRotationKey(
        uint frame, System.Numerics.Vector4 source, SkaRotationKey key)
    {
        var q = key.Rotation;
        return new IntermediateRotationKey
        {
            Frame = frame,
            TimeSeconds = key.Time,
            SourceQuaternionXyzw = [source.X, source.Y, source.Z, source.W],
            EngineQuaternionXyzw = [q.X, q.Y, q.Z, q.W]
        };
    }

    private static IntermediateTranslationKey ToTranslationKey(uint frame, SkaTranslationKey key)
    {
        var t = key.Translation;
        return new IntermediateTranslationKey
        {
            Frame = frame,
            TimeSeconds = key.Time,
            Translation = [t.X, t.Y, t.Z]
        };
    }

    private static string Hex(uint value) => $"0x{value:X8}";

    private static string? ResolveNonzero(uint value) =>
        value == 0 ? null : QbKey.QbKey.TryResolve(value);

    private sealed class IntermediateManifest
    {
        public required string Schema { get; init; }
        public required int SchemaVersion { get; init; }
        public required string Source { get; init; }
        public required uint FileVersion { get; init; }
        public required string Flags { get; init; }
        public required float DurationSeconds { get; init; }
        public required int TimebaseHz { get; init; }
        public required string SkeletonChecksum { get; init; }
        public required int BoneCount { get; init; }
        public required int RotationKeyCount { get; init; }
        public required int TranslationKeyCount { get; init; }
        public required int CustomKeyCount { get; init; }
        public required IntermediateBone[] Bones { get; init; }
    }

    private sealed class IntermediateBone
    {
        public required int Index { get; init; }
        public required string NameChecksum { get; init; }
        public string? Name { get; init; }
        public required string ParentChecksum { get; init; }
        public string? ParentName { get; init; }
        public required string FlipChecksum { get; init; }
        public string? FlipName { get; init; }
        public required IntermediateRotationKey[] RotationKeys { get; init; }
        public required IntermediateTranslationKey[] TranslationKeys { get; init; }
    }

    private sealed class IntermediateRotationKey
    {
        public required uint Frame { get; init; }
        public required float TimeSeconds { get; init; }
        public required float[] SourceQuaternionXyzw { get; init; }
        public required float[] EngineQuaternionXyzw { get; init; }
    }

    private sealed class IntermediateTranslationKey
    {
        public required uint Frame { get; init; }
        public required float TimeSeconds { get; init; }
        public required float[] Translation { get; init; }
    }
}
