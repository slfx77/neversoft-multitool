using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class AnimationDiscoveryCancellationTests
{
    [Fact]
    public void FindInDirectory_PreCancelledEmptyDirectory_Throws()
    {
        var root = Directory.CreateTempSubdirectory("nmt-animation-discovery-cancellation-");
        try
        {
            var token = new CancellationToken(canceled: true);

            Assert.Throws<OperationCanceledException>(() =>
                AnimationDiscovery.FindInDirectory(root.FullName, null, token));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindForCharacter_PreCancelledNonFileSource_ThrowsBeforeReading()
    {
        var source = new TrackingAssetSource();
        var token = new CancellationToken(canceled: true);

        Assert.Throws<OperationCanceledException>(() =>
            AnimationDiscovery.FindForCharacter(source, null, token));
        Assert.False(source.WasRead);
    }

    private sealed class TrackingAssetSource : AssetSource
    {
        public bool WasRead { get; private set; }

        public override string DisplayName => "memory::character.skin.ps2";
        public override string EntryName => "character.skin.ps2";

        public override byte[] ReadBytes()
        {
            WasRead = true;
            return [];
        }

        public override bool CompanionExists(string nameWithExtension) => false;

        public override byte[]? TryReadCompanion(string nameWithExtension) => null;

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null) => null;
    }
}
