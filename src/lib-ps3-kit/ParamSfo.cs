using System.Buffers.Binary;
using System.Text;

namespace LibPs3Kit;

/// <summary>
/// A PARAM.SFO: the game's title, title ID, version and category. Little-endian: the magic "\0PSF", a version,
/// the key table and data table offsets, the entry count, then 16-byte index entries (key offset, format, used
/// length, max length, data offset). Text values are UTF-8; integer values are 32-bit.
/// </summary>
public sealed class ParamSfo
{
    private const int MaxSize = 1024 * 1024;

    public IReadOnlyDictionary<string, object> Values { get; }

    private ParamSfo(Dictionary<string, object> values) => Values = values;

    public string? Title => Text("TITLE");
    public string? TitleId => Text("TITLE_ID");
    public string? Version => Text("VERSION");
    public string? AppVersion => Text("APP_VER");
    public string? Category => Text("CATEGORY");

    public string? Text(string key) => Values.TryGetValue(key, out var v) ? v as string : null;

    /// <summary>The magic every PARAM.SFO starts with.</summary>
    public static ReadOnlySpan<byte> Magic => "\0PSF"u8;

    public static ParamSfo? TryRead(Stream stream)
    {
        if (stream.Length is < 20 or > MaxSize) return null;
        var data = new byte[stream.Length];
        stream.Position = 0;
        if (stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false) != data.Length) return null;
        return TryParse(data);
    }

    public static ParamSfo? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20 || !data[..4].SequenceEqual(Magic)) return null;
        var keyTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var dataTable = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (count < 0 || count > 1024 || 20 + count * 16 > data.Length || keyTable < 0 || keyTable > data.Length || dataTable < 0 || dataTable > data.Length)
            return null;

        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var e = data.Slice(20 + i * 16, 16);
            var keyAt = keyTable + BinaryPrimitives.ReadUInt16LittleEndian(e);
            var format = BinaryPrimitives.ReadUInt16LittleEndian(e[2..]);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            var at = dataTable + (int)BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
            if (keyAt >= data.Length || at < 0 || length < 0 || at + length > data.Length) return null;
            var keyEnd = data[keyAt..].IndexOf((byte)0);
            if (keyEnd < 0) return null;
            var key = Encoding.ASCII.GetString(data.Slice(keyAt, keyEnd));
            var value = data.Slice(at, length);
            values[key] = format == 0x0404 && length >= 4
                ? BinaryPrimitives.ReadInt32LittleEndian(value)
                : Encoding.UTF8.GetString(value).TrimEnd('\0');
        }
        return new ParamSfo(values);
    }
}
