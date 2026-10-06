namespace PgCopy.Core.Tests;

public class ByteSizeTests
{
    [Theory]
    [InlineData("512", 512L)]
    [InlineData("1KB", 1024L)]
    [InlineData("128MB", 128L << 20)]
    [InlineData("1GB", 1L << 30)]
    [InlineData("2.5 GiB", (long)(2.5 * (1L << 30)))]
    [InlineData("off", 0L)]
    public void Parses(string text, long expected) => Assert.Equal(expected, ByteSize.Parse(text));

    [Fact]
    public void RejectsGarbage() => Assert.Throws<FormatException>(() => ByteSize.Parse("lots"));
}
