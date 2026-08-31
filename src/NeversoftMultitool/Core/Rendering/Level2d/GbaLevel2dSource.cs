using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Core.Rendering.Level2d;

/// <summary>
///     A THPS2 GBA level as a picture: the authored isometric art the cartridge
///     composites from its own tiles, plus the two collision views.
/// </summary>
/// <remarks>
///     The art layer runs no collision code and builds no mesh — it is
///     <see cref="GbaLevelImages.RenderColourSurface" /> alone, which is how the
///     level was authored and what the game actually draws.
/// </remarks>
public sealed class GbaLevel2dSource : ILevel2dSource
{
    private static readonly Level2dLayer[] AllLayers =
        [Level2dLayer.Art, Level2dLayer.CollisionHeightfield, Level2dLayer.CollisionOverArt];

    /// <summary>The later cartridges have art but no collision grid.</summary>
    private static readonly Level2dLayer[] ArtOnly = [Level2dLayer.Art];

    private readonly byte[] _rom;
    private readonly int _trueRecordOffset;
    private readonly GbaLaterLevelArt.LaterLevel? _later;

    private GbaLevel2dSource(
        byte[] rom, int trueRecordOffset, string displayName, GbaLaterLevelArt.LaterLevel? later = null)
    {
        _rom = rom;
        _trueRecordOffset = trueRecordOffset;
        DisplayName = displayName;
        _later = later;
    }

    public IReadOnlyList<Level2dLayer> Layers => _later == null ? AllLayers : ArtOnly;

    public string DisplayName { get; }

    public Level2dRender? Render(Level2dLayer layer)
    {
        return layer switch
        {
            Level2dLayer.Art => RenderArt(),
            Level2dLayer.CollisionHeightfield => Wrap(
                GbaCollisionRenderer.Render(_rom, _trueRecordOffset)),
            Level2dLayer.CollisionOverArt => RenderOverlay(),
            _ => null
        };
    }

    /// <summary>Whether a carved entry names a level this can render.</summary>
    public static bool Supports(string fileName) =>
        MeshTypeDetector.DetectByName(fileName).Kind == MeshFileKind.GbaLevel;

    /// <summary>
    ///     Bind a carved <c>.lvl.gba</c> record to the ROM it came from, or null when
    ///     the record does not belong to that ROM.
    /// </summary>
    /// <remarks>
    ///     The record's ROM offset is recovered by content, the same way
    ///     <c>MeshModelParser.ParseGbaLevel</c> does it. Note
    ///     <see cref="GbaLevelCarver.FindRecordOffset" /> returns the FIRST match: it
    ///     does not prove the record occurs only once, and this does not claim it does.
    /// </remarks>
    public static GbaLevel2dSource? TryCreate(byte[] record, byte[] rom, string entryFileName)
    {
        // A later cartridge's art record is bound by identity rather than a byte
        // search: the ROM's own level list already states where each one lives.
        if (record.Length == GbaLaterLevelArt.ArtRecordStride)
        {
            foreach (var level in GbaLaterLevelArt.FindLevels(rom))
            {
                var offset = level.ArtRecordOffset;
                if (offset < 0 || offset + record.Length > rom.Length) continue;
                if (!rom.AsSpan(offset, record.Length).SequenceEqual(record)) continue;
                return new GbaLevel2dSource(rom, offset, $"level{level.Index}", level);
            }

            return null;
        }

        var trueRecord = GbaLevelCarver.FindRecordOffset(rom, record);
        if (trueRecord < 0) return null;

        // The ROM names its own levels; the carved entry name is the join key,
        // exactly as MeshModelParser.ParseGbaLevel resolves it.
        var name = entryFileName;
        foreach (var carved in GbaLevelCarver.ListLevels(rom))
        {
            if (!carved.EntryName.EndsWith(entryFileName, StringComparison.OrdinalIgnoreCase))
                continue;
            name = carved.Name;
            break;
        }

        return new GbaLevel2dSource(rom, trueRecord, name);
    }

    /// <summary>
    ///     The scan-relative <see cref="GbaLevelImages.GbaLevel" /> the art renderer
    ///     needs. Only <c>RecordAddress</c> is read for a colour render, and it sits
    ///     0x144 past the true record — the same reconstruction
    ///     <c>GbaLevelGeometryWriter</c> performs to texture its surface.
    /// </summary>
    private GbaLevelImages.GbaLevel ScanLevel() =>
        new((uint)(0x08000000 + _trueRecordOffset + 0x144), 0, 0, 0);

    private Level2dRender? RenderArt()
    {
        if (_later is { } later)
        {
            // One bit deep and no colour surface, so this is ink coverage rather
            // than the game's own palette (see GbaLaterLevelArt).
            var ink = GbaLaterLevelArt.Render(_rom, later);
            return ink == null ? null : new Level2dRender(ink.Value.Width, ink.Value.Height, ink.Value.Rgba);
        }

        var art = GbaLevelImages.RenderColourSurface(_rom, ScanLevel());
        return art == null ? null : new Level2dRender(art.Value.Width, art.Value.Height, art.Value.Rgba);
    }

    private Level2dRender? RenderOverlay()
    {
        var art = GbaLevelImages.RenderColourSurface(_rom, ScanLevel());
        if (art == null) return null;

        return Wrap(GbaCollisionRenderer.RenderArtOverlay(
            _rom, _trueRecordOffset, art.Value.Width, art.Value.Height, art.Value.Rgba));
    }

    private static Level2dRender? Wrap(GbaCollisionRenderer.GbaCollisionRender? render) =>
        render == null
            ? null
            : new Level2dRender(render.Value.Width, render.Value.Height, render.Value.Rgba);
}
