namespace NeversoftMultitool.Core.Formats.Mesh;

/// <summary>
/// Shared hash lookup for mapping PSX mesh name hashes to DDM object indices.
/// Supports duplicate DDM objects (same name/hash) via occurrence-based resolution.
/// </summary>
public static class DdmHashLookup
{
    /// <summary>
    /// Builds a hash → DDM indices map. Each hash maps to a list of DDM object
    /// indices in file order, supporting duplicate-named objects.
    /// </summary>
    public static Dictionary<uint, List<int>> Build(DdmFile ddm)
    {
        var lookup = new Dictionary<uint, List<int>>();

        for (var i = 0; i < ddm.Objects.Count; i++)
        {
            var obj = ddm.Objects[i];
            var nameHash = QbKey.Hash(obj.Name);

            if (!lookup.TryGetValue(nameHash, out var list))
            {
                list = [];
                lookup[nameHash] = list;
            }
            list.Add(i);

            // Also register by checksum if it differs from name hash
            if (obj.Checksum != 0 && obj.Checksum != nameHash)
            {
                if (!lookup.TryGetValue(obj.Checksum, out var checksumList))
                {
                    checksumList = [];
                    lookup[obj.Checksum] = checksumList;
                }
                checksumList.Add(i);
            }
        }

        return lookup;
    }

    /// <summary>
    /// Pre-resolves each PSX mesh index slot to a specific DDM object index.
    /// When multiple mesh slots share the same hash, the Nth occurrence maps
    /// to the Nth DDM object with that hash (by file order). Clamps to the
    /// last DDM object if PSX has more occurrences than DDM objects for a hash.
    /// </summary>
    public static Dictionary<int, int> ResolveMeshIndices(
        PsxLayoutFile psxFile, Dictionary<uint, List<int>> ddmByHash)
    {
        var result = new Dictionary<int, int>();
        var hashOccurrences = new Dictionary<uint, int>();

        for (var meshIdx = 0; meshIdx < psxFile.MeshNameHashes.Length; meshIdx++)
        {
            var hash = psxFile.MeshNameHashes[meshIdx];
            if (!ddmByHash.TryGetValue(hash, out var ddmIndices))
                continue;

            hashOccurrences.TryGetValue(hash, out var occurrence);
            var ddmListIndex = Math.Min(occurrence, ddmIndices.Count - 1);
            result[meshIdx] = ddmIndices[ddmListIndex];
            hashOccurrences[hash] = occurrence + 1;
        }

        return result;
    }
}
