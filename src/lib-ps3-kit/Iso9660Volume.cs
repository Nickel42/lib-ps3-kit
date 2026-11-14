using System.Buffers.Binary;
using System.Text;

namespace LibPs3Kit;

/// <summary>One piece of a file on disc: <see cref="Length"/> bytes from sector <see cref="Sector"/>.</summary>
public readonly record struct FileExtent(long Sector, long Length);

/// <summary>
/// A file or folder in an ISO 9660 filesystem. <see cref="Path"/> uses '/' and has no leading slash. A file over
/// 4 GiB is stored as several extents (multi-extent records); its bytes are the extents one after another.
/// </summary>
public sealed record IsoEntry(string Path, bool IsDirectory, long Size, IReadOnlyList<FileExtent> Extents)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>The first sector of the file's data, or -1 for an empty file or a folder.</summary>
    public long FirstSector => Extents.Count > 0 && Extents[0].Length > 0 ? Extents[0].Sector : -1;
}

/// <summary>
/// A read-only ISO 9660 reader for PS3 discs: the Joliet tree when the disc has one (full-length names), else the
/// primary one (";1" versions stripped). It reads multi-extent files, skips the self and parent records, and refuses
/// names that would be unsafe to extract (path separators, "..", control characters).
/// </summary>
public sealed class Iso9660Volume
{
    private const int SectorSize = RegionTable.SectorSize;
    private const int MaxDepth = 64;
    private const long MaxDirectorySize = 64L * 1024 * 1024;

    private readonly Stream _stream;
    private readonly bool _joliet;
    private readonly (long Sector, long Size) _root;

    /// <summary>The primary volume descriptor's volume identifier ("PS3VOLUME" on PS3 discs).</summary>
    public string VolumeId { get; }

    /// <summary>Whether names come from the Joliet tree.</summary>
    public bool IsJoliet => _joliet;

    private Iso9660Volume(Stream stream, bool joliet, (long, long) root, string volumeId)
    {
        _stream = stream;
        _joliet = joliet;
        _root = root;
        VolumeId = volumeId;
    }

    /// <summary>Opens the filesystem on <paramref name="stream"/>, or returns null when it has none.</summary>
    public static Iso9660Volume? TryOpen(Stream stream)
    {
        var sector = new byte[SectorSize];
        (long, long)? primary = null, joliet = null;
        var volumeId = "";
        for (long lba = 16; lba < 16 + 32; lba++)
        {
            if (!ReadAt(stream, lba * SectorSize, sector)) return null;
            if (!sector.AsSpan(1, 5).SequenceEqual("CD001"u8)) return null;
            var type = sector[0];
            if (type == 255) break;
            if (type == 1 && primary is null)
            {
                primary = RootOf(sector);
                volumeId = Encoding.ASCII.GetString(sector, 40, 32).Trim();
            }
            else if (type == 2 && sector[88] == 0x25 && sector[89] == 0x2F && sector[90] is 0x40 or 0x43 or 0x45)
                joliet ??= RootOf(sector);
        }
        if (primary is null) return null;
        return new Iso9660Volume(stream, joliet is not null, joliet ?? primary.Value, volumeId);
    }

