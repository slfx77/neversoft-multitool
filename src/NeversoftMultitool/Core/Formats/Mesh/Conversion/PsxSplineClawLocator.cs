using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Finds the runtime tip model instanced at Ock's spline endpoints from
///     content rather than an asset name or mesh checksum. A self-contained
///     one-object kit wins when unique; otherwise a bank mesh must carry the
///     mapped hidden strip-template record that links its tip geometry to the
///     procedural tube texture. Ambiguous scopes deliberately export tubes
///     without tips.
/// </summary>
internal static class PsxSplineClawLocator
{
    private static readonly ConcurrentDictionary<string, FileSystemDiscovery>
        FileSystemDiscoveries = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConditionalWeakTable<
        ArchiveAssetBackend,
        ConcurrentDictionary<string, Lazy<ResolvedClaw?>>>
        ArchiveDiscoveries = new();

    internal static ResolvedClaw? Locate(AssetSource characterSource)
    {
        if (characterSource.FileSystemPath is { } fsPath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(fsPath));
            if (directory == null)
                return null;

            var snapshot = CaptureFileSystemSnapshot(directory);
            return FileSystemDiscoveries.AddOrUpdate(
                directory,
                _ => CreateFileSystemDiscovery(snapshot),
                (_, cached) => string.Equals(
                    cached.Fingerprint,
                    snapshot.Fingerprint,
                    StringComparison.Ordinal)
                    ? cached
                    : CreateFileSystemDiscovery(snapshot)).Result.Value;
        }

        if (characterSource is not ArchiveAssetSource archiveSource)
            return null;

