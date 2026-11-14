using System.Text;

namespace LibPs3Kit;

/// <summary>Whether a PS3 image's encrypted regions hold ciphertext.</summary>
public enum EncryptionState
{
    /// <summary>As Redump dumps it: the encrypted regions need the disc key.</summary>
    Encrypted,
    /// <summary>Decrypted already (PS3Dec, 3k3y, or this library): nothing left to decrypt.</summary>
    Decrypted,
    /// <summary>The region table lists no encrypted region (an image rebuilt from a folder, say).</summary>
    NoEncryptedRegions,
    /// <summary>No file in an encrypted region could tell (none with a known header was found).</summary>
    Unknown,
}

/// <summary>
/// A PS3 disc image (.iso): its region table, the title ID in sector 1, its filesystem and PARAM.SFO, and whether
/// it's still encrypted. Opening reads a few sectors only.
/// </summary>
public sealed class Ps3Disc : IDisposable
{
    private const int SectorSize = RegionTable.SectorSize;

    /// <summary>Sector 1 of a PS3 disc starts with this, followed by the product code.</summary>
    private static ReadOnlySpan<byte> DiscMagic => "PlayStation3"u8;

    /// <summary>3k3y marks the images it decrypts with this at offset 0xF70 (in sector 1).</summary>
    private static ReadOnlySpan<byte> Decrypted3k3y => "Decrypted 3K BLD"u8;

    private readonly Stream _stream;

    public string Path { get; }
    public RegionTable Regions { get; }
    public long Length => _stream.Length;

    /// <summary>The product code in sector 1 ("BLUS-30443"), or null.</summary>
    public string? DiscTitleId { get; }

    /// <summary>The filesystem as stored (read without decrypting: its folders sit in the first plain region).</summary>
    public Iso9660Volume FileSystem { get; }

    public EncryptionState State { get; }

    private Ps3Disc(string path, Stream stream, RegionTable regions, string? titleId, Iso9660Volume fs)
    {
        Path = path;
        _stream = stream;
        Regions = regions;
        DiscTitleId = titleId;
        FileSystem = fs;
        State = FindState();
    }

