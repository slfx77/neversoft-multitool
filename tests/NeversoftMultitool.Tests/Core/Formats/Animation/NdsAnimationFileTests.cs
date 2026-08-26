using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Gob;
using NeversoftMultitool.Core.Formats.Mesh.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

/// <summary>
///     Pins the DS animation clip format and the scatter that applies it.
///
///     The load-bearing corpus facts: every clip the loader's naming reaches
///     parses with exact channel identities (rotation keys 12 bytes, vector keys
///     16, channel size == keysOffset + keys*size), every clip's channel counts
///     match its geometry's joint-flag census (the gate that makes application
///     safe), and frame 0 of a skater clip reproduces the shipped bind operands —
///     the measurement that fixed the quaternion convention.
/// </summary>
public sealed class NdsAnimationFileTests(TestPaths paths)
{
    private const string Sk8landBuild = "Tony Hawk's American Sk8land (2005-11-15, DS - Final)";
    private const string Sk8landRom = "Tony Hawk's American Sk8land (USA).nds";
    private const string Sk8landGob = "vvobj/generated/gob/main.gob";

    [Fact]
    public void ParsesASyntheticClipAndEvaluatesItsChannels()
    {
        var clip = BuildSyntheticClip();
        Assert.True(NdsAnimationFile.TryParse(clip, out var file));
        Assert.Equal(8, file!.Frames);
        Assert.Single(file.Rotations);
        Assert.Single(file.Translations);
        Assert.Single(file.Scales);

        // Key 0 is identity; key at frame 8 is a half-turn around Z.
        var q0 = file.RotationAt(0, 0f);
        Assert.Equal(1f, MathF.Abs(q0.W), 3);
        var q1 = file.RotationAt(0, 8f);
        Assert.Equal(1f, MathF.Abs(q1.Z), 3);

        // Translation lerps between (0,0,0) and (1,2,3).
        var mid = file.TranslationAt(0, 4f);
        Assert.Equal(new Vector3(0.5f, 1f, 1.5f), mid);
        Assert.Equal(Vector3.One, file.ScaleAt(0, 0f));
    }

    [Fact]
    public void RejectsAClipWhoseTableEndDoesNotMatchItsCounts()
    {
        var clip = BuildSyntheticClip();
        BinaryPrimitives.WriteInt32LittleEndian(clip.AsSpan(16), 999);
        Assert.False(NdsAnimationFile.TryParse(clip, out _));
    }

    [CorpusFact]
    public void Sk8land_EveryReachableClipParsesAndMatchesItsGeometrysJointFlags()
    {
        using var gob = OpenGob();
        var byKey = new Dictionary<uint, ArchiveEntry>();
        foreach (var entry in gob.Entries)
            byKey[entry.Crc] = entry;

        var models = 0;
        var clips = 0;
        var applicable = 0;
        foreach (var entry in gob.Entries)
        {
            if (!NdsModelSet.TryParseGeometryName(
                    GobNames.TryResolve(entry.Crc), out var idA, out var idB))
            {
                continue;
            }

            var geometryData = gob.ReadEntry(entry);
            if (!NdsGeometryFile.TryParseValidated(geometryData, out var geometry))
                continue;

            var modelClips = 0;
            for (var n = 0; ; n++)
            {
                if (!byKey.TryGetValue(NdsModelSet.ClipKey(idA, idB, n), out var clipEntry))
                    break;

                Assert.True(NdsAnimationFile.TryParse(gob.ReadEntry(clipEntry), out var clip),
                    $"clip {n} of {idA:x8}.{idB:x8} failed to parse");
                modelClips++;
                if (NdsPoseScatter.CanApply(geometry, clip!))
                    applicable++;
            }

            if (modelClips == 0)
                continue;
            models++;
            clips += modelClips;
        }

        Assert.Equal(77, models);
        Assert.Equal(11156, clips);
        Assert.Equal(11156, applicable);
    }

