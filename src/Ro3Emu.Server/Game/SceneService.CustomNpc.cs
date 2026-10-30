using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Server.Game;

public sealed partial class SceneService
{
    internal const uint CustomNpcHello = 77022;
    internal static bool VisibleTo(GameSession session, WorldNpc npc) => npc.NpcId switch
    {
        CustomNpcSettings.HealerNpc => session.CustomNpcRows,
        CustomNpcSettings.HunterNpc => session.CustomNpcRows,
        _ => true,
    };

    internal Func<GameSession, Task>? CustomNpcReady { get; set; }
    internal Func<GameSession, WorldNpc, int, Task>? CustomHuntAction { get; set; }

    internal async Task CustomNpcHelloAsync(GameSession session)
    {
        if (session.CustomNpcRows) return;
        session.CustomNpcRows = true;
        if (session.Role == null) return;
        if (!session.SceneLoading && worldData.Map(session.SceneEntry) is { } map)
        {
            var view = new AoIViewChangeData();
            foreach (var npc in map.Npcs.Where(n => IsCustomHealer(n) || IsCustomHunter(n))) view.Bases.Add(NpcEntity(npc));
            if (view.Bases.Count > 0) await session.SendGameAsync(GameAoiSink.SyncAoiView, view);
        }
        if (CustomNpcReady != null) await CustomNpcReady(session);
    }
    private static bool IsCustomHealer(WorldNpc npc) => npc.SceneEntry == CustomNpcSettings.Prontera
        && npc.NpcId == CustomNpcSettings.HealerNpc && npc.Name == "Curandeira";
    private static bool IsCustomHunter(WorldNpc npc) => npc.SceneEntry == CustomNpcSettings.Prontera
        && npc.NpcId == CustomNpcSettings.HunterNpc && npc.Name == "Caçador";

    private async Task UseCustomHealerAsync(GameSession session, WorldNpc npc, CustomNpcSettings.Snapshot settings)
    {
        if (!settings.Healer || session.Role == null || session.Dead) return;
        foreach (int id in new[] { 1400100 + settings.BuffLevel, 1401000 + settings.BuffLevel })
        {
            if (data.Row("BuffConfig", id) is not { } row) continue;
            var candidate = new Data.Config.BuffConfig(row).ChangeAttri.Where(x => x.Length >= 2)
                .ToDictionary(x => x[0], x => (double)x[1]);
            int group = id / 100;
            var current = session.Buffs.FirstOrDefault(b => b.ConfigId / 100 == group && b.ExpireMs > MonsterService.NowMs);
            if (current != null && current.Changes.Any(c => candidate.TryGetValue(c.Attr, out var value) && c.Value > value)) continue;
            await buffs.AddAsync(session, id, 600000, npc.Uid);
        }
        await character.SetHpSpAsync(session, long.MaxValue, long.MaxValue);
    }
}
