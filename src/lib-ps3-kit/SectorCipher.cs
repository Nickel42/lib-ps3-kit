using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LibPs3Kit;

/// <summary>
/// AES-128-CBC per 2048-byte sector, as PS3 discs use it: the disc key, and an IV of zeros whose last four bytes
/// are the sector number, big-endian. No padding: each sector is 128 blocks.
/// </summary>
internal sealed class SectorCipher : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _iv = new byte[16];

    public SectorCipher(DiscKey key)
    {
        _aes = Aes.Create();
        _aes.Key = key.ToArray();
    }

    private ReadOnlySpan<byte> IvFor(long sector)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_iv.AsSpan(12), (uint)sector);
        return _iv;
    }

    /// <summary>Decrypts one sector in place.</summary>
    public void Decrypt(long sector, Span<byte> data) =>
        _aes.DecryptCbc(data, IvFor(sector), data, PaddingMode.None);

    /// <summary>Encrypts one sector in place.</summary>
    public void Encrypt(long sector, Span<byte> data) =>
        _aes.EncryptCbc(data, IvFor(sector), data, PaddingMode.None);

    public void Dispose() => _aes.Dispose();
}
