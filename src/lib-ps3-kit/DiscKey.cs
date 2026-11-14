using System.Globalization;

namespace LibPs3Kit;

/// <summary>
/// A PS3 disc's 128-bit decryption key, as Redump publishes it: a <c>.dkey</c> file holding 32 hex digits
/// (the "Disc Key" on a Redump page), or the 16 raw bytes.
/// </summary>
public sealed class DiscKey
{
    public const int Size = 16;

    private readonly byte[] _bytes;

    private DiscKey(byte[] bytes) => _bytes = bytes;

    /// <summary>The key's bytes (a copy).</summary>
    public byte[] ToArray() => (byte[])_bytes.Clone();

    internal ReadOnlySpan<byte> Bytes => _bytes;

    public override string ToString() => Convert.ToHexString(_bytes);

    /// <summary>A key from 16 bytes.</summary>
    public static DiscKey FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size) throw new Ps3Exception(Ps3Error.BadKey, $"A disc key is {Size} bytes, not {bytes.Length}.");
        return new DiscKey(bytes.ToArray());
    }

    /// <summary>A key from 32 hex digits; spaces, dashes and a 0x prefix are ignored.</summary>
    public static DiscKey Parse(string text)
    {
        if (TryParse(text, out var key)) return key;
        throw new Ps3Exception(Ps3Error.BadKey, "A disc key is 32 hex digits (0-9, A-F).");
    }

    public static bool TryParse(string? text, out DiscKey key)
    {
        key = null!;
        if (text is null) return false;
        var hex = new string(text.Trim().Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        if (hex.Length != Size * 2 || !hex.All(char.IsAsciiHexDigit)) return false;
        var bytes = new byte[Size];
        for (var i = 0; i < Size; i++) bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        key = new DiscKey(bytes);
        return true;
    }

    /// <summary>Reads a .dkey file: 32 hex digits as text, or exactly 16 raw bytes.</summary>
    public static DiscKey Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new Ps3Exception(Ps3Error.BadKey, $"No key file at {path}.");
        if (info.Length > 4096) throw new Ps3Exception(Ps3Error.BadKey, $"{info.Name} is too large to be a disc key.");
        var bytes = File.ReadAllBytes(path);
        if (TryParse(System.Text.Encoding.ASCII.GetString(bytes), out var key)) return key;
        if (bytes.Length == Size) return new DiscKey(bytes);
        throw new Ps3Exception(Ps3Error.BadKey, $"{info.Name} doesn't hold a disc key (32 hex digits or 16 bytes).");
    }

    /// <summary>
    /// The key file for <paramref name="isoPath"/>: <c>Game.dkey</c> (or <c>Game.key</c>) beside it, then in each of
    /// <paramref name="folders"/>. Null when there's none.
    /// </summary>
    public static string? FindFileFor(string isoPath, IEnumerable<string>? folders = null)
    {
        var name = Path.GetFileNameWithoutExtension(isoPath);
        var places = new List<string>();
        if (Path.GetDirectoryName(Path.GetFullPath(isoPath)) is { } beside) places.Add(beside);
        if (folders is not null) places.AddRange(folders.Where(f => !string.IsNullOrWhiteSpace(f)));
        foreach (var folder in places)
            foreach (var ext in new[] { ".dkey", ".key" })
            {
                var candidate = Path.Combine(folder, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}
