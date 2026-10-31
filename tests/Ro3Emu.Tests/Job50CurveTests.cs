using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class Job50CurveTests
{
    [Fact]
    public void ContinuesLastIncrementAndRejectsUnsupportedLevels()
    {
        Assert.Equal(27262, Job50Curve.ExpNeed(23964, 25613, 41));
        Assert.Equal(40454, Job50Curve.ExpNeed(23964, 25613, 49));
        Assert.Throws<ArgumentOutOfRangeException>(() => Job50Curve.ExpNeed(23964, 25613, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => Job50Curve.ExpNeed(0, 25613, 41));
        Assert.Throws<OverflowException>(() => Job50Curve.ExpNeed(1, long.MaxValue, 41));
    }
}
