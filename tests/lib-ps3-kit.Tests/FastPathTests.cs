using Xunit;

namespace LibPs3Kit.Tests;

/// <summary>The faster paths give the same bytes as the plain ones.</summary>
public class FastPathTests
{
    [Theory]
    [InlineData(0L, 1)]
    [InlineData(30L, 127)]
    [InlineData(1000L, 128)]
    [InlineData(0xFFFFFF00L, 301)] // several slices; sector numbers that use all four IV bytes
    public void Decrypting_a_run_matches_decrypting_each_sector(long first, int sectors)
    {
        var data = new byte[sectors * TestImage.S];
        new Random(sectors).NextBytes(data);
        var expected = (byte[])data.Clone();
        using var cipher = new SectorCipher(TestImage.Key);
        for (var k = 0; k < sectors; k++) cipher.Decrypt(first + k, expected.AsSpan(k * TestImage.S, TestImage.S));
        cipher.DecryptRun(first, data);
        Assert.Equal(expected, data);
    }

    [Theory]
    [InlineData(4 << 20)]     // every file small
    [InlineData(TestImage.S)] // EBOOT.BIN and BIG.DAT large, read a chunk at a time
    public void Checking_a_jb_folder_finds_the_same_problems_in_the_same_order_one_or_several_at_a_time(int chunk)
    {
        using var dir = new TempDir();
        var image = TestImage.Build();
        var source = image.Save(dir);
        var folder = dir.Combine("Game");
        Ps3Converter.ToJbFolder(source, folder, TestImage.Key, new Ps3Options { Verify = false });
        List<IsoEntry> entries;
        using (var disc = Ps3Disc.Open(source)) entries = disc.FileSystem.Entries().ToList();
        var hashes = image.Files.ToDictionary(f => f.Key, f => System.Security.Cryptography.SHA1.HashData(f.Value), StringComparer.Ordinal);
        Ps3Verification Check(int parallel) => Ps3Converter.VerifyFolder(folder, entries, hashes,
            new Ps3Options { MaxParallelFiles = parallel, ChunkSize = chunk }, new ThrottledProgress(null), CancellationToken.None);

        Assert.True(Check(1).Success);
        Assert.True(Check(4).Success);

        string On(string path) => Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
        void Flip(string path, int at)
        {
            var bytes = File.ReadAllBytes(On(path));
            bytes[at] ^= 0xFF;
            File.WriteAllBytes(On(path), bytes);
        }
        Flip("PS3_GAME/PARAM.SFO", 10);
        Flip("PS3_GAME/USRDIR/BIG.DAT", 4500);
        Flip("PS3_UPDATE/PS3UPDAT.PUP", 0);
        File.Delete(On("PS3_DISC.SFB"));

        var one = Check(1);
        var several = Check(4);
        Assert.False(one.Success);
        Assert.Equal(4, one.Problems.Count);
        Assert.Equal(one.Problems, several.Problems);
        Assert.Contains("PS3_DISC.SFB is missing or the wrong size", one.Problems);
        Assert.Contains("PS3_GAME/USRDIR/BIG.DAT doesn't read back as written", one.Problems);
    }
}
