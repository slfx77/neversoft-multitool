#r "../../src/NeversoftMultitool/bin/Release/net10.0/NeversoftMultitool.dll"
#nullable enable

using NeversoftMultitool.Core.Formats.Texture.Psx;

// Lists the physical PSX texture headers together with the logical texture-
// name slot selected by each header's Index field. This is useful when a mesh
// face resolves the right opaque texture identifier but the wrong physical
// image from a companion library.
//
// Usage from the repository root:
//   dotnet script --no-cache tools/diagnostics/psx_texture_slot_probe.csx -- \
//     path/to/level_l.psx [0xTEXTURE_ID]

if (Args.Count == 0)
{
    Console.Error.WriteLine("usage: psx_texture_slot_probe.csx <library.psx> [texture-id]");
    return;
}

var input = Args[0];
uint? filter = Args.Count > 1
    ? Convert.ToUInt32(Args[1].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Args[1][2..]
        : Args[1], 16)
    : null;

IEnumerable<string> paths = Directory.Exists(input)
    ? Directory.EnumerateFiles(input, "*.psx", SearchOption.AllDirectories)
    : File.Exists(input) ? [input] : Array.Empty<string>();

foreach (var path in paths)
{
    List<(PsxTextureHeader Header, uint NameHash)> textures;
    try
    {
        textures = PsxLibrary.EnumerateTextures(path);
    }
    catch
    {
        continue;
    }

    var matches = filter is { } target
        ? textures.Where(item => item.NameHash == target).ToArray()
        : textures.ToArray();
    if (matches.Length == 0)
        continue;

    Console.WriteLine($"{path}: physical textures={textures.Count}");
    foreach (var (header, nameHash) in matches)
    {
        Console.WriteLine(
            $"  header=0x{header.Offset:X8} slot={header.Index,3} " +
            $"id=0x{nameHash:X8} size={header.Width}x{header.Height} " +
            $"palette={header.PalSize} texId=0x{header.TexId:X8}");
    }
}
