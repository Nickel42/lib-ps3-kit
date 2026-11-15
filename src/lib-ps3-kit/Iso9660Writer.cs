using System.Buffers.Binary;
using System.Text;

namespace LibPs3Kit;

/// <summary>A file or folder to put on a disc image being built, with its place once laid out.</summary>
internal sealed class IsoNode
{
    public required string Name { get; init; }
    public required string Source { get; init; }
    public required string DiscPath { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime Time { get; init; }
    public List<IsoNode> Children { get; } = [];

    // Filled in by the layout.
    internal byte[] PrimaryName = [], JolietName = [];
    internal string PrimaryPath = "";
    internal List<IsoNode> PrimaryOrder = [], JolietOrder = [];
    internal long Sector, DirSize, JolietSector, JolietDirSize;
    internal int Number, JolietNumber;
    internal IsoNode? Parent;
}

/// <summary>Where everything goes: the metadata sectors (0 up to the first file), then each file at its sector.</summary>
internal sealed record IsoLayout(byte[] Metadata, IReadOnlyList<IsoNode> Files, IReadOnlyList<IsoNode> Folders, long TotalSectors);

/// <summary>
/// Lays out a PS3 disc image for a tree of files: the region table in sector 0 (one plain region: nothing to decrypt),
/// "PlayStation3" and the title ID in sector 1, then ISO 9660 with a primary tree (";1" versions) and a Joliet tree
/// (full names), their path tables, and the files. A file over <c>maxExtent</c> bytes is stored as several extents
/// (multi-extent records), as PS3 discs store files over 4 GiB.
/// </summary>
internal static class Iso9660Writer
{
    private const int S = RegionTable.SectorSize;

    /// <summary>The largest whole-sector length a 32-bit extent length can hold.</summary>
    public const long DefaultMaxExtent = 0xFFFFF800;

    /// <summary>A name's bytes in a folder record can't run past 255 - 33.</summary>
    private const int MaxNameBytes = 222;

    public static IsoLayout Build(IsoNode root, string? titleId, long maxExtent, DateTime now)
    {
        if (maxExtent <= 0 || maxExtent % S != 0) throw new ArgumentOutOfRangeException(nameof(maxExtent));
        Prepare(root, null);
        var primaryDirs = PathOrder(root, joliet: false);
        var jolietDirs = PathOrder(root, joliet: true);
        for (var i = 0; i < primaryDirs.Count; i++) primaryDirs[i].Number = i + 1;
        for (var i = 0; i < jolietDirs.Count; i++) jolietDirs[i].JolietNumber = i + 1;

        var pathTableSize = PathTableSize(primaryDirs, joliet: false);
        var jolietTableSize = PathTableSize(jolietDirs, joliet: true);
        long next = 19;
        long primaryL = next; next += Sectors(pathTableSize);
        long primaryM = next; next += Sectors(pathTableSize);
        long jolietL = next; next += Sectors(jolietTableSize);
        long jolietM = next; next += Sectors(jolietTableSize);
        foreach (var d in primaryDirs) { d.DirSize = PackDirectory(d, false, maxExtent, []); d.Sector = next; next += d.DirSize / S; }
        foreach (var d in jolietDirs) { d.JolietDirSize = PackDirectory(d, true, maxExtent, []); d.JolietSector = next; next += d.JolietDirSize / S; }
        var dataStart = next;
        var files = new List<IsoNode>();
        foreach (var d in primaryDirs)
            foreach (var f in d.PrimaryOrder.Where(c => !c.IsDirectory))
            {
                f.Sector = f.Size > 0 ? next : 0;
                next += Sectors(f.Size);
                files.Add(f);
            }
        var total = next;
        if (total - 1 > uint.MaxValue) throw new Ps3Exception(Ps3Error.BadFilesystem, "The folder is too large for a disc image.");

        var meta = new byte[dataStart * S];
        var span = meta.AsSpan();
        // Sector 0: one plain region over the whole image.
        BinaryPrimitives.WriteUInt32BigEndian(span, 1);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], (uint)(total - 1));
        // Sector 1: the disc header and its product code.
        "PlayStation3"u8.CopyTo(span[S..]);
        var id = Encoding.ASCII.GetBytes((titleId ?? "").PadRight(16)[..16]);
        id.CopyTo(span[(S + 16)..]);

        Descriptor(span.Slice(16 * S, S), false, total, pathTableSize, primaryL, primaryM, root, now);
        Descriptor(span.Slice(17 * S, S), true, total, jolietTableSize, jolietL, jolietM, root, now);
        var end = span.Slice(18 * S, S);
        end[0] = 255;
        "CD001"u8.CopyTo(end[1..]);
        end[6] = 1;

