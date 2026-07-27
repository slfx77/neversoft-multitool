// XbxPassSurvey — census of material passes across every Xbox/PC/GC scene corpus.
//
// Phase-1 Step 0 of the THAW-fidelity stream: quantifies the blast radius of the
// additive/subtractive bake + multi-pass compositing fixes BEFORE the converter
// changes land, confirms the ped_boone_full tattoo case (material 0x71894AA9),
// and dumps the two reader-B fields under investigation as the THAW PC
// FixedAlpha source (XbxPass.ColorW = m_color[pass][3], and BlendModeExtra).
//
// Usage: XbxPassSurvey [buildsRoot] [-o outputDir]
//   buildsRoot default: Sample/Builds  ·  outputDir default: TestOutput/xbx_pass_survey

using System.Globalization;
using System.Text;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

var buildsRoot = "Sample/Builds";
var outputDir = "TestOutput/xbx_pass_survey";
for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "-o" or "--output" && i + 1 < args.Length)
        outputDir = args[++i];
    else if (!args[i].StartsWith('-'))
        buildsRoot = args[i];
}

if (!Directory.Exists(buildsRoot))
{
    Console.Error.WriteLine($"Builds root not found: {buildsRoot}");
    return 1;
}

// The corpora served by XbxGeometryWriter (PS2 uses a different writer).
string[] buildGlobs =
[
    "*Underground 2*Xbox*",
    "*Underground 2*Windows*",
    "*American Wasteland*PC*",
    "*American Wasteland*GC*"
];

// Suffixed scene extensions are unambiguous; bare .skin/.mdl (THAW PC pak
// extractions) are included only when the THAW header check passes.
string[] suffixed = [".skin.xbx", ".mdl.xbx", ".scn.xbx", ".skin.wpc", ".mdl.wpc", ".skin.ngc", ".mdl.ngc"];
string[] bare = [".skin", ".mdl"];

Directory.CreateDirectory(outputDir);
var csvPath = Path.Combine(outputDir, "passes.csv");
using var csv = new StreamWriter(csvPath, false, new UTF8Encoding(false));
csv.WriteLine("build,file,reader,matChecksum,matNameChecksum,numPasses,alphaCutoff,sorted,drawOrder," +
              "passIndex,blendMode,blendModeExtra,flags,hasColor,colorR,colorG,colorB,colorW," +
              "fixedAlpha,texChecksum,uAddr,vAddr");

var stats = new SurveyStats();
var boneReport = new StringBuilder();