    /// <summary>Whether <paramref name="path"/> is a PS3 disc image this library can open.</summary>
    public static bool IsPs3Image(string path)
    {
        try
        {
            using var disc = Open(path);
            return true;
        }
        catch (Exception ex) when (ex is Ps3Exception or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Opens a PS3 disc image. Throws <see cref="Ps3Exception"/> (<see cref="Ps3Error.NotPs3Disc"/>) for anything else.</summary>
    public static Ps3Disc Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try
        {
            var head = new byte[SectorSize * 2];
            if (!Iso9660Volume.ReadAt(stream, 0, head)) throw NotPs3(path);
            var fs = Iso9660Volume.TryOpen(stream) ?? throw NotPs3(path);
            var sectors = stream.Length / SectorSize;
            var magic = head.AsSpan(SectorSize, DiscMagic.Length).SequenceEqual(DiscMagic);
            var table = RegionTable.TryParse(head.AsSpan(0, SectorSize), sectors);
            var hasGame = fs.RootEntries().Any(e => e.Name.Equals("PS3_DISC.SFB", StringComparison.OrdinalIgnoreCase)
                                                   || (e.IsDirectory && e.Name.Equals("PS3_GAME", StringComparison.OrdinalIgnoreCase)));
            if (!magic && !hasGame) throw NotPs3(path);
            string? titleId = null;
            if (magic)
            {
                var id = Encoding.ASCII.GetString(head, SectorSize + 16, 16).TrimEnd('\0', ' ');
                titleId = id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? id : null;
            }
            return new Ps3Disc(path, stream, table ?? RegionTable.AllPlain(sectors), titleId, fs);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static Ps3Exception NotPs3(string path) =>
        new(Ps3Error.NotPs3Disc, $"{System.IO.Path.GetFileName(path)} isn't a PS3 disc image.");

    /// <summary>
    /// A view of the image that decrypts as it reads. <paramref name="key"/> may be null when the image is already
    /// decrypted (or has nothing encrypted). The view shares this disc's file handle: don't use two at once.
    /// </summary>
    public Ps3DiscStream OpenDecrypted(DiscKey? key)
    {
        if (State == EncryptionState.Encrypted && key is null)
            throw new Ps3Exception(Ps3Error.KeyMissing, "This image is encrypted; its disc key (.dkey) is needed.");
        var transform = State is EncryptionState.Encrypted or EncryptionState.Unknown && key is not null ? SectorTransform.Decrypt : SectorTransform.None;
        return new Ps3DiscStream(_stream, Regions, key, transform, leaveOpen: true);
    }

    /// <summary>A view of the image that encrypts the encrypted regions as it reads (for a decrypted image).</summary>
    public Ps3DiscStream OpenEncrypted(DiscKey key) => new(_stream, Regions, key, SectorTransform.Encrypt, leaveOpen: true);

    /// <summary>The disc's PARAM.SFO (PS3_GAME/PARAM.SFO), read through the decrypting view when it's encrypted.</summary>
    public ParamSfo? ReadParamSfo(DiscKey? key = null)
    {
        var entry = FileSystem.Find("PS3_GAME/PARAM.SFO");
        if (entry is null || entry.IsDirectory) return null;
        if (State == EncryptionState.Encrypted && key is null && entry.Extents.Any(e => Regions.IsEncrypted(e.Sector))) return null;
        // Without a key, an encrypted image's PARAM.SFO is read as stored (it was just checked to sit in a plain region).
        using var view = State == EncryptionState.Encrypted && key is null
            ? new Ps3DiscStream(_stream, Regions, null, SectorTransform.None, leaveOpen: true)
            : OpenDecrypted(State == EncryptionState.Encrypted ? key : null);
        var fs = Iso9660Volume.TryOpen(view);
        var file = fs?.Find("PS3_GAME/PARAM.SFO");
        if (fs is null || file is null) return null;
        using var s = fs.OpenFile(file);
        return ParamSfo.TryRead(s);
    }

    /// <summary>
    /// Whether <paramref name="key"/> decrypts this image: files in encrypted regions with a known header (SELF,
    /// SFO, PNG, SDAT) start with it once decrypted. Null when no such file was found, so it can't be told.
    /// </summary>
    public bool? KeyFits(DiscKey key)
    {
        var probes = Probes();
        if (probes.Count == 0) return null;
        using var cipher = new SectorCipher(key);
        var sector = new byte[SectorSize];
        foreach (var (entry, magic) in probes)
        {
            if (!Iso9660Volume.ReadAt(_stream, entry.FirstSector * SectorSize, sector)) continue;
            cipher.Decrypt(entry.FirstSector, sector);
            if (sector.AsSpan().StartsWith(magic)) return true;
        }
        return false;
    }

    private EncryptionState FindState()
    {
        if (!Regions.HasEncryptedRegions) return EncryptionState.NoEncryptedRegions;
        var head = new byte[SectorSize * 2];
        if (Iso9660Volume.ReadAt(_stream, 0, head) && head.AsSpan(0xF70).StartsWith(Decrypted3k3y)) return EncryptionState.Decrypted;
        var probes = Probes();
        if (probes.Count == 0) return EncryptionState.Unknown;
        var magicBuffer = new byte[8];
        foreach (var (entry, magic) in probes)
            if (Iso9660Volume.ReadAt(_stream, entry.FirstSector * SectorSize, magicBuffer) && magicBuffer.AsSpan().StartsWith(magic))
                return EncryptionState.Decrypted;
        return EncryptionState.Encrypted;
    }

    private List<(IsoEntry Entry, byte[] Magic)>? _probes;

    /// <summary>
    /// Files that start in an encrypted region and whose header is known. PS3_GAME/USRDIR/EBOOT.BIN alone when it
    /// qualifies (a few folder reads); otherwise up to eight found by walking the whole tree.
    /// </summary>
    private List<(IsoEntry Entry, byte[] Magic)> Probes()
    {
        if (_probes is not null) return _probes;
        bool Usable(IsoEntry e) => !e.IsDirectory && e.Size >= 8 && e.FirstSector >= 0 && Regions.IsEncrypted(e.FirstSector) && MagicFor(e.Name) is not null;
        var list = new List<(IsoEntry, byte[])>();
        try
        {
            if (FileSystem.Find("PS3_GAME/USRDIR/EBOOT.BIN") is { } eboot && Usable(eboot))
                list.Add((eboot, MagicFor(eboot.Name)!));
            else
                foreach (var e in FileSystem.Entries().Where(Usable).Take(8))
                    list.Add((e, MagicFor(e.Name)!));
        }
        catch (Ps3Exception) { }
        return _probes = list;
    }

    private static byte[]? MagicFor(string name)
    {
        var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        if (name.Equals("EBOOT.BIN", StringComparison.OrdinalIgnoreCase) || ext is ".self" or ".sprx") return "SCE\0"u8.ToArray();
        if (ext == ".sfo") return ParamSfo.Magic.ToArray();
        if (ext == ".png") return [0x89, (byte)'P', (byte)'N', (byte)'G'];
        if (ext is ".sdat" or ".edat") return "NPD\0"u8.ToArray();
        return null;
    }

    public void Dispose() => _stream.Dispose();
}
