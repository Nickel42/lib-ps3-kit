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

    [Fact]
    public void Decrypting_stream_reads_whole_sectors_across_region_edges_in_one_call()
    {
        using var dir = new TempDir();
        var image = TestImage.Build();
        using var disc = Ps3Disc.Open(image.Save(dir));
        using var view = disc.OpenDecrypted(TestImage.Key);
        var buffer = new byte[image.Plain.Length];
        Assert.Equal(buffer.Length, view.ReadAtLeast(buffer, buffer.Length));
        Assert.Equal(image.Plain, buffer);
    }

    [Fact]
    public void Region_runs_end_at_the_next_region()
    {
        var table = RegionTable.TryParse(TestImage.Build().Plain.AsSpan(0, TestImage.S), TestImage.Sectors)!;
        Assert.Equal((false, 29L), table.RunAt(0));
        Assert.Equal((true, 49L), table.RunAt(30));
        Assert.Equal((true, 49L), table.RunAt(49));
        Assert.Equal((false, long.MaxValue), table.RunAt(50));
        Assert.Equal((false, long.MaxValue), table.RunAt(1000));
    }

    [Theory]
    [InlineData(TestImage.S)]        // one sector at a time
    [InlineData(3 * TestImage.S)]    // chunks that straddle the region edges
    [InlineData(7 * TestImage.S + 5)] // chunks that don't end on a sector
    public void Converts_in_small_chunks_and_hands_over_the_source_bytes(int chunk)
    {
        using var dir = new TempDir();
        var image = TestImage.Build();
        var source = image.Save(dir);
        var seen = new MemoryStream();
        var output = dir.Combine("out.iso");
        var result = Ps3Converter.ToDecryptedIso(source, output, TestImage.Key,
            new Ps3Options { ChunkSize = chunk, SourceBytes = bytes => seen.Write(bytes) });
        Assert.True(result.Verification!.Success);
        Assert.Equal(image.Plain, File.ReadAllBytes(output));
        Assert.Equal(image.Encrypted, seen.ToArray());

        var back = dir.Combine("back.iso");
        Ps3Converter.ToEncryptedIso(output, back, TestImage.Key, new Ps3Options { ChunkSize = chunk });
        Assert.Equal(image.Encrypted, File.ReadAllBytes(back));
    }

    [Theory]
    [InlineData(1, 4 << 20)]
    [InlineData(1, TestImage.S)]
    [InlineData(4, 4 << 20)]       // every file is small: all extracted side by side
    [InlineData(4, TestImage.S)]   // BIG.DAT and EBOOT.BIN are large, the rest small
    public void Extracts_a_jb_folder_in_chunks_and_in_parallel(int parallel, int chunk)
    {
        using var dir = new TempDir();
        var image = TestImage.Build();
        var folder = dir.Combine("Game");
        var result = Ps3Converter.ToJbFolder(image.Save(dir), folder, TestImage.Key,
            new Ps3Options { MaxParallelFiles = parallel, ChunkSize = chunk });
        Assert.True(result.Verification!.Success);
        foreach (var (path, data) in image.Files)
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(image.Files.Count, result.FilesWritten);
    }

    [Fact]
    public void A_cancelled_parallel_extraction_leaves_nothing()
    {
        using var dir = new TempDir();
        var image = TestImage.Build();
        var folder = dir.Combine("Game");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Ps3Converter.ToJbFolder(image.Save(dir), folder, TestImage.Key,
            new Ps3Options { MaxParallelFiles = 4 }, token: cts.Token));
        Assert.False(Directory.Exists(folder + ".partial"));
        Assert.False(Directory.Exists(folder));
    }

    /// <summary>A JB folder made from the test image, with extras: an empty file and folder, nested folders, odd names.</summary>
    private static string JbFolder(TempDir dir, TestImage image, bool extras = true)
    {
        var folder = dir.Combine("Test Game");
        Ps3Converter.ToJbFolder(image.Save(dir), folder, TestImage.Key);
        if (extras)
        {
            Directory.CreateDirectory(Path.Combine(folder, "PS3_GAME", "USRDIR", "empty folder"));
            Directory.CreateDirectory(Path.Combine(folder, "PS3_GAME", "USRDIR", "a", "b", "c"));
            File.WriteAllBytes(Path.Combine(folder, "PS3_GAME", "USRDIR", "a", "b", "c", "deep.bin"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(folder, "PS3_GAME", "USRDIR", "zz_empty.dat"), []);
            File.WriteAllText(Path.Combine(folder, "PS3_GAME", "USRDIR", "Lower case, spaces & Ünïcode — a rather long file name.txt"), "hello");
        }
        return folder;
    }

    /// <summary>Every file under <paramref name="folder"/>, by its path inside it ('/'-separated), with its bytes.</summary>
    private static Dictionary<string, byte[]> FilesIn(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllBytes, StringComparer.Ordinal);

    [Theory]
    [InlineData(4 << 20, Iso9660Writer.DefaultMaxExtent)]
    [InlineData(TestImage.S, 2 * TestImage.S)]          // small chunks; BIG.DAT and EBOOT.BIN in several extents
    [InlineData(7 * TestImage.S + 5, TestImage.S)]      // chunks off the sector grid; every file a sector per extent
    public void A_jb_folder_becomes_a_decrypted_iso_and_back(int chunk, long extent)
    {
        using var dir = new TempDir();
        var image = TestImage.Build();
        var folder = JbFolder(dir, image);
        var iso = dir.Combine("Rebuilt.iso");
        var result = Ps3Converter.FolderToIso(folder, iso, new Ps3Options { ChunkSize = chunk, MaxExtentSize = extent });
        Assert.True(result.Verification!.Success);
        Assert.Equal("BLUS12345", result.TitleId);
        Assert.Equal("Test Game", result.Title);
        Assert.False(File.Exists(iso + ".partial"));

        using (var disc = Ps3Disc.Open(iso))
        {
            Assert.Equal(EncryptionState.NoEncryptedRegions, disc.State);
            Assert.Equal("BLUS-12345", disc.DiscTitleId);
            Assert.True(disc.FileSystem.IsJoliet);
            Assert.Equal("PS3VOLUME", disc.FileSystem.VolumeId);
            Assert.Equal("BLUS12345", disc.ReadParamSfo()!.TitleId);
            Assert.Contains(disc.FileSystem.Entries(), e => e.Path == "PS3_GAME/USRDIR/empty folder" && e.IsDirectory);
        }

        var again = dir.Combine("Again");
        Ps3Converter.ToJbFolder(iso, again, null);
        Assert.Equal(FilesIn(folder), FilesIn(again));
    }

    [Fact]
    public void The_primary_tree_has_upper_case_names_with_versions_and_the_same_files()
    {
        using var dir = new TempDir();
        var folder = JbFolder(dir, TestImage.Build());
        var iso = dir.Combine("Rebuilt.iso");
        Ps3Converter.FolderToIso(folder, iso);
        using var s = File.OpenRead(iso);
        var primary = Iso9660Volume.TryOpen(s, preferJoliet: false)!;
        Assert.False(primary.IsJoliet);
        var eboot = primary.Find("PS3_GAME/USRDIR/EBOOT.BIN")!;
        Assert.Equal(3000, eboot.Size);
        Assert.NotNull(primary.Find("PS3_GAME/USRDIR/A/B/C/DEEP.BIN"));
        Assert.Equal(FilesIn(folder).Count, primary.Entries().Count(e => !e.IsDirectory));
    }

    [Fact]
    public void Folder_to_iso_can_leave_out_the_update()
    {
        using var dir = new TempDir();
        var folder = JbFolder(dir, TestImage.Build(), extras: false);
        var iso = dir.Combine("Rebuilt.iso");
        Ps3Converter.FolderToIso(folder, iso, new Ps3Options { IncludeUpdate = false });
        using var disc = Ps3Disc.Open(iso);
        Assert.Null(disc.FileSystem.Find("PS3_UPDATE"));
        Assert.NotNull(disc.FileSystem.Find("PS3_GAME/USRDIR/BIG.DAT"));
    }

    [Fact]
    public void Folder_to_iso_refuses_what_it_cant_make()
    {
        using var dir = new TempDir();
        var notGame = dir.Combine("Not a game");
        Directory.CreateDirectory(notGame);
        File.WriteAllText(Path.Combine(notGame, "readme.txt"), "hi");
        Assert.Equal(Ps3Error.NotPs3Disc, Assert.Throws<Ps3Exception>(() => Ps3Converter.FolderToIso(notGame, dir.Combine("x.iso"))).Error);

        var folder = JbFolder(dir, TestImage.Build(), extras: false);
        Assert.Throws<ArgumentException>(() => Ps3Converter.FolderToIso(folder, Path.Combine(folder, "inside.iso")));

        // Two names that are the same once made upper case for the primary tree.
        File.WriteAllText(Path.Combine(folder, "PS3_GAME", "USRDIR", "x y.txt"), "1");
        File.WriteAllText(Path.Combine(folder, "PS3_GAME", "USRDIR", "x_y.txt"), "2");
        Assert.Equal(Ps3Error.BadFilesystem, Assert.Throws<Ps3Exception>(() => Ps3Converter.FolderToIso(folder, dir.Combine("y.iso"))).Error);
        Assert.False(File.Exists(dir.Combine("y.iso.partial")));

        File.Delete(Path.Combine(folder, "PS3_GAME", "USRDIR", "x_y.txt"));
        Ps3Converter.FolderToIso(folder, dir.Combine("z.iso"));
        Assert.Equal(Ps3Error.OutputExists, Assert.Throws<Ps3Exception>(() => Ps3Converter.FolderToIso(folder, dir.Combine("z.iso"))).Error);
    }

    [Theory]
    [InlineData("BLUS30443", "BLUS-30443")]
    [InlineData("BLUS-30443", "BLUS-30443")]
    [InlineData(" NPEB01234 ", "NPEB-01234")]
    [InlineData("", null)]
    public void Title_ids_get_their_dash(string id, string? expected) => Assert.Equal(expected, Ps3Converter.DiscTitleId(id));
}
