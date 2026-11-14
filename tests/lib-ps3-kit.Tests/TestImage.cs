using System.Buffers.Binary;
using System.Text;

namespace LibPs3Kit.Tests;

/// <summary>
/// A small synthetic PS3 disc image: region table in sector 0, the "PlayStation3" header in sector 1, an ISO 9660
/// primary tree (and a Joliet one), and files placed so that EBOOT.BIN and a two-extent file sit in the encrypted
/// region while PS3_DISC.SFB, PARAM.SFO and PS3_UPDATE sit in plain ones.
/// </summary>
internal sealed class TestImage
{
    public const int S = 2048;
    public const int Sectors = 64;
    public static readonly DiscKey Key = DiscKey.Parse("00112233445566778899AABBCCDDEEFF");

    public byte[] Plain { get; } = new byte[Sectors * S];
    public byte[] Encrypted { get; private set; } = [];
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    // Sector plan.
    private const int PrimaryRoot = 20, PrimaryGame = 21, PrimaryUsr = 22, JolietRoot = 23, JolietGame = 24, JolietUsr = 25;
    private const int SfbAt = 26, SfoAt = 27, PrimaryUpdate = 28, JolietUpdate = 29;
    private const int EbootAt = 30, BigFirst = 32, BigSecond = 40, PupAt = 50;

    public static TestImage Build(bool joliet = true, string? oddName = null)
    {
        var t = new TestImage();
        t.Make(joliet, oddName);
        return t;
    }

    private void Make(bool joliet, string? oddName)
    {
        var sfb = Fill("PS3_DISC.SFB", 100, 1);
        var sfo = ParamSfoBytes("Test Game", "BLUS12345");
        Files["PS3_GAME/PARAM.SFO"] = sfo;
        var eboot = Fill("PS3_GAME/USRDIR/EBOOT.BIN", 3000, 2);
        "SCE\0"u8.CopyTo(eboot);
        var big1 = Fill("", 4096, 3);
        var big2 = Fill("", 1000, 4);
        Files["PS3_GAME/USRDIR/BIG.DAT"] = [.. big1, .. big2];
        var pup = Fill("PS3_UPDATE/PS3UPDAT.PUP", 500, 5);

        // Sector 0: two plain regions, [0, 29] and [50, 63], with the encrypted [30, 49] between.
        BinaryPrimitives.WriteUInt32BigEndian(Plain.AsSpan(0), 2);
        uint[] bounds = [0, 29, 50, 63];
        for (var i = 0; i < bounds.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(Plain.AsSpan(8 + i * 4), bounds[i]);
        "PlayStation3"u8.CopyTo(Plain.AsSpan(S));
        "BLUS-12345      "u8.CopyTo(Plain.AsSpan(S + 16));

        Put(SfbAt, sfb); Put(SfoAt, sfo); Put(EbootAt, eboot); Put(BigFirst, big1); Put(BigSecond, big2); Put(PupAt, pup);

        foreach (var j in joliet ? new[] { false, true } : [false])
        {
            int root = j ? JolietRoot : PrimaryRoot, game = j ? JolietGame : PrimaryGame, usr = j ? JolietUsr : PrimaryUsr, upd = j ? JolietUpdate : PrimaryUpdate;
            Dir(root, root, root, j,
                Rec(SfbAt, sfb.Length, 0, "PS3_DISC.SFB", j),
                Rec(game, S, 2, "PS3_GAME", j),
                Rec(upd, S, 2, "PS3_UPDATE", j));
            Dir(game, game, root, j,
                Rec(SfoAt, sfo.Length, 0, "PARAM.SFO", j),
                Rec(usr, S, 2, "USRDIR", j));
            var usrRecords = new List<byte[]>
            {
                Rec(EbootAt, eboot.Length, 0, "EBOOT.BIN", j),
                Rec(BigFirst, big1.Length, 0x80, "BIG.DAT", j),
                Rec(BigSecond, big2.Length, 0, "BIG.DAT", j),
            };
            if (oddName is not null) usrRecords.Add(Rec(PupAt, 1, 0, oddName, j));
            Dir(usr, usr, game, j, [.. usrRecords]);
            Dir(upd, upd, root, j, Rec(PupAt, pup.Length, 0, "PS3UPDAT.PUP", j));
        }

        Descriptor(16, 1, PrimaryRoot, null);
        if (joliet) Descriptor(17, 2, JolietRoot, "%/E"u8.ToArray());
        Plain[(joliet ? 18 : 17) * S] = 255;
        "CD001"u8.CopyTo(Plain.AsSpan((joliet ? 18 : 17) * S + 1));

        Encrypted = (byte[])Plain.Clone();
        using var cipher = new SectorCipher(Key);
        for (var sector = 30; sector <= 49; sector++) cipher.Encrypt(sector, Encrypted.AsSpan(sector * S, S));
    }

    private byte[] Fill(string path, int length, byte seed)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(seed * 31 + i * 7);
        if (path.Length > 0) Files[path] = data;
        return data;
    }