        WritePathTable(span[(int)(primaryL * S)..], primaryDirs, false, littleEndian: true);
        WritePathTable(span[(int)(primaryM * S)..], primaryDirs, false, littleEndian: false);
        WritePathTable(span[(int)(jolietL * S)..], jolietDirs, true, littleEndian: true);
        WritePathTable(span[(int)(jolietM * S)..], jolietDirs, true, littleEndian: false);
        foreach (var d in primaryDirs) PackDirectory(d, false, maxExtent, span.Slice((int)(d.Sector * S), (int)d.DirSize));
        foreach (var d in jolietDirs) PackDirectory(d, true, maxExtent, span.Slice((int)(d.JolietSector * S), (int)d.JolietDirSize));
        return new IsoLayout(meta, files, primaryDirs, total);
    }

    private static long Sectors(long bytes) => (bytes + S - 1) / S;

    /// <summary>Each node's names as stored (checked to fit), its parent, and its children in each tree's order.</summary>
    private static void Prepare(IsoNode node, IsoNode? parent)
    {
        node.Parent = parent;
        if (parent is not null)
        {
            var ascii = new string(node.Name.Select(c => c is > ' ' and < (char)127 and not ';' ? char.ToUpperInvariant(c) : '_').ToArray());
            node.PrimaryName = Encoding.ASCII.GetBytes(node.IsDirectory ? ascii : ascii + ";1");
            node.JolietName = Encoding.BigEndianUnicode.GetBytes(node.Name);
            if (node.JolietName.Length > MaxNameBytes || node.PrimaryName.Length > MaxNameBytes)
                throw new Ps3Exception(Ps3Error.BadFilesystem, $"{node.DiscPath}: the name is too long for a disc image.");
            node.PrimaryPath = parent.Parent is null ? ascii : parent.PrimaryPath + "/" + ascii;
        }
        foreach (var child in node.Children) Prepare(child, node);
        node.PrimaryOrder = [.. node.Children.OrderBy(c => Encoding.ASCII.GetString(c.PrimaryName), StringComparer.Ordinal)];
        node.JolietOrder = [.. node.Children.OrderBy(c => c.Name, StringComparer.Ordinal)];
        var clash = node.PrimaryOrder.GroupBy(c => Encoding.ASCII.GetString(c.PrimaryName)).FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
            throw new Ps3Exception(Ps3Error.BadFilesystem, $"{string.Join(" and ", clash.Select(c => c.DiscPath))} would have the same name on the disc.");
    }

    /// <summary>Folders in path-table order: by level, then by parent's number, then by name. The root is first.</summary>
    private static List<IsoNode> PathOrder(IsoNode root, bool joliet)
    {
        var order = new List<IsoNode> { root };
        for (var i = 0; i < order.Count; i++)
            order.AddRange((joliet ? order[i].JolietOrder : order[i].PrimaryOrder).Where(c => c.IsDirectory));
        return order;
    }

    private static int NameLength(IsoNode d, bool joliet) => d.Parent is null ? 1 : (joliet ? d.JolietName : d.PrimaryName).Length;

    private static int PathTableSize(List<IsoNode> dirs, bool joliet) =>
        dirs.Sum(d => 8 + NameLength(d, joliet) + NameLength(d, joliet) % 2);

    private static void WritePathTable(Span<byte> at, List<IsoNode> dirs, bool joliet, bool littleEndian)
    {
        var p = 0;
        foreach (var d in dirs)
        {
            var name = d.Parent is null ? [0] : joliet ? d.JolietName : d.PrimaryName;
            at[p] = (byte)name.Length;
            at[p + 1] = 0;
            var sector = (uint)(joliet ? d.JolietSector : d.Sector);
            var parent = (ushort)(d.Parent is null ? 1 : joliet ? d.Parent.JolietNumber : d.Parent.Number);
            if (littleEndian)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(at[(p + 2)..], sector);
                BinaryPrimitives.WriteUInt16LittleEndian(at[(p + 6)..], parent);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(at[(p + 2)..], sector);
                BinaryPrimitives.WriteUInt16BigEndian(at[(p + 6)..], parent);
            }
            name.CopyTo(at[(p + 8)..]);
            p += 8 + name.Length + name.Length % 2;
        }
    }

    /// <summary>
    /// A folder's records (".", "..", then its entries, a file over <paramref name="maxExtent"/> as several), none
    /// crossing a sector. Writes them when <paramref name="dest"/> isn't empty; returns the size in whole sectors.
    /// </summary>
    private static long PackDirectory(IsoNode dir, bool joliet, long maxExtent, Span<byte> dest)
    {
        long p = 0;
        void Add(long sector, long length, DateTime time, byte flags, ReadOnlySpan<byte> name, Span<byte> to)
        {
            var len = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
            if (p % S + len > S) p = (p / S + 1) * S;
            if (!to.IsEmpty) Record(to.Slice((int)p, len), sector, length, time, flags, name);
            p += len;
        }
        var parent = dir.Parent ?? dir;
        Add(joliet ? dir.JolietSector : dir.Sector, joliet ? dir.JolietDirSize : dir.DirSize, dir.Time, 2, [0], dest);
        Add(joliet ? parent.JolietSector : parent.Sector, joliet ? parent.JolietDirSize : parent.DirSize, parent.Time, 2, [1], dest);
        foreach (var c in joliet ? dir.JolietOrder : dir.PrimaryOrder)
        {
            var name = joliet ? c.JolietName : c.PrimaryName;
            if (c.IsDirectory)
            {
                Add(joliet ? c.JolietSector : c.Sector, joliet ? c.JolietDirSize : c.DirSize, c.Time, 2, name, dest);
                continue;
            }
            var extents = Math.Max(1, (c.Size + maxExtent - 1) / maxExtent);
            for (long k = 0; k < extents; k++)
            {
                var length = Math.Min(maxExtent, c.Size - k * maxExtent);
                var sector = c.Size > 0 ? c.Sector + k * (maxExtent / S) : 0;
                Add(sector, length, c.Time, k < extents - 1 ? (byte)0x80 : (byte)0, name, dest);
            }
        }
        return Math.Max(1, Sectors(p)) * S;
    }

    private static void Record(Span<byte> r, long sector, long length, DateTime time, byte flags, ReadOnlySpan<byte> name)
    {
        r[0] = (byte)r.Length;
        Both32(r[2..], (uint)sector);
        Both32(r[10..], (uint)length);
        var t = time.ToUniversalTime();
        r[18] = (byte)Math.Clamp(t.Year - 1900, 0, 255);
        r[19] = (byte)t.Month; r[20] = (byte)t.Day; r[21] = (byte)t.Hour; r[22] = (byte)t.Minute; r[23] = (byte)t.Second;
        r[25] = flags;
        Both16(r[28..], 1);
        r[32] = (byte)name.Length;
        name.CopyTo(r[33..]);
    }

    private static void Descriptor(Span<byte> d, bool joliet, long total, int pathTableSize, long l, long m, IsoNode root, DateTime now)
    {
        d[0] = joliet ? (byte)2 : (byte)1;
        "CD001"u8.CopyTo(d[1..]);
        d[6] = 1;
        Text(d.Slice(8, 32), "", joliet);
        Text(d.Slice(40, 32), "PS3VOLUME", joliet);
        Both32(d[80..], (uint)total);
        if (joliet) { d[88] = 0x25; d[89] = 0x2F; d[90] = 0x45; }
        Both16(d[120..], 1);
        Both16(d[124..], 1);
        Both16(d[128..], S);
        Both32(d[132..], (uint)pathTableSize);
        BinaryPrimitives.WriteUInt32LittleEndian(d[140..], (uint)l);
        BinaryPrimitives.WriteUInt32BigEndian(d[148..], (uint)m);
        Record(d.Slice(156, 34), joliet ? root.JolietSector : root.Sector, joliet ? root.JolietDirSize : root.DirSize, now, 2, [0]);
        Text(d.Slice(190, 128), "", joliet);
        Text(d.Slice(318, 128), "", joliet);
        Text(d.Slice(446, 128), "", joliet);
        Text(d.Slice(574, 128), "", joliet);
        Text(d.Slice(702, 37), "", joliet);
        Text(d.Slice(739, 37), "", joliet);
        Text(d.Slice(776, 37), "", joliet);
        var stamp = Encoding.ASCII.GetBytes(now.ToUniversalTime().ToString("yyyyMMddHHmmss") + "00");
        stamp.CopyTo(d[813..]);
        stamp.CopyTo(d[830..]);
        "0000000000000000"u8.CopyTo(d[847..]);
        "0000000000000000"u8.CopyTo(d[864..]);
        d[881] = 1;
    }

    /// <summary>A text field padded with spaces: ASCII, or UCS-2 big-endian in the Joliet descriptor.</summary>
    private static void Text(Span<byte> field, string text, bool joliet)
    {
        if (joliet)
        {
            var chars = field.Length / 2;
            Encoding.BigEndianUnicode.GetBytes(text.PadRight(chars)[..chars]).CopyTo(field);
        }
        else Encoding.ASCII.GetBytes(text.PadRight(field.Length)[..field.Length]).CopyTo(field);
    }

    private static void Both16(Span<byte> at, ushort v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(at, v);
        BinaryPrimitives.WriteUInt16BigEndian(at[2..], v);
    }

    private static void Both32(Span<byte> at, uint v)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(at, v);
        BinaryPrimitives.WriteUInt32BigEndian(at[4..], v);
    }
}
