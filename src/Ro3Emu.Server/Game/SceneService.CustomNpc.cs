using Ro3Emu.Server.Maps;
using Romsg;
using System.Text;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class SceneService
{
    internal const uint CustomNpcHello = 77022;
    internal const uint Job50Hello = 77023, Job50State = 77024;

    internal Task Job50HelloAsync(GameSession session)
    {
        session.Job50Client = true;
        var job = data.Row("JobConfig", session.Progress?.Career ?? 0);
        var values = new List<string> { new CustomNpcSettings(options).Read().Job50 ? "1" : "0",
            job is { } current ? new JobConfig(current).JobRank.ToString() : "1", (session.Progress?.JobLevel ?? 1).ToString() };
        for (int rank = 2; rank <= 5; rank++)
        {
            var before = data.Row("JobLvConfig", rank * 100L + 39);
            var last = data.Row("JobLvConfig", rank * 100L + 40);
            values.Add(before is { } b && last is { } l ? $"{new JobLvConfig(b).JobeExp},{new JobLvConfig(l).JobeExp}" : "0,0");
            values.Add((session.Progress?.CompletedJobLevels.GetValueOrDefault(rank, 40) ?? 40).ToString());
        }
        return session.SendGameRawAsync(Job50State, Encoding.UTF8.GetBytes(string.Join('|', values)));
    }
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
