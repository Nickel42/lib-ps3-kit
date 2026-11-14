using Xunit;

namespace LibPs3Kit.Tests;

public class Ps3KitTests
{
    private static readonly DiscKey WrongKey = DiscKey.Parse("FFEEDDCCBBAA99887766554433221100");

    [Fact]
    public void Region_table_alternates_plain_and_encrypted()
    {
        var image = TestImage.Build();
        var table = RegionTable.TryParse(image.Plain.AsSpan(0, TestImage.S), TestImage.Sectors)!;
        Assert.Equal([new DiscRegion(0, 29, false), new DiscRegion(30, 49, true), new DiscRegion(50, 63, false)], table.Regions);
        Assert.True(table.IsEncrypted(30));
        Assert.True(table.IsEncrypted(49));
        Assert.False(table.IsEncrypted(29));
        Assert.False(table.IsEncrypted(50));
    }

    [Fact]
    public void Region_table_rejects_junk()
    {
        Assert.Null(RegionTable.TryParse(new byte[TestImage.S], 64));
        var bad = new byte[TestImage.S];
        bad[3] = 2; // two plain regions, but the bounds don't rise
        bad[15] = 10; bad[19] = 5;
        Assert.Null(RegionTable.TryParse(bad, 64));
    }

    [Fact]
    public void Opens_and_tells_encrypted_from_decrypted()
    {
        using var dir = new TempDir();
        var image = TestImage.Build();
        using (var disc = Ps3Disc.Open(image.Save(dir)))
        {
            Assert.Equal(EncryptionState.Encrypted, disc.State);
            Assert.Equal("BLUS-12345", disc.DiscTitleId);
            Assert.True(disc.KeyFits(TestImage.Key));
            Assert.False(disc.KeyFits(WrongKey));
            Assert.Equal("BLUS12345", disc.ReadParamSfo()?.TitleId); // PARAM.SFO is in a plain region
        }
        using (var disc = Ps3Disc.Open(image.Save(dir, encrypted: false, name: "plain.iso")))
            Assert.Equal(EncryptionState.Decrypted, disc.State);
    }

    [Fact]
    public void Decrypts_to_the_plain_image_and_encrypts_back()
    {
        using var dir = new TempDir();
        var image = TestImage.Build();
        var source = image.Save(dir);
        var output = dir.Combine("out.iso");
        var result = Ps3Converter.ToDecryptedIso(source, output, TestImage.Key);
        Assert.Equal(image.Plain, File.ReadAllBytes(output));
        Assert.True(result.Verification!.Success);
        Assert.Equal("BLUS12345", result.TitleId);
        Assert.Equal("Test Game", result.Title);
        Assert.False(File.Exists(output + ".partial"));

        var back = dir.Combine("back.iso");
        Ps3Converter.ToEncryptedIso(output, back, TestImage.Key);
        Assert.Equal(image.Encrypted, File.ReadAllBytes(back));
    }

    [Fact]
    public void Extracts_a_jb_folder_from_the_encrypted_image()
    {
        using var dir = new TempDir();
        var image = TestImage.Build();
        var folder = dir.Combine("Test Game");
        var result = Ps3Converter.ToJbFolder(image.Save(dir), folder, TestImage.Key);
        Assert.True(result.Verification!.Success);
        foreach (var (path, data) in image.Files)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(image.Files.Count, result.FilesWritten);
        Assert.False(Directory.Exists(folder + ".partial"));
    }

    [Fact]
    public void Jb_folder_can_leave_out_the_update_and_reads_the_primary_tree()
    {
        using var dir = new TempDir();
        var image = TestImage.Build(joliet: false);
        var folder = dir.Combine("Game");
        Ps3Converter.ToJbFolder(image.Save(dir), folder, TestImage.Key, new Ps3Options { IncludeUpdate = false });
        Assert.True(File.Exists(Path.Combine(folder, "PS3_GAME", "USRDIR", "EBOOT.BIN"))); // ";1" stripped
        Assert.False(Directory.Exists(Path.Combine(folder, "PS3_UPDATE")));
        Assert.Equal(image.Files["PS3_GAME/USRDIR/BIG.DAT"], File.ReadAllBytes(Path.Combine(folder, "PS3_GAME", "USRDIR", "BIG.DAT")));
    }