foreach (var glob in buildGlobs)
{
    foreach (var buildDir in Directory.GetDirectories(buildsRoot, glob))
    {
        var buildName = Path.GetFileName(buildDir);
        Console.WriteLine($"== {buildName}");

        foreach (var file in EnumerateSceneFiles(buildDir))
        {
            var rel = Path.GetRelativePath(buildDir, file);
            byte[] data;
            try
            {
                data = File.ReadAllBytes(file);
            }
            catch (IOException)
            {
                continue;
            }

            var isNgc = file.EndsWith(".ngc", StringComparison.OrdinalIgnoreCase);
            var isBare = bare.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            string reader;
            XbxScene scene;
            try
            {
                if (isNgc)
                {
                    reader = "ngc";
                    scene = NgcSceneFile.Parse(data);
                }
                else if (ThawSceneFile.IsThawScene(data))
                {
                    reader = "thaw";
                    scene = ThawSceneFile.Parse(data);
                }
                else if (!isBare && XbxSceneFile.IsXbxScene(data))
                {
                    reader = "xbx";
                    scene = XbxSceneFile.Parse(data);
                }
                else
                {
                    stats.Skipped++;
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                stats.Failures++;
                if (stats.Failures <= 20)
                    Console.WriteLine($"   parse fail [{rel}]: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            stats.Files++;
            foreach (var mat in scene.Materials)
            {
                stats.Materials++;
                if (mat.Passes.Length > 1) stats.MultiPassMaterials++;

                for (var p = 0; p < mat.Passes.Length; p++)
                {
                    var pass = mat.Passes[p];
                    stats.CountPass(reader, p, pass);

                    csv.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{buildName.Replace(',', ';')},{rel.Replace(',', ';')},{reader},{mat.Checksum:X8},{mat.NameChecksum:X8}," +
                        $"{mat.Passes.Length},{mat.AlphaCutoff},{mat.Sorted},{mat.DrawOrder}," +
                        $"{p},{pass.BlendMode},{pass.BlendModeExtra},{pass.Flags:X8},{pass.HasColor}," +
                        $"{pass.Color.X},{pass.Color.Y},{pass.Color.Z},{pass.ColorW}," +
                        $"{pass.FixedAlpha},{pass.TextureChecksum:X8},{pass.UAddressing},{pass.VAddressing}"));
                }

                if (mat.Checksum == 0x71894AA9)
                {
                    boneReport.AppendLine(CultureInfo.InvariantCulture, $"MATERIAL 0x71894AA9 in {buildName}/{rel}:");
                    for (var p = 0; p < mat.Passes.Length; p++)
                    {
                        var pass = mat.Passes[p];
                        boneReport.AppendLine(CultureInfo.InvariantCulture,
                            $"  pass {p}: blendMode={pass.BlendMode} extra={pass.BlendModeExtra} " +
                            $"flags=0x{pass.Flags:X8} tex=0x{pass.TextureChecksum:X8} " +
                            $"color=({pass.Color.X:F3},{pass.Color.Y:F3},{pass.Color.Z:F3}) " +
                            $"colorW={pass.ColorW:F5} fixedAlpha={pass.FixedAlpha}");
                    }
                }
            }
        }
    }
}

var summary = stats.BuildSummary(boneReport.ToString());
Console.WriteLine();
Console.WriteLine(summary);
File.WriteAllText(Path.Combine(outputDir, "summary.txt"), summary);
Console.WriteLine($"CSV: {csvPath}");
return 0;

IEnumerable<string> EnumerateSceneFiles(string buildDir)
{
    foreach (var file in Directory.EnumerateFiles(buildDir, "*", SearchOption.AllDirectories))
    {
        if (suffixed.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            yield return file;
        }
        else if (bare.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            // Bare .skin/.mdl also exists as PS2/other data in pak trees; the
            // THAW header check in the main loop gates those out.
            yield return file;
        }
    }
}

internal sealed class SurveyStats
{
    private readonly Dictionary<string, Dictionary<uint, int>> _pass0Modes = [];
    private readonly Dictionary<string, Dictionary<uint, int>> _passKModes = [];
    private readonly Dictionary<string, int> _eligibleOverlays = [];
    private readonly Dictionary<string, int> _bakeCandidates = [];
    private readonly Dictionary<string, List<(uint Mode, float ColorW, short Extra, uint FixedAlpha)>> _fixedSamples = [];

    public int Files;
    public int Failures;
    public int Skipped;
    public int Materials;
    public int MultiPassMaterials;

    // Flags that disqualify a pass-k overlay from static compositing.
    private const uint SkipFlags = (1u << 0) /* UvWibble */ | (1u << 3) /* Environment */ |
                                   (1u << 11) /* PassTextureAnimates */ | (1u << 27) /* WaterEffect */;

    public void CountPass(string reader, int passIndex, XbxPass pass)
    {
        var modes = passIndex == 0 ? _pass0Modes : _passKModes;
        if (!modes.TryGetValue(reader, out var hist))
            modes[reader] = hist = [];
        hist[pass.BlendMode] = hist.GetValueOrDefault(pass.BlendMode) + 1;

        if (passIndex == 0 && pass.BlendMode is >= 1 and <= 4)
            _bakeCandidates[reader] = _bakeCandidates.GetValueOrDefault(reader) + 1;

        if (passIndex > 0 && pass.TextureChecksum != 0 &&
            pass.BlendMode is >= 1 and <= 6 && (pass.Flags & SkipFlags) == 0)
        {
            _eligibleOverlays[reader] = _eligibleOverlays.GetValueOrDefault(reader) + 1;
        }

        // FixedAlpha investigation: sample rows for the *_FIXED modes plus a
        // spread of non-fixed modes for contrast.
        if (pass.BlendMode is 2 or 4 or 6 ||
            (pass.BlendMode is 1 or 3 or 5 && (_fixedSamples.GetValueOrDefault(reader)?.Count ?? 0) < 4000))
        {
            if (!_fixedSamples.TryGetValue(reader, out var list))
                _fixedSamples[reader] = list = [];
            if (list.Count < 20000)
                list.Add((pass.BlendMode, pass.ColorW, pass.BlendModeExtra, pass.FixedAlpha));
        }
    }

    public string BuildSummary(string booneReport)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"files={Files} parseFailures={Failures} skipped={Skipped} " +
                      $"materials={Materials} multiPass={MultiPassMaterials}");
        AppendHistogram(sb, "pass0 blend modes", _pass0Modes);
        AppendHistogram(sb, "pass-k (k>=1) blend modes", _passKModes);
        foreach (var (reader, n) in _bakeCandidates.OrderBy(kv => kv.Key))
            sb.AppendLine(CultureInfo.InvariantCulture, $"bake candidates (pass0 mode 1-4) [{reader}]: {n}");
        foreach (var (reader, n) in _eligibleOverlays.OrderBy(kv => kv.Key))
            sb.AppendLine(CultureInfo.InvariantCulture, $"eligible overlays (pass-k tex!=0, mode 1-6, no skip flags) [{reader}]: {n}");

        sb.AppendLine();
        sb.AppendLine("== FixedAlpha investigation ==");
        foreach (var (reader, samples) in _fixedSamples.OrderBy(kv => kv.Key))
        {
            foreach (var modeGroup in samples.GroupBy(s => s.Mode).OrderBy(g => g.Key))
            {
                var colorWs = modeGroup.Select(s => s.ColorW).ToArray();
                var extras = modeGroup.Select(s => (int)s.Extra).ToArray();
                var fixeds = modeGroup.Select(s => s.FixedAlpha).ToArray();
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"[{reader}] mode {modeGroup.Key}: n={modeGroup.Count()}  " +
                    $"colorW min/max/avg={colorWs.Min():F4}/{colorWs.Max():F4}/{colorWs.Average():F4}  " +
                    $"extra nonzero={extras.Count(e => e != 0)} (min={extras.Min()} max={extras.Max()})  " +
                    $"fixedAlpha nonzero={fixeds.Count(f => f != 0)} (max={fixeds.Max()})"));
            }
        }

        if (booneReport.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("== ped_boone_full material ==");
            sb.Append(booneReport);
        }

        return sb.ToString();
    }

    private static void AppendHistogram(
        StringBuilder sb, string title, Dictionary<string, Dictionary<uint, int>> data)
    {
        foreach (var (reader, hist) in data.OrderBy(kv => kv.Key))
        {
            var entries = string.Join("  ", hist.OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}:{kv.Value}"));
            sb.AppendLine(CultureInfo.InvariantCulture, $"{title} [{reader}]: {entries}");
        }
    }
}
