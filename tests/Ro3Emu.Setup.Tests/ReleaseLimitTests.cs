namespace Ro3Emu.Setup.Tests;

/// <summary>`update` keeps an installed game on its release when the publisher's newest is above the known-good limit.</summary>
public class ReleaseLimitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-limit-" + Guid.NewGuid().ToString("N"));

    public ReleaseLimitTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ReleaseLimit.EnvironmentVariable, null);
        Directory.Delete(_dir, true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData(140076, 140075L, true, true)]     // newer than the limit: held back
    [InlineData(140075, 140075L, true, false)]    // the limit itself is fine
    [InlineData(140010, 140075L, true, false)]
    [InlineData(140076, 140075L, false, false)]   // nothing installed yet: nothing to protect
    [InlineData(140076, null, true, false)]       // no limit given
    public void HoldsBack(long offered, long? limit, bool installed, bool expected) =>
        Assert.Equal(expected, ReleaseLimit.HoldsBack(offered, limit, installed));

    [Fact]
    public void ReadsTheFileAndIgnoresJunk()
    {
        Assert.Null(ReleaseLimit.Read(_dir));
        File.WriteAllText(Path.Combine(_dir, ReleaseLimit.FileName), "140075\r\n");
        Assert.Equal(140075, ReleaseLimit.Read(_dir));
        File.WriteAllText(Path.Combine(_dir, ReleaseLimit.FileName), "not a number");
        Assert.Null(ReleaseLimit.Read(_dir));
        File.WriteAllText(Path.Combine(_dir, ReleaseLimit.FileName), "-5");
        Assert.Null(ReleaseLimit.Read(_dir));
    }

    [Fact]
    public void TheEnvironmentVariableWinsOverTheFile()
    {
        File.WriteAllText(Path.Combine(_dir, ReleaseLimit.FileName), "140075");
        Environment.SetEnvironmentVariable(ReleaseLimit.EnvironmentVariable, "140100");
        Assert.Equal(140100, ReleaseLimit.Read(_dir));
    }
}
