namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawZoneTexMdlSupport
{
    public static List<ThawZoneTexFile.MdlGsTextureState> ExtractTextureStatesFromMdl(byte[] mdlData)
    {
        var textureStates = new List<ThawZoneTexFile.MdlGsTextureState>();
        if (!Ps2GeomFile.IsPakMdl(mdlData))
            return textureStates;

        var scene = Ps2GeomFile.ParsePakMdl(mdlData);
        var seen = new HashSet<ThawZoneTexFile.MdlGsTextureState>();
        foreach (var leaf in scene.Leaves)
        {
            if (leaf.DmaTex0 == 0)
                continue;

            var state = new ThawZoneTexFile.MdlGsTextureState(
                leaf.DmaTex0,
                leaf.DmaTex1,
                leaf.DmaMipTbp1,
                leaf.DmaMipTbp2);
            if (seen.Add(state))
                textureStates.Add(state);
        }

        return textureStates;
    }

    public static HashSet<ulong> ExtractTex0ValuesFromMdl(byte[] mdlData)
    {
        var tex0Values = new HashSet<ulong>();
        foreach (var state in ExtractTextureStatesFromMdl(mdlData))
            tex0Values.Add(state.Tex0);

        return tex0Values;
    }
}
