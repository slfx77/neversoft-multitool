using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh;
using Pfim;

// -----------------------------------------------------------------------
// BlendModeDiag: Diagnostic tool to analyze DDM material BlendMode values
// and DDX texture alpha usage for THPS2X level files.
// -----------------------------------------------------------------------

var basePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    @"source\repos\NeversoftMultitool\Sample\Builds",
    @"Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)");

var ddmDir = Path.Combine(basePath, "DDM");
var ddxDir = Path.Combine(basePath, "DDX");

if (!Directory.Exists(ddmDir))
{
    Console.Error.WriteLine($"DDM directory not found: {ddmDir}");
    return 1;
}

Console.WriteLine("=== DDM BlendMode & DDX Alpha Diagnostic ===");
Console.WriteLine($"DDM dir: {ddmDir}");
Console.WriteLine($"DDX dir: {ddxDir}");
Console.WriteLine();

// -----------------------------------------------------------------------
// 1. Parse all DDM files, collect material info
// -----------------------------------------------------------------------

var ddmFiles = Directory.GetFiles(ddmDir, "*.DDM");
Console.WriteLine($"Found {ddmFiles.Length} DDM files");
Console.WriteLine();

// Track all materials globally
var allMaterials = new List<(string DdmFile, string ObjectName, string MatName, string TexName, uint BlendMode, byte DiffR, byte DiffG, byte DiffB, byte DiffA, float Emissive, float SpecLevel, float Gloss)>();

