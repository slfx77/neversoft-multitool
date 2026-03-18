using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawReplayKickMeshBuilder
{
    public static GsVertexEvent[] BuildKickEvents(
        IReadOnlyList<int> outputWindow,
        IReadOnlyDictionary<int, ReplayOutputSlot> kickBuffer,
        IReadOnlyDictionary<int, ReplayOutputSlot> currentBatchSlots)
    {
        var events = new GsVertexEvent[outputWindow.Count];
        for (var i = 0; i < outputWindow.Count; i++)
        {
            var address = outputWindow[i];
            if (kickBuffer.TryGetValue(address, out var source))
            {
                events[i] = new GsVertexEvent
                {
                    OutputIndex = i,
                    FullOutputAddress = address,
                    OutputAddress = (byte)address,
                    Kind = GsVertexEventKind.Vertex,
                    VertexSource = source.Source,
                    IsNoKick = source.IsNoKick,
                    IsBufferedCarry = !currentBatchSlots.ContainsKey(address)
                };
            }
            else
            {
                events[i] = new GsVertexEvent
                {
                    OutputIndex = i,
                    FullOutputAddress = address,
                    OutputAddress = (byte)address,
                    Kind = GsVertexEventKind.Gap,
                    VertexSource = null,
                    IsNoKick = false,
                    IsBufferedCarry = false
                };
            }
        }

        return events;
    }

    public static List<Ps2Mesh> BuildMeshesFromEvents(
        IReadOnlyList<GsVertexEvent> events,
        uint materialChecksum,
        bool hasColors)
    {
        var subStrips = new List<(Ps2Vertex[] Vertices, bool StartsOnOddOutputSlot)>();
        var currentStrip = new List<Ps2Vertex>();
        var stripStartIndex = -1;

        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            if (evt.Kind != GsVertexEventKind.Vertex || evt.VertexSource is not ReplayVertexSource source)
            {
                AddSubStrip(subStrips, currentStrip, stripStartIndex);
                currentStrip.Clear();
                stripStartIndex = -1;
                continue;
            }

            if (currentStrip.Count == 0)
                stripStartIndex = i;

            currentStrip.Add(ToPs2Vertex(source, hasColors, evt.IsNoKick));
        }

        AddSubStrip(subStrips, currentStrip, stripStartIndex);

        var meshes = new List<Ps2Mesh>(subStrips.Count);
        foreach (var (vertices, startsOnOddOutputSlot) in subStrips)
        {
            meshes.Add(new Ps2Mesh
            {
                Checksum = materialChecksum,
                MaterialChecksum = materialChecksum,
                StartsOnOddOutputSlot = startsOnOddOutputSlot,
                Vertices = vertices
            });
        }

        return meshes;
    }

    public static int CountStripTriangles(Ps2Mesh mesh)
    {
        var verts = mesh.Vertices;
        var count = 0;
        var stripStart = 0;
        var parityBias = mesh.StartsOnOddOutputSlot ? 1 : 0;

        for (var i = 0; i < verts.Length; i++)
        {
            if (verts[i].IsStripRestart)
                continue;

            if (i - stripStart < 2)
                continue;

            Ps2Vertex a;
            Ps2Vertex b;
            Ps2Vertex c;
            if (((i - stripStart + parityBias) & 1) == 0)
            {
                a = verts[i - 2];
                b = verts[i - 1];
                c = verts[i];
            }
            else
            {
                a = verts[i - 1];
                b = verts[i - 2];
                c = verts[i];
            }

            if (IsDegenerate(a, b, c))
                continue;

            count++;
        }

        return count;
    }

    private static void AddSubStrip(
        List<(Ps2Vertex[] Vertices, bool StartsOnOddOutputSlot)> subStrips,
        List<Ps2Vertex> currentStrip,
        int stripStartIndex)
    {
        if (currentStrip.Count < 3 || stripStartIndex < 0)
            return;

        subStrips.Add(([.. currentStrip], (stripStartIndex & 1) != 0));
    }

    private static Ps2Vertex ToPs2Vertex(ReplayVertexSource source, bool hasColors, bool isNoKick)
    {
        return new Ps2Vertex(
            source.Position,
            source.Normal,
            128,
            128,
            128,
            128,
            source.U,
            source.V,
            source.HasNormal,
            hasColors,
            source.HasUv,
            isNoKick);
    }

    private static bool IsDegenerate(in Ps2Vertex a, in Ps2Vertex b, in Ps2Vertex c)
    {
        const float epsilon = 1e-8f;

        if (Vector3.DistanceSquared(a.Position, b.Position) <= epsilon ||
            Vector3.DistanceSquared(b.Position, c.Position) <= epsilon ||
            Vector3.DistanceSquared(a.Position, c.Position) <= epsilon)
        {
            return true;
        }

        var cross = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
        return cross.LengthSquared() <= epsilon;
    }
}
