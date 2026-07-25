namespace NeversoftMultitool.Core.Formats.Animation;

internal sealed record PsxAnimationBoneRemap(IReadOnlyList<int> SourceToTarget)
{
    public bool IsIdentity
    {
        get
        {
            for (var i = 0; i < SourceToTarget.Count; i++)
            {
                if (SourceToTarget[i] != i)
                    return false;
            }

            return true;
        }
    }

    public int RemappedCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < SourceToTarget.Count; i++)
            {
                if (SourceToTarget[i] != i)
                    count++;
            }

            return count;
        }
    }
}
