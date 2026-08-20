using System.Security.Cryptography;

namespace NeversoftMultitool.Core.Formats.DiscImage;

/// <summary>
///     Presents one AES-encrypted Wii partition as a contiguous, seekable,
///     read-only plaintext stream whose offset 0 is the partition's boot.bin —
///     i.e. a GameCube-shaped image that <see cref="GcmFileSystem" /> can walk.
///
///     The partition data area is a run of <c>0x8000</c>-byte clusters. Each
///     cluster is <c>0x400</c> bytes of (encrypted) SHA-1 hashes followed by
///     <c>0x7C00</c> bytes of (encrypted) user data. The user data is decrypted
///     AES-128-CBC with the partition's title key and an IV taken from the
///     still-encrypted cluster bytes at <c>0x3D0..0x3E0</c>; the hash block is
///     not needed for reading. So each physical cluster yields 0x7C00 plaintext
///     bytes, and a logical offset maps to
///     <c>cluster = logical / 0x7C00</c>, <c>within = logical % 0x7C00</c>.
///
///     One decrypted cluster is cached, which makes the sequential reads the FST
///     walk and file extraction perform effectively single-pass.
/// </summary>
public sealed class WiiPartitionStream : Stream
{
    private const int ClusterSize = 0x8000;
    private const int HashSize = 0x400;
    private const int DataSize = 0x7C00;
    private const int IvOffset = 0x3D0;

    private readonly byte[] _cluster = new byte[ClusterSize];
    private readonly byte[] _decrypted = new byte[DataSize];
    private readonly long _dataOffset;
    private readonly Stream _disc;
    private readonly long _length;
    private readonly bool _ownsDisc;
    private readonly Aes _aes;

    private long _cachedCluster = -1;
    private long _position;

    public WiiPartitionStream(Stream disc, long dataOffset, long dataSize, byte[] titleKey, bool ownsDisc = false)
    {
        _disc = disc;
        _dataOffset = dataOffset;
        _length = dataSize / ClusterSize * DataSize; // whole clusters only
        _ownsDisc = ownsDisc;
        _aes = Aes.Create();
        _aes.Key = titleKey;
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.None;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (count > 0 && _position < _length)
        {
            var clusterIndex = _position / DataSize;
            var within = (int)(_position % DataSize);
            var plaintext = DecryptCluster(clusterIndex);
            var n = Math.Min(count, DataSize - within);
            n = (int)Math.Min(n, _length - _position);
            Array.Copy(plaintext, within, buffer, offset, n);
            offset += n;
            count -= n;
            _position += n;
            total += n;
        }

        return total;
    }

    private byte[] DecryptCluster(long clusterIndex)
    {
        if (clusterIndex == _cachedCluster)
            return _decrypted;

        var physical = _dataOffset + clusterIndex * ClusterSize;
        _disc.Position = physical;
        _disc.ReadExactly(_cluster, 0, ClusterSize);

        // IV for the data block is the encrypted bytes at 0x3D0 of this cluster.
        var iv = new byte[16];
        Array.Copy(_cluster, IvOffset, iv, 0, 16);
        using var decryptor = _aes.CreateDecryptor(_aes.Key, iv);
        decryptor.TransformBlock(_cluster, HashSize, DataSize, _decrypted, 0);

        _cachedCluster = clusterIndex;
        return _decrypted;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => _position
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _aes.Dispose();
            if (_ownsDisc)
                _disc.Dispose();
        }

        base.Dispose(disposing);
    }
}