    /// <summary>
    ///     The regression that pins the quaternion convention: patching frame 0 of
    ///     the skater's first clip must reproduce the shipped bind operands. Under
    ///     the transposed convention the same measurement lands at RMS 0.42.
    /// </summary>
    [CorpusFact]
    public void Sk8land_SkaterFrameZeroReproducesTheBindPose()
    {
        using var gob = OpenGob();
        var byKey = new Dictionary<uint, ArchiveEntry>();
        foreach (var entry in gob.Entries)
            byKey[entry.Crc] = entry;

        var geometryData = gob.ReadEntry(byKey[GobNames.Hash(".\\07b0aa3f.07b0aa3f.geometry.bin")]);
        Assert.True(NdsGeometryFile.TryParseValidated(geometryData, out var geometry));
        Assert.True(NdsAnimationFile.TryParse(
            gob.ReadEntry(byKey[NdsModelSet.ClipKey(0x07B0AA3F, 0x07B0AA3F, 0)]), out var clip));
        Assert.True(NdsPoseScatter.CanApply(geometry!, clip!));

        var bind = Flatten(NdsGxInterpreter.Run(geometryData, geometry!));
        var patched = NdsPoseScatter.Apply(geometryData, geometry!, clip!, 0f);
        var posed = Flatten(NdsGxInterpreter.Run(patched, geometry!));

        Assert.Equal(bind.Count, posed.Count);
        var sum = 0.0;
        for (var i = 0; i < bind.Count; i++)
            sum += (bind[i] - posed[i]).LengthSquared();
        var rms = Math.Sqrt(sum / bind.Count);
        Assert.True(rms < 0.01, $"frame-0 RMS {rms} should be ~0.001");
    }

    private static List<Vector3> Flatten(IReadOnlyList<NdsGeometryGroup> groups)
    {
        var result = new List<Vector3>();
        foreach (var group in groups)
        foreach (var vertex in group.Vertices)
            result.Add(vertex.Position);
        return result;
    }

    private IArchiveFileSystem OpenGob()
    {
        var romPath = paths.FindSampleFile(Sk8landBuild, Sk8landRom);
        Assert.SkipWhen(romPath == null, "Sk8land ROM sample not available");
        var cart = ArchiveFileSystem.TryOpen(romPath!);
        var gob = cart!.TryOpenNested(cart.FindByPath(Sk8landGob)!);
        Assert.NotNull(gob);
        return gob!;
    }

    /// <summary>8 frames; one rotation, one translation, one scale channel, two keys each.</summary>
    private static byte[] BuildSyntheticClip()
    {
        var rotation = Channel(8, 12,
        [
            [0, 0, 0, 0, 0, 4096],           // t=0 identity (x y z w)
            [8, 0, 0, 0, 4096, 0]            // t=8 half-turn around Z
        ]);
        var translation = Channel(8, 16,
        [
            [0, 0, 0, 0, 0],
            [8, 0, 4096, 8192, 12288]        // t=8 -> (1,2,3)
        ]);
        var scale = Channel(8, 16,
        [
            [0, 0, 4096, 4096, 4096],
            [8, 0, 4096, 4096, 4096]
        ]);

        var header = new byte[20 + 3 * 4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 8);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), header.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), header.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), header.Length + rotation.Length);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(28), header.Length + rotation.Length + translation.Length);
        return [.. header, .. rotation, .. translation, .. scale];
    }

    private static byte[] Channel(int frames, int keySize, int[][] keys)
    {
        var data = new byte[16 + keys.Length * keySize];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)frames);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), (ushort)keys.Length);
        data[6] = (byte)keySize;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 16);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), 16);
        for (var k = 0; k < keys.Length; k++)
        {
            var at = 16 + k * keySize;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), (ushort)keys[k][0]);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at + 2), (ushort)keys[k][1]);
            for (var v = 2; v < keys[k].Length; v++)
            {
                if (keySize == 12)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(
                        data.AsSpan(at + 4 + (v - 2) * 2), (short)keys[k][v]);
                }
                else
                {
                    BinaryPrimitives.WriteInt32LittleEndian(
                        data.AsSpan(at + 4 + (v - 2) * 4), keys[k][v]);
                }
            }
        }

        return data;
    }
}
