using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Verifies the piecewise-rigid rotation composition fix in
///     <see cref="ModelDocumentGeometryAdapter.PopulatePsxAnimations" />. The
///     THPS2 PSX engine's <c>Decomp_GetAnimTransform</c> writes each bone's
///     world rotation as its own local Euler matrix only — no parent
///     multiplication. To reproduce that invariant inside glTF's chained scene
///     graph the adapter pre-divides each emitted quaternion by its parent's
///     engine-local rotation, so the chain composes back to the engine's intent.
/// </summary>
public sealed class PsxAnimationCompositionTests
{
    private const float AngleEpsilon = 1e-3f;

    [Fact]
    public void Composition_GrandchildWorldRotation_EqualsEngineLocalRotation()
    {
        // 3-bone chain: root (Y=90°) → mid (X=45°) → leaf (identity).
        // Engine semantics: leaf.world_rot = leaf.engine_local_rot = identity.
        // After Piece 1's correction, chaining the emitted glTF quaternions
        // through the parent chain must compose back to identity (in glTF coords).
        var animation = BuildAnimation(
            (bone: 0, channelIndex: 1, s16Value: AngleToS16Units(MathF.PI / 2f)), // Ry=90° on root
            (bone: 1, channelIndex: 0, s16Value: AngleToS16Units(MathF.PI / 4f))  // Rx=45° on mid
        );

        var document = CreateThreeBoneDocument();
        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("walk", animation)],
            new PsxAnimationOptions());

        var (root, mid, leaf) = ExtractRotationsAtFrame0(document.Animations[0]);

        // glTF chain: world_rot[leaf] = root * mid * leaf
        var composed = root * mid * leaf;
        AssertQuaternionsClose(Quaternion.Identity, composed);
    }

    [Fact]
    public void Composition_PlaceholderBoneUnderAnimatedParent_GetsCorrectingChannel()
    {
        // Only root has rotation data. Mid + leaf are placeholders. The
        // piecewise-rigid fix must STILL emit channels for mid and leaf so
        // the engine's "world_rot = local_rot (= identity for placeholders)"
        // is preserved despite glTF's automatic parent chaining.
        var animation = BuildAnimation(
            (bone: 0, channelIndex: 1, s16Value: AngleToS16Units(MathF.PI / 2f))
        );

        var document = CreateThreeBoneDocument();
        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("walk", animation)],
            new PsxAnimationOptions());

        var rotationBones = document.Animations[0].Channels
            .Where(c => c.Property == ModelAnimationProperty.Rotation)
            .Select(c => c.BoneIndex)
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal([0, 1, 2], rotationBones);
        Assert.All(
            document.Animations[0].Channels.Where(static c => c.Property == ModelAnimationProperty.Rotation),
            static c => Assert.Equal(ModelAnimationInterpolation.Linear, c.Interpolation));

        // Mid and leaf channels should compose with root to keep the leaf
        // at engine-local identity.
        var (root, mid, leaf) = ExtractRotationsAtFrame0(document.Animations[0]);
        AssertQuaternionsClose(Quaternion.Identity, root * mid * leaf);
    }

    [Fact]
    public void Composition_NoAnimationData_EmitsNoChannels()
    {
        // Regression: all-placeholder animation must not emit any channels.
        var animation = BuildAnimation();
        var document = CreateThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("rest", animation)],
            new PsxAnimationOptions());

        Assert.Empty(document.Animations);
    }

    [Fact]
    public void Translation_WithTranslationOption_EmitsZeroStreams()
    {
        // The default export skips translations, but the diagnostic path still
        // emits channels when enabled. All-zero streams stay anchored to bind.
        var animation = BuildAnimation();
        var document = CreateThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("with_trans", animation)],
            new PsxAnimationOptions(SkipRotation: true, SkipTranslation: false));

        var translationBones = document.Animations[0].Channels
            .Where(c => c.Property == ModelAnimationProperty.Translation)
            .Select(c => c.BoneIndex)
            .OrderBy(static x => x)
            .ToArray();

        Assert.Equal([0, 1, 2], translationBones);
        Assert.All(
            document.Animations[0].Channels.Where(static c => c.Property == ModelAnimationProperty.Translation),
            static c => Assert.Equal(ModelAnimationInterpolation.Linear, c.Interpolation));
    }

    [Fact]
    public void Translation_WithBoneFilter_EmitsOnlySelectedStreams()
    {
        // Skater banks can carry meaningful board motion on one root-side bone
        // while limb translations are noise for the current export path. The
        // diagnostic filter lets visual checks isolate that case.
        var animation = BuildAnimation(
            frameCount: 2,
            (bone: 0, channelIndex: 3, frame: 1, s16Value: 10),
            (bone: 1, channelIndex: 3, frame: 1, s16Value: 20),
            (bone: 2, channelIndex: 3, frame: 1, s16Value: 30));
        var document = CreateThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("board_only", animation)],
            new PsxAnimationOptions(
                SkipRotation: true,
                SkipTranslation: false,
                TranslationBoneFilter: new HashSet<int> { 1 }));

        var channel = Assert.Single(document.Animations[0].Channels);
        Assert.Equal(ModelAnimationProperty.Translation, channel.Property);
        Assert.Equal(1, channel.BoneIndex);
    }

    [Fact]
    public void Translation_WithTranslationOption_UsesRuntimeShiftedVertexScaleDivisor()
    {
        // Anim translations are matrix-local s16 offsets in character vertex
        // units. glTF already stores bind translations, so keys are emitted as
        // bind-anchored deltas. The runtime shifts Super SMatrix translations
        // right by 4 before loading GTE translation, so the default export uses
        // ScaleDivisor * 16.
        var animation = BuildAnimation(
            frameCount: 2,
            (bone: 1, channelIndex: 3, frame: 0, s16Value: 100),
            (bone: 1, channelIndex: 3, frame: 1, s16Value: 820));
        var document = CreateTranslatedThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(scaleDivisor: 36f, translationDivisor: 2.25f),
            skeletonIndex: 0,
            [("with_trans", animation)],
            new PsxAnimationOptions(SkipRotation: true, SkipTranslation: false));

        var channel = Assert.Single(
            document.Animations[0].Channels,
            static c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == 1);

        Assert.Equal(3f, channel.Values[0], 3);
        Assert.Equal(4.25f, channel.Values[3], 3);
    }

    [Fact]
    public void Translation_WithDivisorScale_CanInspectUnshiftedRawDelta()
    {
        // The default translation scale matches the runtime /16 shift. Keeping
        // the scale explicit lets diagnostics reproduce the old unshifted path
        // when needed.
        var animation = BuildAnimation(
            frameCount: 2,
            (bone: 1, channelIndex: 3, frame: 0, s16Value: 100),
            (bone: 1, channelIndex: 3, frame: 1, s16Value: 820));
        var document = CreateTranslatedThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(scaleDivisor: 36f, translationDivisor: 2.25f),
            skeletonIndex: 0,
            [("with_trans", animation)],
            new PsxAnimationOptions(
                SkipRotation: true,
                SkipTranslation: false,
                TranslationDivisorScale: 1f));

        var channel = Assert.Single(
            document.Animations[0].Channels,
            static c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == 1);

        Assert.Equal(3f, channel.Values[0], 3);
        Assert.Equal(23f, channel.Values[3], 3);
    }

    [Fact]
    public void Translation_WithAbsoluteMode_ReplacesBindTranslation()
    {
        // Engine matrices store Tx/Ty/Tz directly. This diagnostic mode tests
        // that path for isolated root-side bones such as the skater board.
        var animation = BuildAnimation(
            frameCount: 1,
            (bone: 1, channelIndex: 3, frame: 0, s16Value: 72));
        var document = CreateTranslatedThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(scaleDivisor: 36f, translationDivisor: 2.25f),
            skeletonIndex: 0,
            [("absolute_trans", animation)],
            new PsxAnimationOptions(
                SkipRotation: true,
                SkipTranslation: false,
                AbsoluteTranslation: true,
                TranslationDivisorScale: 1f));

        var channel = Assert.Single(
            document.Animations[0].Channels,
            static c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == 1);

        Assert.Equal(2f, channel.Values[0], 3);
        Assert.Equal(0f, channel.Values[1], 3);
        Assert.Equal(0f, channel.Values[2], 3);
    }

    [Fact]
    public void Translation_WithEngineWorldMode_SolvesWorldTargetBackToLocal()
    {
        // The engine recursively composes translation targets before rendering
        // Super matrices. The opt-in diagnostic export mode mirrors that
        // world-space target, then solves the local glTF translation needed
        // under the already-exported parent rotation.
        var animation = BuildAnimation(
            frameCount: 2,
            (bone: 0, channelIndex: 2, frame: 1, s16Value: AngleToS16Units(MathF.PI / 2f)));
        var document = CreateTranslatedThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(),
            skeletonIndex: 0,
            [("engine_world_trans", animation)],
            new PsxAnimationOptions(
                SkipTranslation: false,
                EngineWorldTranslation: true));

        var modelAnimation = document.Animations[0];
        var rootRotation = Assert.Single(
            modelAnimation.Channels,
            static c => c.Property == ModelAnimationProperty.Rotation && c.BoneIndex == 0);
        var midTranslation = Assert.Single(
            modelAnimation.Channels,
            static c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == 1);
        var rootFrame1 = ReadQuaternionFrame(rootRotation, 1);
        var expectedLocalFrame1 = Vector3.Transform(
            new Vector3(3f, 4f, 5f),
            Quaternion.Conjugate(rootFrame1));

        AssertVectorClose(new Vector3(3f, 4f, 5f), ReadVector3Frame(midTranslation, 0));
        AssertVectorClose(expectedLocalFrame1, ReadVector3Frame(midTranslation, 1));
    }

    [Fact]
    public void Translation_WithTranslationOption_DefaultsToLocalDeltaPath()
    {
        // The current visual baseline keeps translation emission local and
        // bind-anchored. Unlike the engine-world diagnostic path, a rotating
        // parent does not alter the child's emitted local translation when the
        // child's own Tx/Ty/Tz stream is unchanged.
        var animation = BuildAnimation(
            frameCount: 2,
            (bone: 0, channelIndex: 2, frame: 1, s16Value: AngleToS16Units(MathF.PI / 2f)));
        var document = CreateTranslatedThreeBoneDocument();

        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(),
            skeletonIndex: 0,
            [("local_delta_trans", animation)],
            new PsxAnimationOptions(
                SkipTranslation: false));

        var midTranslation = Assert.Single(
            document.Animations[0].Channels,
            static c => c.Property == ModelAnimationProperty.Translation && c.BoneIndex == 1);

        AssertVectorClose(new Vector3(3f, 4f, 5f), ReadVector3Frame(midTranslation, 0));
        AssertVectorClose(new Vector3(3f, 4f, 5f), ReadVector3Frame(midTranslation, 1));
    }

    [Fact]
    public void Legacy_DoesNotEmitCorrectionChannels()
    {
        // LegacyRotationChain=true reproduces the pre-fix behaviour: only bones
        // with their own rotation data get channels. Provided so users can A/B
        // compare animations during validation.
        var animation = BuildAnimation(
            (bone: 0, channelIndex: 1, s16Value: AngleToS16Units(MathF.PI / 2f))
        );

        var document = CreateThreeBoneDocument();
        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("walk", animation)],
            new PsxAnimationOptions(LegacyRotationChain: true));

        var rotationBones = document.Animations[0].Channels
            .Where(c => c.Property == ModelAnimationProperty.Rotation)
            .Select(c => c.BoneIndex)
            .ToArray();

        Assert.Equal([0], rotationBones);
    }

    [Fact]
    public void Composition_RootRotationOnly_LeafChannelUndoesParent()
    {
        // Single-rotation case: only root has Y=90°. Engine says leaf.world_rot
        // = identity. With chaining, leaf's emitted quaternion must be exactly
        // inverse(root) so root * leaf = identity.
        var rootYaw = MathF.PI / 2f;
        var animation = BuildAnimation(
            (bone: 0, channelIndex: 1, s16Value: AngleToS16Units(rootYaw))
        );

        var document = CreateThreeBoneDocument();
        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("walk", animation)],
            new PsxAnimationOptions());

        var (root, mid, _) = ExtractRotationsAtFrame0(document.Animations[0]);

        // Root is parentless, so it's emitted raw (just glTF-flipped).
        // Mid's correction = inverse(root_engine_local) * identity = inverse(root).
        // Verify root * mid == identity (engine_local_rot[mid] = identity).
        AssertQuaternionsClose(Quaternion.Identity, root * mid);
    }

    [Fact]
    public void Composition_ForwardParentIndex_StillEmitsDescendantCorrection()
    {
        // PSX object order is not guaranteed parent-first. Spidey-style
        // hierarchies can have a child whose parent appears later in the
        // object list. The correction mask must follow the graph, not index
        // order, otherwise placeholder children inherit too much rotation in
        // glTF and the animation bends past the engine pose.
        var rootYaw = MathF.PI / 2f;
        var animation = BuildAnimation(
            (bone: 2, channelIndex: 1, s16Value: AngleToS16Units(rootYaw))
        );

        var document = CreateForwardParentDocument();
        ModelDocumentGeometryAdapter.PopulatePsxAnimations(
            document, BuildPsxFile(), skeletonIndex: 0,
            [("idle", animation)],
            new PsxAnimationOptions());

        var byBone = document.Animations[0].Channels
            .Where(c => c.Property == ModelAnimationProperty.Rotation)
            .ToDictionary(c => c.BoneIndex, ReadQuaternionFrame0);

        Assert.Equal([0, 2], byBone.Keys.OrderBy(static x => x).ToArray());
        AssertQuaternionsClose(Quaternion.Identity, byBone[2] * byBone[0]);
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static PsxMeshFile BuildPsxFile(float scaleDivisor = 1f, float translationDivisor = 1f)
    {
        return new PsxMeshFile
        {
            Version = 4,
            Objects = [],
            Meshes = [],
            MeshNameHashes = [],
            TextureHashes = [],
            ScaleDivisor = scaleDivisor,
            TranslationDivisor = translationDivisor
        };
    }

    private static ModelDocument CreateThreeBoneDocument()
    {
        var document = new ModelDocument { Name = "test" };
        var skeleton = new ModelSkeleton { Name = "rig" };
        skeleton.Bones.Add(new ModelBone { Name = "root", ParentIndex = -1 });
        skeleton.Bones.Add(new ModelBone { Name = "mid", ParentIndex = 0 });
        skeleton.Bones.Add(new ModelBone { Name = "leaf", ParentIndex = 1 });
        document.Skeletons.Add(skeleton);
        return document;
    }

    private static ModelDocument CreateTranslatedThreeBoneDocument()
    {
        var document = new ModelDocument { Name = "test" };
        var skeleton = new ModelSkeleton { Name = "rig" };
        skeleton.Bones.Add(new ModelBone { Name = "root", ParentIndex = -1 });
        skeleton.Bones.Add(new ModelBone
        {
            Name = "mid",
            ParentIndex = 0,
            LocalTransform = Matrix4x4.CreateTranslation(3f, 4f, 5f)
        });
        skeleton.Bones.Add(new ModelBone { Name = "leaf", ParentIndex = 1 });
        document.Skeletons.Add(skeleton);
        return document;
    }

    private static ModelDocument CreateForwardParentDocument()
    {
        var document = new ModelDocument { Name = "test" };
        var skeleton = new ModelSkeleton { Name = "rig" };
        skeleton.Bones.Add(new ModelBone { Name = "child_before_parent", ParentIndex = 2 });
        skeleton.Bones.Add(new ModelBone { Name = "orphan", ParentIndex = -1 });
        skeleton.Bones.Add(new ModelBone { Name = "root_after_child", ParentIndex = -1 });
        document.Skeletons.Add(skeleton);
        return document;
    }

    private static PsxAnimation BuildAnimation(params (int bone, int channelIndex, short s16Value)[] keys)
    {
        const int boneCount = 3;
        const int frameCount = 1;
        var channels = new short[boneCount, PsxAnimation.ChannelsPerBone, frameCount];
        foreach (var (bone, channel, s16) in keys)
            channels[bone, channel, 0] = s16;

        return new PsxAnimation
        {
            BoneCount = boneCount,
            FrameCount = frameCount,
            Channels = channels
        };
    }

    private static PsxAnimation BuildAnimation(
        int frameCount,
        params (int bone, int channelIndex, int frame, short s16Value)[] keys)
    {
        const int boneCount = 3;
        var channels = new short[boneCount, PsxAnimation.ChannelsPerBone, frameCount];
        foreach (var (bone, channel, frame, s16) in keys)
            channels[bone, channel, frame] = s16;

        return new PsxAnimation
        {
            BoneCount = boneCount,
            FrameCount = frameCount,
            Channels = channels
        };
    }

    /// <summary>
    ///     Converts a radian angle to PSY-Q units used by
    ///     <see cref="PsxAnimation.GetBoneRotation" /> (4096 = 360°).
    /// </summary>
    private static short AngleToS16Units(float radians)
    {
        return (short)Math.Round(radians * (4096f / (2f * MathF.PI)));
    }

    private static (Quaternion Root, Quaternion Mid, Quaternion Leaf) ExtractRotationsAtFrame0(ModelAnimation animation)
    {
        var byBone = animation.Channels
            .Where(c => c.Property == ModelAnimationProperty.Rotation)
            .ToDictionary(c => c.BoneIndex, ReadQuaternionFrame0);

        return (
            byBone.GetValueOrDefault(0, Quaternion.Identity),
            byBone.GetValueOrDefault(1, Quaternion.Identity),
            byBone.GetValueOrDefault(2, Quaternion.Identity));
    }

    private static Quaternion ReadQuaternionFrame0(ModelAnimationChannel channel)
    {
        return ReadQuaternionFrame(channel, 0);
    }

    private static Quaternion ReadQuaternionFrame(ModelAnimationChannel channel, int frame)
    {
        var offset = frame * 4;
        return new Quaternion(
            channel.Values[offset],
            channel.Values[offset + 1],
            channel.Values[offset + 2],
            channel.Values[offset + 3]);
    }

    private static Vector3 ReadVector3Frame(ModelAnimationChannel channel, int frame)
    {
        var offset = frame * 3;
        return new Vector3(
            channel.Values[offset],
            channel.Values[offset + 1],
            channel.Values[offset + 2]);
    }

    private static void AssertQuaternionsClose(Quaternion expected, Quaternion actual)
    {
        // Quaternions q and -q represent the same rotation, so accept either sign.
        var direct = Distance(expected, actual);
        var flipped = Distance(expected, Quaternion.Negate(actual));
        var min = MathF.Min(direct, flipped);
        Assert.True(
            min < AngleEpsilon,
            $"Quaternion mismatch: expected {expected}, actual {actual} (distance {min}).");
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        var distance = Vector3.Distance(expected, actual);
        Assert.True(
            distance < AngleEpsilon,
            $"Vector mismatch: expected {expected}, actual {actual} (distance {distance}).");
    }

    private static float Distance(Quaternion a, Quaternion b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        var dw = a.W - b.W;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
    }

}
