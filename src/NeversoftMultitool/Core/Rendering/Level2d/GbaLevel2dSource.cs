using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Core.Rendering.Level2d;

/// <summary>
///     A Vicarious Visions GBA level as a picture: the authored isometric art the
///     cartridge composites from its own tiles, plus collision views where the
///     cartridge's collision decoder is available.
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

    private static readonly Level2dLayer[] ArtAndCollision =
        [Level2dLayer.Art, Level2dLayer.CollisionHeightfield];

    private readonly byte[] _rom;
    private readonly int _trueRecordOffset;
    private readonly GbaLaterLevelArt.LaterLevel? _later;
    private readonly GbaThps3LevelArt.Thps3Level? _thps3;

    private GbaLevel2dSource(
        byte[] rom, int trueRecordOffset, string displayName,
        GbaLaterLevelArt.LaterLevel? later = null,
        GbaThps3LevelArt.Thps3Level? thps3 = null)
    {
        _rom = rom;
        _trueRecordOffset = trueRecordOffset;
        DisplayName = displayName;
        _later = later;
        _thps3 = thps3;
    }

    public IReadOnlyList<Level2dLayer> Layers =>
        _thps3 != null || _later != null ? ArtAndCollision : AllLayers;

    public string DisplayName { get; }

    public Level2dRender? Render(Level2dLayer layer)
    {
        return layer switch
        {
            Level2dLayer.Art => RenderArt(),
            Level2dLayer.CollisionHeightfield => RenderCollision(),
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
        // THPS3's complete visible-art record is 0x70 bytes. Bind it against the
        // structurally located table so coincidental byte matches cannot route.
        if (record.Length == GbaThps3LevelArt.LevelRecordStride)
        {
            foreach (var level in GbaThps3LevelArt.FindLevels(rom))
            {
                var offset = level.LevelRecordOffset;
                if (offset < 0 || offset + record.Length > rom.Length) continue;
                if (!rom.AsSpan(offset, record.Length).SequenceEqual(record)) continue;
                return new GbaLevel2dSource(rom, offset, $"level{level.Index}", thps3: level);
            }

            return null;
        }

        // A later cartridge's art record is bound by identity rather than a byte
        // search: the ROM's own level list already states where each one lives.
        if (record.Length == GbaLaterLevelArt.ArtRecordStride)
        {
            foreach (var level in GbaLaterLevelArt.FindLevels(rom))
            {
                var offset = level.ArtRecordOffset;
                if (offset < 0 || offset + record.Length > rom.Length) continue;
                if (!rom.AsSpan(offset, record.Length).SequenceEqual(record)) continue;
                return new GbaLevel2dSource(
                    rom, level.LevelRecordOffset, $"level{level.Index}", level);
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
        if (_thps3 is { } thps3)
        {
            var thps3Art = GbaThps3LevelArt.RenderColourSurface(_rom, thps3);
            return thps3Art == null
                ? null
                : new Level2dRender(thps3Art.Value.Width, thps3Art.Value.Height, thps3Art.Value.Rgba);
        }

        if (_later is { } later)
        {
            var laterArt = GbaLaterLevelArt.RenderColourSurface(_rom, later);
            return laterArt == null
                ? null
                : new Level2dRender(laterArt.Value.Width, laterArt.Value.Height, laterArt.Value.Rgba);
        }

        var art = GbaLevelImages.RenderColourSurface(_rom, ScanLevel());
        return art == null ? null : new Level2dRender(art.Value.Width, art.Value.Height, art.Value.Rgba);
    }

    private Level2dRender? RenderOverlay()
    {
        // THPS3 and the later games have no decoded stored art origin yet. Their
        // collision layer remains independently useful (with THPS3's documented
        // empty-scene state), but an overlay would imply a registration we cannot
        // substantiate.
        if (_thps3 != null || _later != null)
            return null;

        var art = GbaLevelImages.RenderColourSurface(_rom, ScanLevel());
        if (art == null) return null;

        return Wrap(GbaCollisionRenderer.RenderArtOverlay(
            _rom, _trueRecordOffset, art.Value.Width, art.Value.Height, art.Value.Rgba));
    }

    private Level2dRender? RenderCollision() => Wrap(
        _thps3 is { } thps3
            ? GbaCollisionRenderer.Render(_rom, thps3)
            : _later is { } later
            ? GbaCollisionRenderer.Render(_rom, later)
            : GbaCollisionRenderer.Render(_rom, _trueRecordOffset));

    private static Level2dRender? Wrap(GbaCollisionRenderer.GbaCollisionRender? render) =>
        render == null
            ? null
            : new Level2dRender(render.Value.Width, render.Value.Height, render.Value.Rgba);
}
