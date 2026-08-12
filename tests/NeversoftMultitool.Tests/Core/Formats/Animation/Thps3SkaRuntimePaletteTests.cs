using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

/// <summary>
///     Pins THPS3's final skin-matrix composition against a retained PCSX2
///     runtime palette. The savestate is not needed by the test: only its small,
///     normalized 29-bone RwMatrix palette is committed as a golden.
/// </summary>
public sealed class Thps3SkaRuntimePaletteTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";
    private const string ExecutableSha256 = "361B89107B8ACB87DCD89B71330F0AE54FF6ACC498F40E9462D4CCFB24B8E00C";
    private const string SavestateSha256 = "633B8BB6C80E34E212F693DAD09D29A4ADD4568859A2C11056861B38B897CD05";
    private const string SknSha256 = "DB56BFBC17E0772E7B3C1DD03D9C0CE5863A2723C714525B325F6533779F99B6";
    private const string SkaSha256 = "D0118026564FDDC46A335B618324B9984D82ECF25A859A253B0FE442FAEA4CC0";
    private const string PayloadSha256 = "9361EDCF29A801A929E723DD244C5A6FA8710DBF6E94F40FC79611760C98F99F";
    private const float RuntimeTime = 0.799999475479126f;

    [Fact]
    public void Writer_Thps3Runtime_ConjugatesRotationAndKeepsRawTranslation()
    {
        var bindRotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.4f, -0.2f, 0.1f));
        var sourceRotation = Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(-0.7f, 0.3f, 0.25f));
        var sourceTranslation = new Vector3(17f, -9f, 5f);
        var document = new ModelDocument { Name = "thps3_policy" };
        var skeleton = new ModelSkeleton { Name = "skeleton" };
        skeleton.Bones.Add(new ModelBone
        {
            Name = "bone_0",
            ParentIndex = -1,
            LocalTransform = Matrix4x4.CreateFromQuaternion(bindRotation)
                             * Matrix4x4.CreateTranslation(100f, 200f, 300f),
            InverseBindMatrix = Matrix4x4.Identity
        });
        document.Skeletons.Add(skeleton);

        SkaAnimationWriter.PopulateSkaAnimations(
            document,
            0,
            [("idle", new SkaAnimation
            {
                Version = 0,
                Flags = 0,
                Duration = 1f,
                BoneTracks =
                [
                    new SkaBoneTrack
                    {
                        BoneIndex = 0,
                        RotationKeys = [new SkaRotationKey(0.25f, sourceRotation)],
                        TranslationKeys = [new SkaTranslationKey(0.25f, sourceTranslation)]
                    }
                ]
            })],
            SkaCompositionMode.Thps3Runtime);

        var animation = Assert.Single(document.Animations);
        var rotation = Assert.Single(animation.Channels,
            static channel => channel.Property == ModelAnimationProperty.Rotation);
        var translation = Assert.Single(animation.Channels,
            static channel => channel.Property == ModelAnimationProperty.Translation);
        var emittedRotation = new Quaternion(
            rotation.Values[0], rotation.Values[1], rotation.Values[2], rotation.Values[3]);

        Assert.True(MathF.Abs(Quaternion.Dot(
            Quaternion.Conjugate(sourceRotation), emittedRotation)) > 0.999999f);
        Assert.Equal([sourceTranslation.X, sourceTranslation.Y, sourceTranslation.Z], translation.Values);
    }

    [CorpusFact]
    public void IdleExport_All29SkinMatricesMatchFinalRuntimePalette()
    {
        Assert.SkipWhen(paths.Thps3SkaDir == null || paths.GoldenFilesDir == null || !paths.HasSampleBuilds,
            "THPS3 SKA, golden files, or Sample/Builds are not available");

        var skaPath = Path.Combine(paths.Thps3SkaDir!, "skater_m_Idle.ska");
        var sknPaths = paths.FindSampleFiles(BuildName, "skater_m.skn")
            .Where(static path => path.Replace('\\', '/').EndsWith(
                "/SKATE3/pre/cas_male/models/skater_m/skater_m.skn",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.SkipWhen(!File.Exists(skaPath), "THPS3 skater_m_Idle.ska is not available");
        var sknPath = Assert.Single(sknPaths);
        Assert.Equal(5_280, new FileInfo(skaPath).Length);
        Assert.Equal(155_771, new FileInfo(sknPath).Length);
        Assert.Equal(SkaSha256, Sha256(File.ReadAllBytes(skaPath)));
        Assert.Equal(SknSha256, Sha256(File.ReadAllBytes(sknPath)));

        var animation = SkaFile.Parse(File.ReadAllBytes(skaPath));
        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(sknPath),
            FileName = Path.GetFileName(sknPath),
            OutputStem = "skater_m",
            SourceKind = ModelSourceKind.RenderWareDff,
            SkaAnimations = [("skater_m_Idle", animation)]
        });
        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.True(triangles > 0);
        Assert.NotNull(glbBytes);

        using var stream = new MemoryStream(glbBytes, false);
        var glb = ModelRoot.ReadGLB(stream);
        var glbAnimation = Assert.Single(glb.LogicalAnimations);
        var skin = Assert.Single(glb.LogicalSkins);
        Assert.Equal(29, skin.JointsCount);
        var (rootJoint, _) = skin.GetJoint(0);
        var coordinateRoot = Assert.IsType<Node>(rootJoint.VisualParent);
        Assert.Equal("skeleton_root", coordinateRoot.Name);
        Assert.True(Matrix4x4.Invert(coordinateRoot.LocalMatrix, out var inverseCoordinateRoot));

        var expected = ReadGoldenMatrices();
        Assert.Equal(skin.JointsCount, expected.Length);
        double squaredError = 0;
        var valueCount = 0;
        var maxAbsoluteError = 0f;
        for (var bone = 0; bone < skin.JointsCount; bone++)
        {
            var (joint, inverseBind) = skin.GetJoint(bone);
            var exportWorld = joint.GetWorldMatrix(glbAnimation, RuntimeTime);
            var sourceWorld = exportWorld * inverseCoordinateRoot;
            var actual = inverseBind * sourceWorld;
            AccumulateAffineError(expected[bone], actual,
                ref squaredError, ref valueCount, ref maxAbsoluteError);
        }

        var rmse = Math.Sqrt(squaredError / valueCount);
        Assert.True(rmse < 0.0005,
            $"THPS3 runtime palette RMSE {rmse:G9} exceeded 0.0005");
        Assert.True(maxAbsoluteError < 0.003f,
            $"THPS3 runtime palette maximum error {maxAbsoluteError:G9} exceeded 0.003");
    }

    [CorpusFact]
    public void LooseDiscCorpus_RuntimeTracksParseWithoutPlaceholderSuppression()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample/Builds are not available");
        var files = paths.FindSampleFiles(BuildName, "*.ska")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(3_000, files.Length);

        var runtimeFiles = 0;
        long tracks = 0;
        long rotationKeys = 0;
        long translationKeys = 0;
        var payloads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            if (data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4)) != 0x80000000u)
                continue;

            runtimeFiles++;
            payloads.Add(Sha256(data));
            var animation = SkaFile.Parse(data);
            tracks += animation.BoneTracks.Length;
            foreach (var track in animation.BoneTracks)
            {
                rotationKeys += track.RotationKeys.Length;
                translationKeys += track.TranslationKeys.Length;
                Assert.False(track.RotationKeys.Length == 1
                             && track.RotationKeys[0].Rotation == Quaternion.Identity);
                Assert.False(track.TranslationKeys.Length == 1
                             && track.TranslationKeys[0].Translation == Vector3.Zero);
                if (track.BoneIndex > 0)
                    Assert.NotEmpty(track.RotationKeys);
                Assert.NotEmpty(track.TranslationKeys);
            }
        }

        Assert.Equal(2_998, runtimeFiles);
        Assert.Equal(928, payloads.Count);
        Assert.Equal(51_712, tracks);
        Assert.Equal(593_497, rotationKeys);
        Assert.Equal(158_983, translationKeys);
    }

    private Matrix4x4[] ReadGoldenMatrices()
    {
        var path = Path.Combine(paths.GoldenFilesDir!, "Animation", "thps3-idle-runtime-palette.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(PayloadSha256, root.GetProperty("payloadSha256").GetString());
        var source = root.GetProperty("source");
        Assert.Equal(ExecutableSha256, source.GetProperty("executableSha256").GetString());
        Assert.Equal(SavestateSha256, source.GetProperty("savestateSha256").GetString());
        Assert.Equal(SknSha256, source.GetProperty("sknSha256").GetString());
        Assert.Equal(SkaSha256, source.GetProperty("skaSha256").GetString());
        Assert.Equal("0x00B404C0", source.GetProperty("poseAddress").GetString());
        Assert.Equal("0x00B415F0", source.GetProperty("paletteAddress").GetString());
        Assert.Equal(29, source.GetProperty("boneCount").GetInt32());
        Assert.Equal(RuntimeTime, source.GetProperty("time").GetSingle());
        var payload = Convert.FromBase64String(root.GetProperty("float32LeBase64").GetString()!);
        Assert.Equal(29 * 12 * sizeof(float), payload.Length);
        Assert.Equal(PayloadSha256, Sha256(payload));

        var result = new Matrix4x4[29];
        for (var bone = 0; bone < result.Length; bone++)
        {
            var offset = bone * 12 * sizeof(float);
            float Read(int index) => BinaryPrimitives.ReadSingleLittleEndian(
                payload.AsSpan(offset + index * sizeof(float), sizeof(float)));
            result[bone] = new Matrix4x4(
                Read(0), Read(1), Read(2), 0f,
                Read(3), Read(4), Read(5), 0f,
                Read(6), Read(7), Read(8), 0f,
                Read(9), Read(10), Read(11), 1f);
        }

        return result;
    }

    private static void AccumulateAffineError(
        Matrix4x4 expected,
        Matrix4x4 actual,
        ref double squaredError,
        ref int valueCount,
        ref float maxAbsoluteError)
    {
        var expectedValues = new[]
        {
            expected.M11, expected.M12, expected.M13,
            expected.M21, expected.M22, expected.M23,
            expected.M31, expected.M32, expected.M33,
            expected.M41, expected.M42, expected.M43
        };
        var actualValues = new[]
        {
            actual.M11, actual.M12, actual.M13,
            actual.M21, actual.M22, actual.M23,
            actual.M31, actual.M32, actual.M33,
            actual.M41, actual.M42, actual.M43
        };
        for (var i = 0; i < expectedValues.Length; i++)
        {
            var difference = expectedValues[i] - actualValues[i];
            squaredError += difference * difference;
            valueCount++;
            maxAbsoluteError = Math.Max(maxAbsoluteError, MathF.Abs(difference));
        }
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