    private static (long, long) RootOf(byte[] descriptor) =>
        (BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(156 + 2)), BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(156 + 10)));

    /// <summary>Every file and folder, folders before their contents, in disc order within each folder.</summary>
    public IReadOnlyList<IsoEntry> Entries()
    {
        var result = new List<IsoEntry>();
        var visited = new HashSet<long>();
        Walk(_root.Sector, _root.Size, "", 0, result, visited);
        return result;
    }

    /// <summary>The entries directly in the root folder.</summary>
    public IReadOnlyList<IsoEntry> RootEntries() => ReadDirectory(_root.Sector, _root.Size, "");

    /// <summary>The entry at <paramref name="path"/> ('/'-separated, case-insensitive), or null.</summary>
    public IsoEntry? Find(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        (long Sector, long Size) dir = _root;
        var prefix = "";
        for (var i = 0; i < parts.Length; i++)
        {
            var match = ReadDirectory(dir.Sector, dir.Size, prefix).FirstOrDefault(e => e.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            if (i == parts.Length - 1) return match;
            if (!match.IsDirectory) return null;
            dir = (match.Extents[0].Sector, match.Extents[0].Length);
            prefix = match.Path;
        }
        return null;
    }

    private void Walk(long sector, long size, string prefix, int depth, List<IsoEntry> result, HashSet<long> visited)
    {
        if (depth > MaxDepth) throw new Ps3Exception(Ps3Error.BadFilesystem, "The filesystem's folders nest too deeply.");
        if (!visited.Add(sector)) throw new Ps3Exception(Ps3Error.BadFilesystem, "The filesystem's folders loop back on themselves.");
        foreach (var entry in ReadDirectory(sector, size, prefix))
        {
            result.Add(entry);
            if (entry.IsDirectory) Walk(entry.Extents[0].Sector, entry.Extents[0].Length, entry.Path, depth + 1, result, visited);
        }
    }

    private List<IsoEntry> ReadDirectory(long sector, long size, string prefix)
    {
        if (size <= 0 || size > MaxDirectorySize) throw new Ps3Exception(Ps3Error.BadFilesystem, $"A folder record has an impossible size ({size} bytes).");
        var data = new byte[size];
        if (!ReadAt(_stream, sector * SectorSize, data))
            throw new Ps3Exception(Ps3Error.BadFilesystem, "A folder lies past the end of the image.");

        var entries = new List<IsoEntry>();
        string? pendingName = null;
        var pendingExtents = new List<FileExtent>();
        var p = 0;
        while (p < data.Length)
        {
            int length = data[p];
            if (length == 0)
            {
                // Records never cross a sector boundary: the rest of this sector is padding.
                p = (p / SectorSize + 1) * SectorSize;
                continue;
            }
            if (length < 34 || p + length > data.Length) throw new Ps3Exception(Ps3Error.BadFilesystem, "A folder record is damaged.");
            var record = data.AsSpan(p, length);
            int nameLength = record[32];
            if (33 + nameLength > length) throw new Ps3Exception(Ps3Error.BadFilesystem, "A folder record's name runs past it.");
            var rawName = record.Slice(33, nameLength);
            p += length;
            if (nameLength == 1 && rawName[0] is 0 or 1) continue; // "." and ".."

            var flags = record[25];
            var extent = new FileExtent(BinaryPrimitives.ReadUInt32LittleEndian(record[2..]), BinaryPrimitives.ReadUInt32LittleEndian(record[10..]));
            var name = DecodeName(rawName);
            var isDirectory = (flags & 0x02) != 0;
            var more = (flags & 0x80) != 0;

            if (pendingName is not null && pendingName != name)
                throw new Ps3Exception(Ps3Error.BadFilesystem, $"The file {pendingName} ends without its last extent.");
            pendingExtents.Add(extent);
            if (more && !isDirectory)
            {
                pendingName = name;
                continue;
            }
            var path = prefix.Length == 0 ? name : prefix + "/" + name;
            entries.Add(new IsoEntry(path, isDirectory, isDirectory ? 0 : pendingExtents.Sum(e => e.Length), pendingExtents.ToArray()));
            pendingName = null;
            pendingExtents.Clear();
        }
        if (pendingName is not null) throw new Ps3Exception(Ps3Error.BadFilesystem, $"The file {pendingName} ends without its last extent.");
        return entries;
    }

    private string DecodeName(ReadOnlySpan<byte> raw)
    {
        var name = _joliet ? Encoding.BigEndianUnicode.GetString(raw) : Encoding.ASCII.GetString(raw);
        var semicolon = name.LastIndexOf(';');
        if (semicolon > 0) name = name[..semicolon];
        if (!_joliet && name.EndsWith('.')) name = name[..^1];
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || name.Any(char.IsControl))
            throw new Ps3Exception(Ps3Error.BadFilesystem, $"The filesystem holds a name that can't be extracted safely: \"{name}\".");
        return name;
    }

    /// <summary>A stream over one file's bytes (its extents in order).</summary>
    public Stream OpenFile(IsoEntry entry)
    {
        if (entry.IsDirectory) throw new ArgumentException("A folder has no data to read.", nameof(entry));
        return new ExtentStream(_stream, entry.Extents);
    }

    internal static bool ReadAt(Stream s, long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset + buffer.Length > s.Length) return false;
        s.Position = offset;
        return s.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
    }

    /// <summary>Reads a file's extents one after another.</summary>
    private sealed class ExtentStream(Stream inner, IReadOnlyList<FileExtent> extents) : Stream
    {
        private readonly long _length = extents.Sum(e => e.Length);
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, _length); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var total = 0;
            while (buffer.Length > 0 && _position < _length)
            {
                long start = 0;
                foreach (var e in extents)
                {
                    if (_position < start + e.Length)
                    {
                        var within = _position - start;
                        var n = (int)Math.Min(buffer.Length, e.Length - within);
                        inner.Position = e.Sector * SectorSize + within;
                        var read = inner.ReadAtLeast(buffer[..n], n, throwOnEndOfStream: false);
                        if (read < n) throw new Ps3Exception(Ps3Error.BadFilesystem, "A file runs past the end of the image.");
                        _position += n;
                        total += n;
                        buffer = buffer[n..];
                        break;
                    }
                    start += e.Length;
                }
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => _length + offset };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
