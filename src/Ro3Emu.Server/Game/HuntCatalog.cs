namespace Ro3Emu.Server.Game;

/// <summary>Field hunt targets selected from exported spawns, EXP and scaled HP; indexed by base level.</summary>
internal static class HuntCatalog
{
    internal const long FirstTask = 99010001, EasierFirstTask = 99011001;
    internal static long FirstFor(bool easier) => easier ? EasierFirstTask : FirstTask;
    internal static (int Npc, int Map) ForLevel(int level) => HuntTargets.ForLevel(level);

    internal static bool IsHunt(long id) => id >= FirstTask && id < FirstTask + 99
        || id >= EasierFirstTask && id < EasierFirstTask + 99;
}
