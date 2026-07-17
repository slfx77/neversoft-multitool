using System.Security.Cryptography;

namespace NeversoftMultitool.Core.Formats.Mesh.RenderWare;

internal readonly record struct ArchiveRwDffFingerprint(string MeshSha256, string TextureSha256);

internal readonly record struct ArchiveRwDffCopyCandidate(
    string FileName,
    int NestingDepth,
    ArchiveRwDffFingerprint? Fingerprint);

/// <summary>
///     Selects exact nested RW-DFF copies that can be hidden in favor of a root-archive copy.
///     Both the mesh and same-stem texture payload must match; missing companions and variants remain.
/// </summary>
internal static class ArchiveRwDffCopyDeduplicator
{
    internal static ArchiveRwDffFingerprint Fingerprint(byte[] meshBytes, byte[] textureBytes)
    {
        return new ArchiveRwDffFingerprint(
            Convert.ToHexString(SHA256.HashData(meshBytes)),
            Convert.ToHexString(SHA256.HashData(textureBytes)));
    }

    internal static IReadOnlyList<int> SelectIndicesToKeep(
        IReadOnlyList<ArchiveRwDffCopyCandidate> candidates)
    {
        var keep = Enumerable.Repeat(true, candidates.Count).ToArray();
        var namedGroups = Enumerable.Range(0, candidates.Count)
            .GroupBy(index => candidates[index].FileName, StringComparer.OrdinalIgnoreCase);

        foreach (var namedGroup in namedGroups)
        {
            foreach (var fingerprintGroup in namedGroup
                         .Where(index => candidates[index].Fingerprint.HasValue)
                         .GroupBy(index => candidates[index].Fingerprint!.Value))
            {
                if (!fingerprintGroup.Any(index => candidates[index].NestingDepth == 0))
                    continue;

                foreach (var index in fingerprintGroup)
                {
                    if (candidates[index].NestingDepth > 0)
                        keep[index] = false;
                }
            }
        }

        return Enumerable.Range(0, candidates.Count).Where(index => keep[index]).ToArray();
    }
}
