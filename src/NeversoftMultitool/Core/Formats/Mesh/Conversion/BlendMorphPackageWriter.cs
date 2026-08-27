using System.IO.Compression;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Writes the blend package's morph-target geometry and morph-weight tracks.
///     Split from <see cref="BlendPackageWriter" /> so the base package writer
///     stays one screen of mesh/animation plumbing.
///
///     <para>Everything here fails closed: a mesh whose primitives disagree on
///     target count, a target whose delta array does not parallel its vertex
///     buffer, or a channel that does not address a real target set is omitted
///     rather than emitted in a shape the importer would have to guess at. The
///     manifest ignores nulls, so a document without morph data produces a
///     byte-identical package to before.</para>
/// </summary>
internal static class BlendMorphPackageWriter
{
    /// <summary>
    ///     The mesh's morph-target count, or 0 when it has none or its primitives
    ///     disagree. glTF weights apply mesh-wide, so a mesh whose primitives
    ///     carry different target counts has no single well-defined track.
    /// </summary>
    public static int TargetCount(ModelMesh mesh)
    {
        if (mesh.Primitives.Count == 0)
            return 0;

        var count = mesh.Primitives[0].MorphTargets?.Count ?? 0;
        if (count == 0)
            return 0;

        foreach (var primitive in mesh.Primitives)
        {
            var targets = primitive.MorphTargets;
            if (targets == null || targets.Count != count)
                return 0;
            foreach (var target in targets)
            {
                if (target.PositionDeltas.Length != primitive.Vertices.Length)
                    return 0;
            }
        }

        return count;
    }

    public static List<BlendMorphTargetManifest>? WriteTargets(
        ZipArchive archive,
        ModelMesh mesh,
        int meshIndex,
        int primitiveIndex,
        int targetCount)
    {
        if (targetCount == 0)
            return null;

        var primitive = mesh.Primitives[primitiveIndex];
        var targets = primitive.MorphTargets!;
        var result = new List<BlendMorphTargetManifest>(targetCount);
        for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
        {
            var target = targets[targetIndex];
            var path =
                $"buffers/mesh_{meshIndex:D4}_prim_{primitiveIndex:D4}.morph_{targetIndex:D4}.bin";
            var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
            using (var stream = entry.Open())
            using (var writer = new BinaryWriter(stream))
            {
                foreach (var delta in target.PositionDeltas)
                {
                    writer.Write(delta.X);
                    writer.Write(delta.Y);
                    writer.Write(delta.Z);
                }
            }

            result.Add(new BlendMorphTargetManifest
            {
                Name = target.Name,
                PositionDeltaBuffer = path,
                VertexCount = target.PositionDeltas.Length
            });
        }

        return result;
    }

    public static BlendMorphChannelManifest? WriteChannel(
        ZipArchive archive,
        ModelDocument document,
        ModelAnimation animation,
        int animationIndex)
    {
        var channel = animation.MorphChannel;
        if (channel == null ||
            (uint)channel.MeshIndex >= (uint)document.Meshes.Count ||
            channel.TargetCount <= 0 ||
            channel.KeyCount <= 0 ||
            channel.Weights.Length != channel.KeyCount * channel.TargetCount ||
            channel.Times.Any(static time => !float.IsFinite(time)) ||
            channel.Weights.Any(static weight => !float.IsFinite(weight)) ||
            TargetCount(document.Meshes[channel.MeshIndex]) != channel.TargetCount)
        {
            return null;
        }

        var timesPath = $"buffers/anim_{animationIndex:D4}_morph.times.bin";
        var weightsPath = $"buffers/anim_{animationIndex:D4}_morph.weights.bin";
        BlendPackageWriter.WriteFloatBuffer(archive, timesPath, channel.Times);
        BlendPackageWriter.WriteFloatBuffer(archive, weightsPath, channel.Weights);
        return new BlendMorphChannelManifest
        {
            MeshIndex = channel.MeshIndex,
            TargetCount = channel.TargetCount,
            TimesBuffer = timesPath,
            WeightsBuffer = weightsPath,
            KeyCount = channel.KeyCount
        };
    }
}
