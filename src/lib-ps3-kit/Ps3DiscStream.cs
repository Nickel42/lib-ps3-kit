namespace LibPs3Kit;

/// <summary>What a <see cref="Ps3DiscStream"/> does to the encrypted regions as it reads them.</summary>
public enum SectorTransform
{
    /// <summary>Nothing: the bytes as they are on disk.</summary>
    None,
    Decrypt,
    Encrypt,
}

/// <summary>
/// A read-only, seekable view of a PS3 disc image that decrypts (or encrypts) the encrypted regions as it reads,
/// sector by sector, and passes the plain regions through. The ISO 9660 reader works on it, so files can be
/// extracted straight from a Redump image without writing a decrypted copy first.
/// </summary>
public sealed class Ps3DiscStream : Stream
{
    private const int SectorSize = RegionTable.SectorSize;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly RegionTable _regions;
    private readonly SectorCipher? _cipher;
    private readonly SectorTransform _transform;
    private readonly byte[] _sector = new byte[SectorSize];
    private long _cachedSector = -1;
    private long _position;

    public Ps3DiscStream(Stream inner, RegionTable regions, DiscKey? key, SectorTransform transform, bool leaveOpen = false)
    {
        if (!inner.CanRead || !inner.CanSeek) throw new ArgumentException("The image stream must be readable and seekable.", nameof(inner));
        if (transform != SectorTransform.None && key is null && regions.HasEncryptedRegions)
            throw new Ps3Exception(Ps3Error.KeyMissing, "This image is encrypted; its disc key (.dkey) is needed.");
        _inner = inner;
        _leaveOpen = leaveOpen;
        _regions = regions;
        _transform = regions.HasEncryptedRegions ? transform : SectorTransform.None;
        _cipher = _transform == SectorTransform.None ? null : new SectorCipher(key!);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        var length = Length;
        while (buffer.Length > 0 && _position < length)
        {
            var sector = _position / SectorSize;
            var within = (int)(_position % SectorSize);

            // Whole sectors that need no transform are read straight into the caller's buffer.
            if (within == 0 && buffer.Length >= SectorSize && !NeedsTransform(sector))
            {
                var run = PlainRun(sector, Math.Min(buffer.Length, length - _position) / SectorSize);
                if (run > 0)
                {
                    _inner.Position = _position;
                    var read = _inner.ReadAtLeast(buffer[..(int)(run * SectorSize)], (int)(run * SectorSize), throwOnEndOfStream: false);
                    if (read == 0) break;
                    _position += read;
                    total += read;
                    buffer = buffer[read..];
                    continue;
                }
            }

            var available = LoadSector(sector);
            if (available <= within) break;
            var n = Math.Min(available - within, buffer.Length);
            _sector.AsSpan(within, n).CopyTo(buffer);
            _position += n;
            total += n;
            buffer = buffer[n..];
        }
        return total;
    }

    private bool NeedsTransform(long sector) => _cipher is not null && _regions.IsEncrypted(sector);

    /// <summary>How many sectors from <paramref name="sector"/> on (at most <paramref name="max"/>) pass through unchanged.</summary>
    private long PlainRun(long sector, long max)
    {
        if (_cipher is null) return max;
        long n = 0;
        while (n < max && !_regions.IsEncrypted(sector + n)) n++;
        return n;
    }

    /// <summary>Reads (and transforms) one sector into the cache; returns how many bytes of it the image holds.</summary>
    private int LoadSector(long sector)
    {
        if (sector == _cachedSector) return (int)Math.Min(SectorSize, Length - sector * SectorSize);
        _inner.Position = sector * SectorSize;
        var read = _inner.ReadAtLeast(_sector, SectorSize, throwOnEndOfStream: false);
        if (read == SectorSize && NeedsTransform(sector))
        {
            if (_transform == SectorTransform.Decrypt) _cipher!.Decrypt(sector, _sector);
            else _cipher!.Encrypt(sector, _sector);
        }
        _cachedSector = read == SectorSize ? sector : -1;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
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
            _cipher?.Dispose();
            if (!_leaveOpen) _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
