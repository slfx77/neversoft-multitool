using System.Globalization;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Gob;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Texture.Nds;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     The texture halves of a DS container: every parsed bank, and every texel blob
///     indexed by the id its filename encodes.
///
///     A model's bank is STATED rather than inferred whenever the model's name was
///     recovered — the two share the model set's first id (see
///     <see cref="NdsModelSet" />). For a model with no recovered name the bank falls
///     back to <see cref="NdsTextureBankResolver" />, which joins on the GX state
///     both sides declare and speaks only when exactly one bank is compatible.
/// </summary>
public sealed class NdsTextureLookup
{
    private readonly List<IReadOnlyList<NdsTextureEntry>> _banks = [];
    private readonly Dictionary<uint, IReadOnlyList<NdsTextureEntry>> _banksByKey = [];
    private readonly Dictionary<uint, ArchiveEntry> _texels = [];
    private readonly IArchiveFileSystem _container;

    private NdsTextureLookup(IArchiveFileSystem container)
    {
        _container = container;
    }

    public static NdsTextureLookup Build(IArchiveFileSystem container)
    {
        var catalog = new NdsTextureLookup(container);
        foreach (var entry in container.Entries)
        {
            var name = entry.Name;
            if (name.EndsWith(".texture.bin", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(name.AsSpan(0, Math.Min(8, name.Length)),
                    NumberStyles.HexNumber, null, out var id))
            {
                catalog._texels[id] = entry;
            }
        }

        long? PixelLength(uint id) => catalog._texels.TryGetValue(id, out var e) ? e.Size : null;

        foreach (var entry in container.Entries)
        {
            byte[] data;
            try
            {
                data = container.ReadEntry(entry);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                continue;
            }

            if (!NdsTextureBank.TryParseValidated(data, PixelLength, out var bank))
                continue;

            catalog._banks.Add(bank);
            catalog._banksByKey[entry.Crc] = bank;
        }

        return catalog;
    }

    /// <summary>
    ///     The bank for one model. Prefers the binding the loader spells — the model
    ///     set's own id — and falls back to the GX-state join when the model's name
    ///     was not recovered.
    /// </summary>
    public NdsTextureSource? For(ArchiveEntry entry, IReadOnlyList<NdsGeometryGroup> groups)
    {
        if (NdsModelSet.TryParseGeometryName(GobNames.TryResolve(entry.Crc), out var idA, out _)
            && _banksByKey.TryGetValue(NdsModelSet.TextureBankKey(idA), out var stated))
        {
            return new NdsTextureSource(stated, ReadTexels);
        }

        var joined = NdsTextureBankResolver.Resolve(groups, _banks);
        return joined == null ? null : new NdsTextureSource(joined, ReadTexels);
    }

    private byte[]? ReadTexels(uint pixelId)
    {
        if (!_texels.TryGetValue(pixelId, out var entry))
            return null;
        try
        {
            return _container.ReadEntry(entry);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }
}
