namespace Ro3Emu.Server.Game;

/// <summary>Owner-selected extension of the last observed job EXP increment, for levels 41–49.</summary>
internal static class Job50Curve
{
    internal static long ExpNeed(long level39, long level40, int level)
    {
        if (level39 <= 0 || level40 < level39 || level is < 41 or > 49)
            throw new ArgumentOutOfRangeException(nameof(level));
        return checked(level40 + (level - 40L) * (level40 - level39));
    }
}