foreach (var ddmPath in ddmFiles.OrderBy(f => f))
{
    try
    {
        var ddm = DdmFile.Parse(ddmPath);
        var ddmName = Path.GetFileNameWithoutExtension(ddmPath);

        foreach (var obj in ddm.Objects)
        {
            foreach (var mat in obj.Materials)
            {
                allMaterials.Add((
                    ddmName, obj.Name, mat.Name, mat.TextureName,
                    mat.BlendMode, mat.DiffuseR, mat.DiffuseG, mat.DiffuseB, mat.DiffuseA,
                    mat.Emissive, mat.SpecularLevel, mat.Glossiness));
            }
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR parsing {ddmPath}: {ex.Message}");
    }
}

Console.WriteLine($"Total materials across all DDM files: {allMaterials.Count}");
Console.WriteLine();

// -----------------------------------------------------------------------
// 2. BlendMode value distribution
// -----------------------------------------------------------------------

Console.WriteLine("=== BLEND MODE DISTRIBUTION ===");
Console.WriteLine();

var blendGroups = allMaterials.GroupBy(m => m.BlendMode).OrderBy(g => g.Key);
foreach (var group in blendGroups)
{
    Console.WriteLine($"BlendMode = {group.Key} (0x{group.Key:X8}): {group.Count()} materials");

    // Show some samples
    var samples = group.Take(5).ToList();
    foreach (var s in samples)
    {
        Console.WriteLine($"  [{s.DdmFile}] obj={s.ObjectName} mat={s.MatName} tex={s.TexName} " +
                          $"diffuse=({s.DiffR},{s.DiffG},{s.DiffB},{s.DiffA}) emissive={s.Emissive:F2} specLevel={s.SpecLevel:F2} gloss={s.Gloss:F2}");
    }
    if (group.Count() > 5)
        Console.WriteLine($"  ... and {group.Count() - 5} more");
    Console.WriteLine();
}

// -----------------------------------------------------------------------
// 3. DiffuseA distribution (separate from BlendMode)
// -----------------------------------------------------------------------

Console.WriteLine("=== DIFFUSE ALPHA DISTRIBUTION ===");
Console.WriteLine();

var diffuseAGroups = allMaterials.GroupBy(m => m.DiffA).OrderBy(g => g.Key);
foreach (var group in diffuseAGroups)
{
    Console.WriteLine($"DiffuseA = {group.Key}: {group.Count()} materials");
    var samples = group.Take(3).ToList();
    foreach (var s in samples)
    {
        Console.WriteLine($"  [{s.DdmFile}] mat={s.MatName} tex={s.TexName} blend={s.BlendMode}");
    }
    if (group.Count() > 3)
        Console.WriteLine($"  ... and {group.Count() - 3} more");
    Console.WriteLine();
}

// -----------------------------------------------------------------------
// 4. Identify potential grayscale/light-map textures
//    (materials with names hinting at light: "light", "lm_", "shadow", etc.
//     OR materials with low diffuse RGB that could be light overlays)
// -----------------------------------------------------------------------

Console.WriteLine("=== POTENTIAL LIGHT/OVERLAY MATERIALS ===");
Console.WriteLine("(Materials where name contains 'light', 'lm_', 'shadow', 'glow', 'decal', 'fade')");
Console.WriteLine();

var lightKeywords = new[] { "light", "lm_", "shadow", "glow", "decal", "fade", "glass", "window", "trans", "alpha", "blend" };
var lightMaterials = allMaterials.Where(m =>
{
    var nameLower = (m.MatName + " " + m.TexName).ToLowerInvariant();
    return lightKeywords.Any(kw => nameLower.Contains(kw));
}).ToList();

if (lightMaterials.Count > 0)
{
    foreach (var m in lightMaterials.OrderBy(m => m.BlendMode).ThenBy(m => m.MatName))
    {
        Console.WriteLine($"  blend={m.BlendMode} [{m.DdmFile}] mat={m.MatName} tex={m.TexName} " +
                          $"diffuse=({m.DiffR},{m.DiffG},{m.DiffB},{m.DiffA}) emissive={m.Emissive:F2}");
    }
}
else
{
    Console.WriteLine("  None found by name.");
}
Console.WriteLine();

// -----------------------------------------------------------------------
// 5. Look for materials with very dark vertex diffuse (potential light maps)
// -----------------------------------------------------------------------

Console.WriteLine("=== MATERIALS WITH LOW DIFFUSE RGB (potential dark overlays) ===");
Console.WriteLine("(DiffuseR+G+B < 100, i.e. very dark base color)");
Console.WriteLine();

var darkMaterials = allMaterials.Where(m => (m.DiffR + m.DiffG + m.DiffB) < 100 && m.DiffA > 0).ToList();
foreach (var m in darkMaterials.OrderBy(m => m.BlendMode).Take(30))
{
    Console.WriteLine($"  blend={m.BlendMode} [{m.DdmFile}] mat={m.MatName} tex={m.TexName} " +
                      $"diffuse=({m.DiffR},{m.DiffG},{m.DiffB},{m.DiffA}) emissive={m.Emissive:F2}");
}
if (darkMaterials.Count > 30)
    Console.WriteLine($"  ... and {darkMaterials.Count - 30} more");
Console.WriteLine();

// -----------------------------------------------------------------------
// 6. Cross-reference BlendMode with DiffuseA
// -----------------------------------------------------------------------

Console.WriteLine("=== BLEND MODE x DIFFUSE ALPHA CROSS-TAB ===");
Console.WriteLine();

var crossTab = allMaterials
    .GroupBy(m => (m.BlendMode, m.DiffA))
    .OrderBy(g => g.Key.BlendMode)
    .ThenBy(g => g.Key.DiffA);

foreach (var group in crossTab)
{
    Console.WriteLine($"  BlendMode={group.Key.BlendMode}, DiffuseA={group.Key.DiffA}: {group.Count()} materials");
}
Console.WriteLine();

// -----------------------------------------------------------------------
// 7. DDX texture alpha analysis
// -----------------------------------------------------------------------

Console.WriteLine("=== DDX TEXTURE ALPHA ANALYSIS ===");
Console.WriteLine();

if (!Directory.Exists(ddxDir))
{
    Console.WriteLine("DDX directory not found, skipping alpha analysis.");
}
else
{
    var ddxFiles = Directory.GetFiles(ddxDir, "*.DDX");
    Console.WriteLine($"Found {ddxFiles.Length} DDX archives");
    Console.WriteLine();

    // Collect unique texture names referenced by materials
    var referencedTextures = allMaterials
        .Select(m => m.TexName)
        .Where(t => !t.Equals("No_Texture_Map", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    Console.WriteLine($"Unique texture names referenced by materials: {referencedTextures.Count}");

    // Load all DDX archives and check alpha
    var textureAlphaResults = new Dictionary<string, (string DdxSource, string DdsFormat, int Width, int Height, bool HasAnyTransparent, int TransparentPixels, int TotalPixels, int MinAlpha)>(StringComparer.OrdinalIgnoreCase);

    foreach (var ddxPath in ddxFiles.OrderBy(f => f))
    {
        var ddxName = Path.GetFileNameWithoutExtension(ddxPath);
        try
        {
            var entries = DdxArchive.ReadAllEntries(ddxPath);
            foreach (var (texName, ddsBytes) in entries)
            {
                if (textureAlphaResults.ContainsKey(texName))
                    continue; // already analyzed from another DDX

                try
                {
                    using var ms = new MemoryStream(ddsBytes);
                    using var image = Pfimage.FromStream(ms);

                    var hasAlphaChannel = image.Format == ImageFormat.Rgba32;
                    var transparentPixels = 0;
                    var totalPixels = image.Width * image.Height;
                    var minAlpha = 255;
                    var formatName = image.Format.ToString();

                    if (hasAlphaChannel)
                    {
                        // Scan alpha channel (BGRA32 layout: B,G,R,A)
                        var stride = image.Stride;
                        for (var y = 0; y < image.Height; y++)
                        {
                            for (var x = 0; x < image.Width; x++)
                            {
                                var offset = y * stride + x * 4 + 3; // alpha byte
                                if (offset < image.Data.Length)
                                {
                                    var alpha = image.Data[offset];
                                    if (alpha < 255)
                                        transparentPixels++;
                                    if (alpha < minAlpha)
                                        minAlpha = alpha;
                                }
                            }
                        }
                    }

                    textureAlphaResults[texName] = (ddxName, formatName, image.Width, image.Height,
                        transparentPixels > 0, transparentPixels, totalPixels, minAlpha);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  ERROR decoding {texName} from {ddxName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR reading DDX {ddxPath}: {ex.Message}");
        }
    }

    Console.WriteLine($"Total unique textures decoded: {textureAlphaResults.Count}");
    Console.WriteLine();

    // Summarize alpha findings
    var withAlpha = textureAlphaResults.Values.Where(v => v.HasAnyTransparent).ToList();
    var allOpaque = textureAlphaResults.Values.Where(v => !v.HasAnyTransparent).ToList();
    var rgba32Count = textureAlphaResults.Values.Count(v => v.DdsFormat == "Rgba32");
    var rgb24Count = textureAlphaResults.Values.Count(v => v.DdsFormat == "Rgb24");

    Console.WriteLine($"  Format: Rgba32={rgba32Count}, Rgb24={rgb24Count}");
    Console.WriteLine($"  Textures with actual transparent pixels (alpha < 255): {withAlpha.Count}");
    Console.WriteLine($"  Textures that are fully opaque (all alpha == 255 or RGB24): {allOpaque.Count + rgb24Count}");
    Console.WriteLine();

    // Show textures that actually have transparency, grouped by min alpha
    Console.WriteLine("--- Textures WITH actual transparency (alpha < 255) ---");
    Console.WriteLine();

    foreach (var t in withAlpha.OrderBy(v => v.MinAlpha))
    {
        var pct = 100.0 * t.TransparentPixels / t.TotalPixels;
        var texName = textureAlphaResults.First(kvp => kvp.Value == t).Key;

        // Check if this texture is referenced by any material, and what BlendMode
        var refMats = allMaterials.Where(m => m.TexName.Equals(texName, StringComparison.OrdinalIgnoreCase)).ToList();
        var blendModes = refMats.Select(m => m.BlendMode).Distinct().OrderBy(b => b).ToList();
        var blendStr = blendModes.Count > 0 ? string.Join(",", blendModes) : "NOT_REFERENCED";

        Console.WriteLine($"  {texName}: {t.DdsFormat} {t.Width}x{t.Height}, minAlpha={t.MinAlpha}, " +
                          $"transparent={t.TransparentPixels}/{t.TotalPixels} ({pct:F1}%), " +
                          $"blendModes=[{blendStr}]");
    }
    Console.WriteLine();

    // Check for materials with BlendMode > 0 whose textures are actually opaque
    Console.WriteLine("--- Materials with BlendMode > 0 but texture has NO transparent pixels ---");
    Console.WriteLine();

    var blendButOpaque = allMaterials
        .Where(m => m.BlendMode > 0 &&
                    !m.TexName.Equals("No_Texture_Map", StringComparison.OrdinalIgnoreCase))
        .Where(m =>
        {
            if (textureAlphaResults.TryGetValue(m.TexName, out var info))
                return !info.HasAnyTransparent;
            return false;
        })
        .ToList();

    foreach (var m in blendButOpaque.Take(20))
    {
        Console.WriteLine($"  blend={m.BlendMode} [{m.DdmFile}] mat={m.MatName} tex={m.TexName} " +
                          $"diffuse=({m.DiffR},{m.DiffG},{m.DiffB},{m.DiffA})");
    }
    if (blendButOpaque.Count > 20)
        Console.WriteLine($"  ... and {blendButOpaque.Count - 20} more");
    Console.WriteLine();

    // Also show materials with BlendMode=0 but texture HAS transparency
    Console.WriteLine("--- Materials with BlendMode = 0 but texture HAS transparent pixels ---");
    Console.WriteLine();

    var opaqueButTransparent = allMaterials
        .Where(m => m.BlendMode == 0 &&
                    !m.TexName.Equals("No_Texture_Map", StringComparison.OrdinalIgnoreCase))
        .Where(m =>
        {
            if (textureAlphaResults.TryGetValue(m.TexName, out var info))
                return info.HasAnyTransparent;
            return false;
        })
        .ToList();

    foreach (var m in opaqueButTransparent.Take(20))
    {
        textureAlphaResults.TryGetValue(m.TexName, out var info);
        Console.WriteLine($"  blend={m.BlendMode} [{m.DdmFile}] mat={m.MatName} tex={m.TexName} " +
                          $"diffuse=({m.DiffR},{m.DiffG},{m.DiffB},{m.DiffA}) " +
                          $"texMinAlpha={info.MinAlpha} texTransPx={info.TransparentPixels}");
    }
    if (opaqueButTransparent.Count > 20)
        Console.WriteLine($"  ... and {opaqueButTransparent.Count - 20} more");
    Console.WriteLine();
}

// -----------------------------------------------------------------------
// 8. Materials with grayscale textures (check if diffuse RGB is equal — grayscale hint)
// -----------------------------------------------------------------------

Console.WriteLine("=== MATERIALS WITH EQUAL R=G=B DIFFUSE (potential grayscale/light overlays) ===");
Console.WriteLine();

var grayscaleDiffuse = allMaterials.Where(m => m.DiffR == m.DiffG && m.DiffG == m.DiffB && m.DiffR < 200).ToList();
var grayByBlend = grayscaleDiffuse.GroupBy(m => m.BlendMode).OrderBy(g => g.Key);
foreach (var group in grayByBlend)
{
    Console.WriteLine($"  BlendMode={group.Key}: {group.Count()} materials with gray diffuse");
    foreach (var m in group.Take(5))
    {
        Console.WriteLine($"    [{m.DdmFile}] mat={m.MatName} tex={m.TexName} diffuse=({m.DiffR},{m.DiffG},{m.DiffB},{m.DiffA})");
    }
    if (group.Count() > 5)
        Console.WriteLine($"    ... and {group.Count() - 5} more");
}
Console.WriteLine();

Console.WriteLine("=== DONE ===");
return 0;