        var archiveDirectory = GetArchiveEntryDirectory(archiveSource.Entry);
        var discoveries = ArchiveDiscoveries.GetValue(
            archiveSource.Backend,
            static _ => new ConcurrentDictionary<string, Lazy<ResolvedClaw?>>(
                StringComparer.OrdinalIgnoreCase));
        return discoveries.GetOrAdd(
            archiveDirectory,
            path => new Lazy<ResolvedClaw?>(
                () => Discover(EnumerateArchiveSources(archiveSource.Backend, path)),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static ResolvedClaw? Discover(IEnumerable<AssetSource> sources)
    {
        var standalone = new List<ResolvedClaw>();
        var mappedBanks = new List<ResolvedClaw>();
        var legacyBanks = new List<ResolvedClaw>();
        foreach (var source in sources)
        {
            try
            {
                var bytes = source.ReadBytes();
                var file = PsxMeshFile.Parse(bytes);
                if (file == null)
                    continue;

                var textureProvider = MeshCompanionResolver.BuildPsxTextureProvider(
                    source, source.EntryName, bytes);
                foreach (var candidate in FindCandidates(file, textureProvider))
                {
                    var resolved = new ResolvedClaw(
                        file,
                        candidate.ObjectIndex,
                        candidate.MeshIndex,
                        textureProvider);
                    if (file.Objects.Count == 1 && file.Meshes.Count == 1)
                    {
                        standalone.Add(resolved);
                    }
                    else if (PsxSplineAppendageGeometry.FindMappedTubeTextureHash(
                                 file, candidate.MeshIndex, textureProvider).HasValue)
                    {
                        mappedBanks.Add(resolved);
                    }
                    else if (IsConservativeLegacyTipMesh(file.Meshes[candidate.MeshIndex]))
                    {
                        legacyBanks.Add(resolved);
                    }
                }
            }
            catch
            {
                // A malformed or unrelated sibling is not evidence for or
                // against a kit elsewhere in the same scope.
            }
        }

        // Retail scopes can contain both the self-contained kit and a copy in
        // a level bank. Prefer the unique dedicated payload without relying on
        // its filename. Multiple candidates in either tier are unsafe.
        if (standalone.Count > 0)
            return standalone.Count == 1 ? standalone[0] : null;

        if (mappedBanks.Count > 0)
            return mappedBanks.Count == 1 ? mappedBanks[0] : null;

        return legacyBanks.Count == 1 ? legacyBanks[0] : null;
    }

    private static IEnumerable<ClawCandidate> FindCandidates(
        PsxMeshFile file,
        MeshChecksumTextureResolver textureProvider)
    {
        for (var meshIndex = 0; meshIndex < file.Meshes.Count; meshIndex++)
        {
            if (!IsDrawableTipMesh(file.Meshes[meshIndex], textureProvider))
                continue;

            var objectIndices = FindPlacingObjects(file, meshIndex).Take(2).ToArray();
            if (objectIndices.Length == 1)
                yield return new ClawCandidate(objectIndices[0], meshIndex);
        }
    }

    private static bool IsDrawableTipMesh(
        PsxMesh mesh,
        MeshChecksumTextureResolver textureProvider)
    {
        // Both authored generations describe the same compact four-prong tip:
        // 22-28 vertices, 22-24 source faces, and 40 emitted triangles. This
        // excludes the other single-object 40-triangle corpus asset while
        // allowing the older mixed-quad and newer triangulated encodings.
        if (mesh.Vertices.Count is < 22 or > 28
            || mesh.Faces.Count is < 22 or > 24
            || mesh.Faces.Sum(static face => face.IsQuad ? 2 : 1) != 40)
        {
            return false;
        }

        var textureHashes = mesh.Faces
            .Where(static face => face.IsTextured && face.TextureHash != 0)
            .Select(static face => face.TextureHash)
            .Distinct()
            .ToArray();
        if (IsConservativeLegacyTipMesh(mesh))
        {
            // The 2/18 and 4/29 bank copies retain an odd prototype texture
            // handle (plus one zero-hash face) that no standalone library
            // resolves. Their exact compact bounds and topology are corpus-unique.
            // keep the drawable tip even when its old material cannot resolve.
            return mesh.Faces.All(static face => face.IsTextured)
                   && textureHashes.Length > 0;
        }

        return mesh.Faces.All(static face => face.IsTextured && face.TextureHash != 0)
               && textureHashes.Length == 1
               && IsResolvableSquareTexture(textureHashes[0], textureProvider);
    }

    private static bool IsResolvableSquareTexture(
        uint textureHash,
        MeshChecksumTextureResolver textureProvider)
    {
        return textureProvider(textureHash) is { } pngBytes
               && ModelDocumentGeometryAdapter.TryExtractPngDimensions(pngBytes) is { } dimensions
               && dimensions.Width == dimensions.Height
               && dimensions.Width is >= 16 and <= 64;
    }

    private static bool IsConservativeLegacyTipMesh(PsxMesh mesh)
    {
        if (mesh.Vertices.Count != 22
            || mesh.Vertices.Any(static vertex => vertex.Type != 0)
            || mesh.Faces.Count != 24
            || mesh.Faces.Count(static face => face.IsQuad) != 16)
        {
            return false;
        }

        return mesh.Vertices.Min(static vertex => vertex.RawX) == -6
               && mesh.Vertices.Max(static vertex => vertex.RawX) == 8
               && mesh.Vertices.Min(static vertex => vertex.RawY) == -16
               && mesh.Vertices.Max(static vertex => vertex.RawY) == 13
               && mesh.Vertices.Min(static vertex => vertex.RawZ) == -26
               && mesh.Vertices.Max(static vertex => vertex.RawZ) == 0;
    }

    private static IEnumerable<int> FindPlacingObjects(PsxMeshFile file, int meshIndex)
    {
        if (PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(file))
        {
            if (meshIndex < file.Objects.Count)
                yield return meshIndex;
            yield break;
        }

        for (var objectIndex = 0; objectIndex < file.Objects.Count; objectIndex++)
        {
            if (file.Objects[objectIndex].MeshIndex == meshIndex)
                yield return objectIndex;
        }
    }

    private static FileSystemSnapshot CaptureFileSystemSnapshot(string directory)
    {
        var entries = Directory.EnumerateFiles(directory)
            .Where(static path => path.EndsWith(".psx", StringComparison.OrdinalIgnoreCase))
            .Select(static path =>
            {
                var info = new FileInfo(path);
                return new FileSystemPsxEntry(
                    path,
                    info.Name,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks);
            })
            .OrderBy(static entry => entry.Name, StringComparer.Ordinal)
            .ToArray();

        // Keep the fingerprint deterministic and collision-free for the
        // metadata contract. Prefixing the name length prevents separators in
        // a valid filename from making two different snapshots equivalent.
        var fingerprint = new StringBuilder(entries.Length * 48);
        foreach (var entry in entries)
        {
            fingerprint.Append(entry.Name.Length)
                .Append(':')
                .Append(entry.Name)
                .Append('|')
                .Append(entry.Length)
                .Append('|')
                .Append(entry.LastWriteTimeUtcTicks)
                .Append('\n');
        }

        return new FileSystemSnapshot(entries, fingerprint.ToString());
    }

    private static FileSystemDiscovery CreateFileSystemDiscovery(
        FileSystemSnapshot snapshot)
    {
        return new FileSystemDiscovery(
            snapshot.Fingerprint,
            new Lazy<ResolvedClaw?>(
                () => Discover(snapshot.Entries.Select(static entry =>
                    (AssetSource)new FileSystemAssetSource(entry.Path))),
                LazyThreadSafetyMode.ExecutionAndPublication));
    }

    private static IEnumerable<AssetSource> EnumerateArchiveSources(
        ArchiveAssetBackend backend,
        string directory)
    {
        return backend.Entries
            .Where(static entry => entry.Name.EndsWith(
                ".psx", StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.Equals(
                GetArchiveEntryDirectory(entry), directory,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => (AssetSource)new ArchiveAssetSource(backend, entry));
    }

    private static string GetArchiveEntryDirectory(ArchiveEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Directory))
            return entry.Directory.Replace('\\', '/').Trim('/');

        var normalized = entry.Name.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator].Trim('/');
    }

    private readonly record struct ClawCandidate(int ObjectIndex, int MeshIndex);

    private readonly record struct FileSystemPsxEntry(
        string Path,
        string Name,
        long Length,
        long LastWriteTimeUtcTicks);

    private sealed record FileSystemSnapshot(
        IReadOnlyList<FileSystemPsxEntry> Entries,
        string Fingerprint);

    private sealed record FileSystemDiscovery(
        string Fingerprint,
        Lazy<ResolvedClaw?> Result);

    internal sealed record ResolvedClaw(
        PsxMeshFile File,
        int ObjectIndex,
        int MeshIndex,
        MeshChecksumTextureResolver TextureProvider);
}
