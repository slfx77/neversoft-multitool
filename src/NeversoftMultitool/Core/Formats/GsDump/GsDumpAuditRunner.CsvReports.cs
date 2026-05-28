using System.Globalization;
using System.Text;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal static partial class GsDumpAuditRunner
{
    private static void SaveMaterialCsv(string path, IReadOnlyList<GsMaterialAuditRow> materials)
    {
        var csv = new StringBuilder();
        AppendCsvRow(
            csv,
            "primitive",
            "draws",
            "pixels_written",
            "missing_texture_draws",
            "bounds",
            "tex0",
            "texture_psm",
            "texture_size",
            "texture_tbp",
            "texture_tbw",
            "texture_tcc",
            "texture_tfx",
            "texture_cbp",
            "texture_cpsm",
            "texture_csm",
            "texture_csa",
            "texture_cld",
            "tex1",
            "clamp",
            "wms",
            "wmt",
            "min_u_or_mask",
            "max_u_or_fix",
            "min_v_or_mask",
            "max_v_or_fix",
            "alpha",
            "alpha_a",
            "alpha_b",
            "alpha_c",
            "alpha_d",
            "alpha_fix",
            "test",
            "ate",
            "atst",
            "aref",
            "afail",
            "zte",
            "ztst",
            "texa",
            "ta0",
            "aem",
            "ta1",
            "fog_color",
            "framebuffer",
            "fbp",
            "fbw",
            "fb_psm",
            "fb_mask",
            "zbuf",
            "zbp",
            "zpsm",
            "zmask",
            "scissor",
            "dither",
            "fba",
            "coord_mode",
            "avg_rgb",
            "min_rgb",
            "max_rgb",
            "avg_a",
            "uv_range",
            "q_range",
            "prim",
            "key");

        foreach (var material in materials)
        {
            AppendCsvRow(
                csv,
                material.Primitive,
                material.Draws,
                material.PixelsWritten,
                material.MissingTextureDraws,
                FormatBounds(material.Bounds),
                material.Tex0,
                $"0x{material.TexturePsm:X2}",
                $"{material.TextureWidth}x{material.TextureHeight}",
                material.TextureTbp,
                material.TextureTbw,
                material.TextureTcc,
                material.TextureTfx,
                material.TextureCbp,
                $"0x{material.TextureCpsm:X2}",
                material.TextureCsm,
                material.TextureCsa,
                material.TextureCld,
                material.Tex1,
                material.Clamp,
                material.ClampWms,
                material.ClampWmt,
                material.ClampMinUOrMask,
                material.ClampMaxUOrFix,
                material.ClampMinVOrMask,
                material.ClampMaxVOrFix,
                material.Alpha,
                material.AlphaA,
                material.AlphaB,
                material.AlphaC,
                material.AlphaD,
                material.AlphaFix,
                material.Test,
                material.AlphaTestEnabled,
                material.AlphaTestMethod,
                material.AlphaRef,
                material.AlphaFailMode,
                material.DepthTestEnabled,
                material.DepthTestMethod,
                material.Texa,
                material.TexaTa0,
                material.TexaAem,
                material.TexaTa1,
                material.FogColor,
                material.FramebufferKey,
                material.FramebufferFbp,
                material.FramebufferFbw,
                $"0x{material.FramebufferPsm:X2}",
                $"0x{material.FramebufferMask:X8}",
                material.Zbuf,
                material.Zbp,
                $"0x{material.Zpsm:X2}",
                material.Zmask,
                $"{material.ScissorX0},{material.ScissorY0}-{material.ScissorX1},{material.ScissorY1}",
                material.DitherEnabled,
                material.FramebufferAlphaWriteEnabled,
                material.FixedTextureCoordinates ? "UV/FST" : "STQ",
                $"{FormatDouble(material.AvgR)}/{FormatDouble(material.AvgG)}/{FormatDouble(material.AvgB)}",
                $"{FormatDouble(material.MinR)}/{FormatDouble(material.MinG)}/{FormatDouble(material.MinB)}",
                $"{FormatDouble(material.MaxR)}/{FormatDouble(material.MaxG)}/{FormatDouble(material.MaxB)}",
                FormatDouble(material.AvgA),
                $"{FormatDouble(material.MinU)},{FormatDouble(material.MinV)}-{FormatDouble(material.MaxU)},{FormatDouble(material.MaxV)}",
                $"{FormatDouble(material.MinQ)}-{FormatDouble(material.MaxQ)}",
                material.Prim,
                material.Key);
        }

        File.WriteAllText(path, csv.ToString());
    }

    private static void AppendCsvRow(StringBuilder csv, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i != 0)
                csv.Append(',');
            csv.Append(CsvValue(values[i]));
        }

        csv.AppendLine();
    }

    private static string CsvValue(object? value)
    {
        var text = value switch
        {
            null => "",
            bool b => b ? "1" : "0",
            double d => FormatDouble(d),
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };

        if (!text.Contains(',') && !text.Contains('"') && !text.Contains('\n') && !text.Contains('\r'))
            return text;

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatBounds(GsPixelBounds? bounds)
    {
        return bounds == null
            ? ""
            : $"{bounds.X},{bounds.Y},{bounds.Width}x{bounds.Height}";
    }
}
