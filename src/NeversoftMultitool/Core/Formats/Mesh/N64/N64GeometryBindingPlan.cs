namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Resolves the object-table matrix used to place an N64 render-bank
///     corner. Static rendering uses the established placement-relative
///     expression (<c>placing object + G_MTX</c>); animation may opt into the
///     recovered global-joint interpretation only after the animation gate has
///     proved it for the complete emitted placement set.
/// </summary>
internal readonly record struct N64GeometryBindingPlan
{
    private N64GeometryBindingPlan(int objectCount, N64MatrixOffsetMode offsetMode)
    {
        ObjectCount = objectCount;
        OffsetMode = offsetMode;
    }

    public int ObjectCount { get; }
    public N64MatrixOffsetMode OffsetMode { get; }
    public bool IsSkinned => OffsetMode == N64MatrixOffsetMode.GlobalJoint;

    public static N64GeometryBindingPlan Static(int objectCount)
    {
        return new N64GeometryBindingPlan(objectCount, N64MatrixOffsetMode.RelativeToPlacement);
    }

    public static N64GeometryBindingPlan Animated(int objectCount)
    {
        return new N64GeometryBindingPlan(objectCount, N64MatrixOffsetMode.GlobalJoint);
    }

    /// <summary>
    ///     Resolves the object whose bind offset the corner receives. Returning
    ///     false preserves the historical static fallback to the origin; an
    ///     animated plan is never published unless every corner resolves.
    /// </summary>
    public bool TryResolveOffsetObjectIndex(
        int placingObjectIndex,
        int matrixIndex,
        out int offsetObjectIndex)
    {
        var candidate = OffsetMode == N64MatrixOffsetMode.GlobalJoint
            ? matrixIndex
            : (long)placingObjectIndex + matrixIndex;
        if (candidate < 0 || candidate >= ObjectCount)
        {
            offsetObjectIndex = -1;
            return false;
        }

        offsetObjectIndex = (int)candidate;
        return true;
    }

    public int ResolveOffsetObjectIndexOrDefault(int placingObjectIndex, int matrixIndex)
    {
        return TryResolveOffsetObjectIndex(placingObjectIndex, matrixIndex, out var resolved)
            ? resolved
            : -1;
    }

    public int ResolveSkinJoint(int matrixIndex)
    {
        if (!IsSkinned || (uint)matrixIndex >= (uint)ObjectCount)
            throw new InvalidOperationException("N64 corner is not covered by a global-joint binding plan.");
        return matrixIndex;
    }
}

internal enum N64MatrixOffsetMode
{
    RelativeToPlacement,
    GlobalJoint
}
