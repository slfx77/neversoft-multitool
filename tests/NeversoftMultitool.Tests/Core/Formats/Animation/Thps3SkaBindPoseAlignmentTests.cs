using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using Xunit;
using Xunit.v3;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

/// <summary>
///     Diagnostic: compare the THPS3 SKA's first rotation/translation key per bone
///     against the SKN's bind-pose local transform (derived from IBMs + HAnim
///     push/pop hierarchy). For an idle animation these should roughly agree —
///     whichever conjugation convention makes them agree is the right one.
/// </summary>
public class Thps3SkaBindPoseAlignmentTests
{
    private const string SkaPath = "c:/tmp/skater_m_Idle.ska";
    private const string SknPath = "c:/tmp/skater_m.skn";

    private readonly ITestOutputHelper _output;

    public Thps3SkaBindPoseAlignmentTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void CompareFirstKeyToBindLocal()
    {
        if (!File.Exists(SkaPath) || !File.Exists(SknPath))
            return;

        var skaData = File.ReadAllBytes(SkaPath);
        var anim = SkaFile.Parse(skaData);

        var clump = RwDffFile.Parse(SknPath);
        var skin = clump.Atomics.First(a => a.SkinData != null).SkinData!;

        // Reconstruct bind-pose local transform per bone via the same logic
        // RwDffGltfWriter uses.
        var parent = new int[skin.NumBones];
        var stack = new Stack<int>();
        var cur = -1;
        for (var i = 0; i < skin.NumBones; i++)
        {
            parent[i] = cur;
            var f = skin.Bones[i].Flags & 3;
            if ((f & 2) != 0) stack.Push(cur);
            cur = i;
            if ((f & 1) != 0) cur = stack.Count > 0 ? stack.Pop() : -1;
        }

        var global = new Matrix4x4[skin.NumBones];
        for (var i = 0; i < skin.NumBones; i++)
        {
            if (!Matrix4x4.Invert(skin.Bones[i].InverseBindMatrix, out var bind))
                bind = Matrix4x4.Identity;
            global[i] = bind;
        }

        _output.WriteLine($"{"bone",-5} {"parent",-7} " +
                          $"{"bindLocal_t",-36} " +
                          $"{"skaFirst_t",-36} " +
                          $"{"bindLocal_q",-48} " +
                          $"{"skaFirst_q_raw",-48} " +
                          $"{"skaFirst_q_conj",-48}");

        for (var i = 0; i < skin.NumBones; i++)
        {
            Matrix4x4 local;
            if (parent[i] >= 0 && Matrix4x4.Invert(global[parent[i]], out var invP))
                local = global[i] * invP;
            else
                local = global[i];

            Matrix4x4.Decompose(local, out _, out var bindQ, out var bindT);

            var track = i < anim.BoneTracks.Length ? anim.BoneTracks[i] : null;
            var skaQ = track != null && track.RotationKeys.Length > 0
                ? track.RotationKeys[0].Rotation
                : Quaternion.Identity;
            var skaT = track != null && track.TranslationKeys.Length > 0
                ? track.TranslationKeys[0].Translation
                : Vector3.Zero;

            // After our parser's current fix we store conj(q_raw); recover raw
            // by re-conjugating (conj is its own inverse).
            var skaQraw = Quaternion.Conjugate(skaQ);
            var skaQconj = skaQ;

            _output.WriteLine($"{i,-5} {parent[i],-7} " +
                              $"{FormatVec(bindT),-36} {FormatVec(skaT),-36} " +
                              $"{FormatQuat(bindQ),-48} " +
                              $"{FormatQuat(skaQraw),-48} " +
                              $"{FormatQuat(skaQconj),-48}");
        }
    }

    private static string FormatVec(Vector3 v)
        => $"({v.X:+0.00;-0.00}, {v.Y:+0.00;-0.00}, {v.Z:+0.00;-0.00})";

    private static string FormatQuat(Quaternion q)
        => $"({q.X:+0.000;-0.000},{q.Y:+0.000;-0.000},{q.Z:+0.000;-0.000},{q.W:+0.000;-0.000})";
}