    [Fact]
    public void A_decrypted_image_needs_no_key()
    {
        using var dir = new TempDir();
        var image = TestImage.Build();
        var folder = dir.Combine("Game");
        Ps3Converter.ToJbFolder(image.Save(dir, encrypted: false), folder, key: null);
        Assert.Equal(image.Files["PS3_GAME/USRDIR/EBOOT.BIN"], File.ReadAllBytes(Path.Combine(folder, "PS3_GAME", "USRDIR", "EBOOT.BIN")));
    }

    [Fact]
    public void Missing_or_wrong_key_fails_before_writing()
    {
        using var dir = new TempDir();
        var source = TestImage.Build().Save(dir);
        var output = dir.Combine("out.iso");
        Assert.Equal(Ps3Error.KeyMissing, Assert.Throws<Ps3Exception>(() => Ps3Converter.ToDecryptedIso(source, output, null)).Error);
        Assert.Equal(Ps3Error.WrongKey, Assert.Throws<Ps3Exception>(() => Ps3Converter.ToDecryptedIso(source, output, WrongKey)).Error);
        Assert.False(File.Exists(output));
        Assert.False(File.Exists(output + ".partial"));
    }

    [Fact]
    public void Existing_output_is_kept_unless_overwrite()
    {
        using var dir = new TempDir();
        var source = TestImage.Build().Save(dir);
        var output = dir.Combine("out.iso");
        File.WriteAllText(output, "keep");
        Assert.Equal(Ps3Error.OutputExists, Assert.Throws<Ps3Exception>(() => Ps3Converter.ToDecryptedIso(source, output, TestImage.Key)).Error);
        Assert.Equal("keep", File.ReadAllText(output));
        Ps3Converter.ToDecryptedIso(source, output, TestImage.Key, new Ps3Options { Overwrite = true });
        Assert.NotEqual("keep", File.ReadAllText(output));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a\\b")]
    public void Unsafe_names_are_refused(string name)
    {
        using var dir = new TempDir();
        var source = TestImage.Build(oddName: name).Save(dir);
        var ex = Assert.Throws<Ps3Exception>(() => Ps3Converter.ToJbFolder(source, dir.Combine("Game"), TestImage.Key));
        Assert.Equal(Ps3Error.BadFilesystem, ex.Error);
        Assert.False(Directory.Exists(dir.Combine("Game.partial")));
    }

    [Fact]
    public void Not_a_ps3_image()
    {
        using var dir = new TempDir();
        var path = dir.Combine("other.iso");
        File.WriteAllBytes(path, new byte[64 * 2048]);
        Assert.False(Ps3Disc.IsPs3Image(path));
        Assert.Equal(Ps3Error.NotPs3Disc, Assert.Throws<Ps3Exception>(() => Ps3Disc.Open(path)).Error);
    }

    [Fact]
    public void Disc_keys_parse_load_and_are_found_beside_the_image()
    {
        using var dir = new TempDir();
        Assert.Equal("00112233445566778899AABBCCDDEEFF", DiscKey.Parse(" 0x00112233-44556677 8899aabbccddeeff\n").ToString());
        Assert.False(DiscKey.TryParse("1234", out _));
        var iso = dir.Combine("Game (USA).iso");
        Assert.Null(DiscKey.FindFileFor(iso));
        File.WriteAllText(dir.Combine("Game (USA).dkey"), "00112233445566778899AABBCCDDEEFF");
        Assert.Equal(TestImage.Key.ToString(), DiscKey.Load(DiscKey.FindFileFor(iso)!).ToString());

        var keys = Directory.CreateDirectory(dir.Combine("keys")).FullName;
        File.WriteAllBytes(Path.Combine(keys, "Other.dkey"), TestImage.Key.ToArray());
        Assert.Equal(TestImage.Key.ToString(), DiscKey.Load(DiscKey.FindFileFor(dir.Combine("Other.iso"), [keys])!).ToString());
    }

    [Fact]
    public void Decrypting_stream_reads_across_region_edges()
    {
        using var dir = new TempDir();
        var image = TestImage.Build();
        using var disc = Ps3Disc.Open(image.Save(dir));
        using var view = disc.OpenDecrypted(TestImage.Key);
        var buffer = new byte[5000];
        view.Position = 29 * TestImage.S + 100; // starts in plain, runs into the encrypted region
        Assert.Equal(buffer.Length, view.ReadAtLeast(buffer, buffer.Length));
        Assert.Equal(image.Plain.AsSpan(29 * TestImage.S + 100, buffer.Length).ToArray(), buffer);
    }
}