    private void Put(int sector, byte[] data) => data.CopyTo(Plain.AsSpan(sector * S));

    private void Descriptor(int sector, byte type, int root, byte[]? escape)
    {
        var d = Plain.AsSpan(sector * S, S);
        d[0] = type;
        "CD001"u8.CopyTo(d[1..]);
        d[6] = 1;
        Encoding.ASCII.GetBytes("PS3VOLUME".PadRight(32)).CopyTo(d[8..]);
        Encoding.ASCII.GetBytes("PS3VOLUME".PadRight(32)).CopyTo(d[40..]);
        escape?.CopyTo(d[88..]);
        Rec(root, S, 2, "\0", escape is not null, raw: [0]).CopyTo(d[156..]);
    }

    private void Dir(int sector, int self, int parent, bool joliet, params byte[][] children)
    {
        var at = sector * S;
        foreach (var r in new[] { Rec(self, S, 2, "", joliet, raw: [0]), Rec(parent, S, 2, "", joliet, raw: [1]) }.Concat(children))
        {
            r.CopyTo(Plain.AsSpan(at));
            at += r.Length;
        }
    }

    private static byte[] Rec(int lba, int size, byte flags, string name, bool joliet, byte[]? raw = null)
    {
        var isDir = (flags & 2) != 0;
        var nameBytes = raw ?? (joliet ? Encoding.BigEndianUnicode.GetBytes(name) : Encoding.ASCII.GetBytes(isDir ? name : name + ";1"));
        var length = 33 + nameBytes.Length + (nameBytes.Length % 2 == 0 ? 1 : 0);
        var r = new byte[length];
        r[0] = (byte)length;
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(2), (uint)lba);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(6), (uint)lba);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(10), (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(14), (uint)size);
        r[25] = flags;
        r[28] = 1; r[31] = 1;
        r[32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(r.AsSpan(33));
        return r;
    }

    public static byte[] ParamSfoBytes(string title, string titleId)
    {
        var keys = "TITLE\0TITLE_ID\0\0"u8.ToArray(); // padded to 16
        const int count = 2, keyTable = 20 + count * 16, dataTable = keyTable + 16;
        var data = new byte[dataTable + 64];
        "\0PSF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x0101);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), keyTable);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), dataTable);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), count);
        void Entry(int i, int keyAt, string value, int dataAt)
        {
            var e = data.AsSpan(20 + i * 16);
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)keyAt);
            BinaryPrimitives.WriteUInt16LittleEndian(e[2..], 0x0204);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], (uint)dataAt);
            bytes.CopyTo(data.AsSpan(dataTable + dataAt));
        }
        keys.CopyTo(data.AsSpan(keyTable));
        Entry(0, 0, title, 0);
        Entry(1, 6, titleId, 32);
        return data;
    }

    /// <summary>Writes the encrypted (or plain) image to a new temp folder and returns its path.</summary>
    public string Save(TempDir dir, bool encrypted = true, string name = "Test Game (USA).iso")
    {
        var path = Path.Combine(dir.Path, name);
        File.WriteAllBytes(path, encrypted ? Encrypted : Plain);
        return path;
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("ps3kit-").FullName;
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
