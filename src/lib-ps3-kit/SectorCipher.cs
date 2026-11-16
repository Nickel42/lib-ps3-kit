using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LibPs3Kit;

/// <summary>
/// AES-128-CBC per 2048-byte sector, as PS3 discs use it: the disc key, and an IV of zeros whose last four bytes
/// are the sector number, big-endian. No padding: each sector is 128 blocks.
/// </summary>
internal sealed class SectorCipher : IDisposable
{
    private const int SectorSize = RegionTable.SectorSize;
    private const int Block = 16;

    /// <summary>How many sectors <see cref="DecryptRun"/> takes per call into AES: big enough that the call costs nothing, small enough to stay in the cache.</summary>
    private const int SectorsPerSlice = 128;

    private readonly Aes _aes;
    private readonly byte[] _iv = new byte[16];
    private byte[]? _scratch;

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

    /// <summary>
    /// Decrypts whole sectors in place, the first being <paramref name="firstSector"/>: the same bytes as
    /// <see cref="Decrypt"/> on each, but many sectors per call into AES. CBC decryption is each block decrypted on
    /// its own (ECB) and XORed with the ciphertext block before it, or with the IV for a sector's first block, so a
    /// slice of sectors is copied aside, ECB-decrypted in one call, and XORed with the copy. Not for two threads at once.
    /// </summary>
    public void DecryptRun(long firstSector, Span<byte> data)
    {
        if (data.Length % SectorSize != 0) throw new ArgumentException("Whole sectors only.", nameof(data));
        var scratch = _scratch ??= new byte[SectorsPerSlice * SectorSize];
        var sector = firstSector;
        while (data.Length > 0)
        {
            var bytes = Math.Min(data.Length, scratch.Length);
            var slice = data[..bytes];
            var cipher = scratch.AsSpan(0, bytes);
            slice.CopyTo(cipher);
            _aes.DecryptEcb(cipher, slice, PaddingMode.None);
            for (var at = 0; at < bytes; at += SectorSize, sector++)
            {
                var plain = slice.Slice(at, SectorSize);
                Xor(plain[..Block], IvFor(sector));
                Xor(plain[Block..], cipher.Slice(at, SectorSize - Block));
            }
            data = data[bytes..];
        }
    }

    /// <summary><paramref name="target"/> ^= <paramref name="with"/>, the same length (a multiple of 16).</summary>
    private static void Xor(Span<byte> target, ReadOnlySpan<byte> with)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
            for (; i + Vector<byte>.Count <= target.Length; i += Vector<byte>.Count)
                (new Vector<byte>(target[i..]) ^ new Vector<byte>(with[i..])).CopyTo(target[i..]);
        var t = MemoryMarshal.Cast<byte, ulong>(target[i..]);
        var w = MemoryMarshal.Cast<byte, ulong>(with[i..]);
        for (var k = 0; k < t.Length; k++) t[k] ^= w[k];
    }

    /// <summary>Encrypts one sector in place.</summary>
    public void Encrypt(long sector, Span<byte> data) =>
        _aes.EncryptCbc(data, IvFor(sector), data, PaddingMode.None);

    public void Dispose() => _aes.Dispose();
}
