using System.Buffers.Binary;

namespace LibPs3Kit;

/// <summary>A run of sectors, plain or encrypted, from <see cref="FirstSector"/> to <see cref="LastSector"/> inclusive.</summary>
public readonly record struct DiscRegion(long FirstSector, long LastSector, bool Encrypted)
{
    public long SectorCount => LastSector - FirstSector + 1;
}

/// <summary>
/// The region table in sector 0 of a PS3 disc image. A big-endian count N of plain regions comes first, then
/// four bytes, then 2N big-endian sector numbers v0..v(2N-1). Plain region k runs from v(2k) to v(2k+1)
/// inclusive; the encrypted region between two plain ones runs from v(2k+1)+1 to v(2k+2)-1. This is how RPCS3
/// and PS3Dec read it. Sectors past the table's last plain region are plain.
/// </summary>
public sealed class RegionTable
{
    public const int SectorSize = 2048;

    /// <summary>A sane upper bound; real discs have a handful of regions.</summary>
    private const int MaxPlainRegions = 128;

    public IReadOnlyList<DiscRegion> Regions { get; }

    private readonly DiscRegion[] _encrypted;

    private RegionTable(List<DiscRegion> regions)
    {
        Regions = regions;
        _encrypted = regions.Where(r => r.Encrypted).ToArray();
    }

    /// <summary>The encrypted regions only.</summary>
    public IReadOnlyList<DiscRegion> EncryptedRegions => _encrypted;

    public bool HasEncryptedRegions => _encrypted.Length > 0;

    /// <summary>Whether sector <paramref name="sector"/> is in an encrypted region.</summary>
    public bool IsEncrypted(long sector)
    {
        // A binary search over the few encrypted regions.
        int lo = 0, hi = _encrypted.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var r = _encrypted[mid];
            if (sector < r.FirstSector) hi = mid - 1;
            else if (sector > r.LastSector) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="sector"/> is encrypted, and the last sector of the run it starts that is the same
    /// (encrypted or plain), so a reader can take the whole run at once. Past the table, everything is plain.
    /// </summary>
    internal (bool Encrypted, long RunEnd) RunAt(long sector)
    {
        int lo = 0, hi = _encrypted.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var r = _encrypted[mid];
            if (sector < r.FirstSector) hi = mid - 1;
            else if (sector > r.LastSector) lo = mid + 1;
            else return (true, r.LastSector);
        }
        // Plain up to the next encrypted region (lo is the first one that starts after the sector).
        return (false, lo < _encrypted.Length ? _encrypted[lo].FirstSector - 1 : long.MaxValue);
    }

    /// <summary>
    /// Parses sector 0. Returns null when it doesn't hold a valid table (not a PS3 image): the count is 0 or
    /// implausible, or the sector numbers don't rise.
    /// </summary>
    public static RegionTable? TryParse(ReadOnlySpan<byte> sector0, long imageSectors)
    {
        if (sector0.Length < SectorSize) return null;
        var plainCount = BinaryPrimitives.ReadUInt32BigEndian(sector0);
        if (plainCount == 0 || plainCount > MaxPlainRegions) return null;
        var values = new long[plainCount * 2];
        for (var i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadUInt32BigEndian(sector0[(8 + i * 4)..]);
        if (values[0] != 0) return null;

        var regions = new List<DiscRegion>();
        for (var k = 0; k < plainCount; k++)
        {
            long first = values[2 * k], last = values[2 * k + 1];
            if (last < first) return null;
            if (k > 0)
            {
                var gapFirst = regions[^1].LastSector + 1;
                if (first <= gapFirst) return null; // an encrypted region must hold at least one sector
                regions.Add(new DiscRegion(gapFirst, first - 1, true));
            }
            regions.Add(new DiscRegion(first, last, false));
        }
        if (imageSectors > 0 && regions[^1].LastSector >= imageSectors)
        {
            // A table that runs past the image's end: the image is cut short. Keep what lies inside it.
            regions = regions.Where(r => r.FirstSector < imageSectors)
                .Select(r => r with { LastSector = Math.Min(r.LastSector, imageSectors - 1) }).ToList();
        }
        else if (imageSectors > 0 && regions[^1].LastSector < imageSectors - 1)
            regions.Add(new DiscRegion(regions[^1].LastSector + 1, imageSectors - 1, false));
        return new RegionTable(regions);
    }

    /// <summary>A table with no encrypted regions, for an image that has nothing to decrypt.</summary>
    internal static RegionTable AllPlain(long imageSectors) =>
        new([new DiscRegion(0, Math.Max(0, imageSectors - 1), false)]);
}
