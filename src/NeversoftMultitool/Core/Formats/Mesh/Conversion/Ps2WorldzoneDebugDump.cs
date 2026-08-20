using System.Globalization;
using System.Text;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using QbKeyTable = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Writes the worldzone diagnostics gathered by a
///     <see cref="Ps2GeomDebugCollector" /> — every parser/writer decline with
///     its reason (<c>{stem}.rejections.csv</c>) and every emitted leaf's GS
///     state, classification, and texture-resolution tag
///     (<c>{stem}.materials.csv</c>) — plus the texture catalog's own debug
///     dump. Pure diagnostics: nothing here feeds back into conversion.
/// </summary>
internal static class Ps2WorldzoneDebugDump
{
    public static void Write(
        string directory,
        string stem,
        Ps2GeomDebugCollector collector,
        ZoneTextureCatalog? textureCatalog)
    {
        Directory.CreateDirectory(directory);
        WriteRejections(Path.Combine(directory, $"{stem}.rejections.csv"), collector);
        WriteMaterials(Path.Combine(directory, $"{stem}.materials.csv"), collector);
        textureCatalog?.WriteDebugDump(Path.Combine(directory, "texture_catalog"));
    }

    /// <summary>One-line console summary: rejection counts grouped by stage/reason.</summary>
    public static string Summarize(Ps2GeomDebugCollector collector)
    {
        if (collector.LeafRejections.Count == 0)
            return $"{collector.Materials.Count} leaves emitted, 0 rejections";

        var reasons = collector.LeafRejections
            .GroupBy(static rejection => $"{rejection.Stage}/{rejection.Reason}")
            .OrderByDescending(static group => group.Count())
            .Select(static group => $"{group.Key}: {group.Count()}");
        return $"{collector.Materials.Count} leaves emitted, " +
               $"{collector.LeafRejections.Count} rejections ({string.Join(", ", reasons)})";
    }

    private static void WriteRejections(string path, Ps2GeomDebugCollector collector)
    {
        var sb = new StringBuilder();
        sb.AppendLine("mdl,stage,reason,leafIndex,vertexCount,tex0,minX,minY,minZ,maxX,maxY,maxZ");
        foreach (var rejection in collector.LeafRejections)
        {
            sb.AppendLine(string.Join(',',
                rejection.MdlName,
                rejection.Stage,
                rejection.Reason,
                Invariant(rejection.LeafIndex),
                Invariant(rejection.VertexCount),
                rejection.Tex0.ToString("X16", CultureInfo.InvariantCulture),
                Invariant(rejection.Min.X), Invariant(rejection.Min.Y), Invariant(rejection.Min.Z),
                Invariant(rejection.Max.X), Invariant(rejection.Max.Y), Invariant(rejection.Max.Z)));
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteMaterials(string path, Ps2GeomDebugCollector collector)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "mdl,leafIndex,space,drawIndex,passIndex,overlapGroup,isBillboard," +
            "leafChecksum,leafName,leafFlags," +
            "nodeCreatedAtStart,nodeTodGroup,nodeVariable," +
            "alpha1,alphaA,alphaB,alphaC,alphaD,alphaFix," +
            "test1,ate,atst,aref,afail," +
            "fbmskAlphaByte,alphaMode,renderLayer,renderOrderKey,groupChecksum," +
            "tex0,textureChecksum,syntheticDestAlpha,textureResolved," +
            "resolveMode,sourceLabel,entryLabel,bakeClass,vertexCount," +
            "minX,minY,minZ,maxX,maxY,maxZ");
        foreach (var record in collector.Materials)
        {
            var alphaBlend = (byte)(record.Alpha1 & 0xFF);
            sb.AppendLine(string.Join(',',
                record.MdlName,
                Invariant(record.LeafIndex),
                record.Space,
                Invariant(record.DrawIndex),
                Invariant(record.PassIndex),
                Invariant(record.OverlapGroup),
                record.IsBillboard ? "1" : "0",
                record.LeafChecksum.ToString("X8", CultureInfo.InvariantCulture),
                Csv(QbKeyTable.TryResolve(record.LeafChecksum) ?? ""),
                record.LeafFlags.ToString("X8", CultureInfo.InvariantCulture),
                record.NodeCreatedAtStart ? "1" : "0",
                Csv(record.NodeTodGroup == 0
                    ? ""
                    : QbKeyTable.TryResolve(record.NodeTodGroup) ?? record.NodeTodGroup.ToString("X8", CultureInfo.InvariantCulture)),
                Csv(record.NodeVariable == 0
                    ? ""
                    : QbKeyTable.TryResolve(record.NodeVariable) ?? record.NodeVariable.ToString("X8", CultureInfo.InvariantCulture)),
                record.Alpha1.ToString("X16", CultureInfo.InvariantCulture),
                Invariant(alphaBlend & 0x03),
                Invariant((alphaBlend >> 2) & 0x03),
                Invariant((alphaBlend >> 4) & 0x03),
                Invariant((alphaBlend >> 6) & 0x03),
                Invariant((int)((record.Alpha1 >> 32) & 0xFF)),
                record.Test1.ToString("X16", CultureInfo.InvariantCulture),
                Invariant((int)(record.Test1 & 0x1)),
                Invariant((int)((record.Test1 >> 1) & 0x7)),
                Invariant((int)((record.Test1 >> 4) & 0xFF)),
                Invariant((int)((record.Test1 >> 12) & 0x3)),
                Invariant((int)((record.Frame1 >> 56) & 0xFF)),
                record.AlphaMode,
                record.RenderLayer,
                Invariant(record.RenderOrderKey),
                record.GroupChecksum.ToString("X8", CultureInfo.InvariantCulture),
                record.Tex0.ToString("X16", CultureInfo.InvariantCulture),
                record.TextureChecksum.ToString("X8", CultureInfo.InvariantCulture),
                record.UsedSyntheticDestinationAlpha ? "1" : "0",
                record.TextureResolved ? "1" : "0",
                Csv(record.ResolveMode),
                Csv(record.SourceLabel),
                Csv(record.EntryLabel),
                record.BakeClass,
                Invariant(record.VertexCount),
                Invariant(record.Min.X), Invariant(record.Min.Y), Invariant(record.Min.Z),
                Invariant(record.Max.X), Invariant(record.Max.Y), Invariant(record.Max.Z)));
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static string Invariant(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(uint value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Csv(string value) =>
        value.Contains(',', StringComparison.Ordinal)
            ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : value;
}
