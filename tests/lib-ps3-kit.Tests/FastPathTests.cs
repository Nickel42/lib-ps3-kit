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
}
