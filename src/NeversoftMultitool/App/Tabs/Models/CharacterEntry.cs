using System.ComponentModel;
using System.Runtime.CompilerServices;
using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool;

/// <summary>
///     A skinned character (skin file) in the Character Preview tab. Wraps the
///     same data as <see cref="MeshFileEntry"/> but caches the resolved skeleton
///     bone count so the animation panel can filter mismatched anims without
///     re-parsing on every selection.
/// </summary>
internal sealed class CharacterEntry : INotifyPropertyChanged
{
    private int? _skeletonBoneCount;

    public required MeshFileEntry Mesh { get; init; }

    /// <summary>Display name shown in the character list.</summary>
    public string FileName => Mesh.FileName;

    /// <summary>Relative path tooltip shown in the character list.</summary>
    public string RelativePath => Mesh.RelativePath ?? Mesh.FileName;

    public string FormatDisplay => Mesh.FormatDisplay;

    /// <summary>
    ///     Skeleton bone count (resolved lazily on first access). Null if the
    ///     skeleton cannot be loaded or the character has no skeleton.
    /// </summary>
    public int? SkeletonBoneCount
    {
        get => _skeletonBoneCount;
        set
        {
            if (_skeletonBoneCount == value) return;
            _skeletonBoneCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusDisplay));
        }
    }

    public string StatusDisplay => _skeletonBoneCount.HasValue
        ? $"{_skeletonBoneCount} bones"
        : "—";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
