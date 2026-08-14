using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class AnimationDiscoveryArchiveScopingTests
{
    [Fact]
    public void FindForCharacter_ArchiveScopeMatchesWholeCaseInsensitivePathSegments()
    {
        var archivePath = Path.Combine(
            Path.GetTempPath(),
            $"NsMultitool_AnimationScope_{Guid.NewGuid():N}.prx");

        try
        {
            File.WriteAllBytes(archivePath, BuildCompressedPreV3(
                ("Models/TONY/tony.skn", [0]),
                (@"ANIMS\ToNy\right.ska", BuildSkaHeader()),
                ("Anims/tonyhawk/wrong.ska", BuildSkaHeader()),
                ("NotAnims/tony/foreign.ska", BuildSkaHeader())));

            var backend = ArchiveAssetBackend.TryOpen(archivePath);
            Assert.NotNull(backend);
            using (backend.FileSystem)
            {
                var characterEntry = backend.FindByPath("models/tony/tony.skn");
                Assert.NotNull(characterEntry);

                var probes = AnimationDiscovery.FindForCharacter(
                    new ArchiveAssetSource(backend, characterEntry),
                    skeletonBoneCount: 1,
                    TestContext.Current.CancellationToken);

                var probe = Assert.Single(probes);
                var source = Assert.IsType<ArchiveAssetSource>(probe.Source);
                Assert.Equal("ANIMS/ToNy/right.ska", source.Entry.FullName);
            }
        }
        finally
        {
            if (File.Exists(archivePath))
                File.Delete(archivePath);
        }
    }

    private static byte[] BuildSkaHeader()
    {
        var data = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), SkaFile.FlagPlatform);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x3FC00000u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);
        return data;
    }

    private static byte[] BuildCompressedPreV3(params (string Name, byte[] Data)[] files)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0);
        writer.Write(0xABCD0003u);
        writer.Write(files.Length);

        foreach (var (name, data) in files)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name + "\0");
            writer.Write(data.Length);
            writer.Write(0);
            writer.Write((short)nameBytes.Length);
            writer.Write((short)0);
            writer.Write(0u);
            writer.Write(nameBytes);
            writer.Write(data);
            writer.Write(new byte[(4 - data.Length % 4) % 4]);
        }

        var bytes = stream.ToArray();
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 0);
        return bytes;
    }
}
