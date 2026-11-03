using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    private static long HuntCycle(GameSession session) => session.Tasks?.CompleteTaskNum
        .Where(x => HuntCatalog.IsHunt(x.Key)).Sum(x => (long)Math.Max(0, x.Value)) ?? 0;

    private static int[] HuntOfferSlots(GameSession session) => session.Progress == null ? [] :
        HuntOptions.Choose(session.Progress.Level, HuntCycle(session), session.TaskRuntime.LastHuntNpc);

    private static bool HuntOfferAllowed(GameSession session, long taskId)
    {
        if (session.Progress == null) return false;
        if (!session.HuntOptionsClient)
            return taskId == HuntCatalog.FirstFor(session.EasierHunts) + Math.Clamp(session.Progress.Level, 1, 99) - 1;
        return HuntOfferSlots(session).Any(slot => taskId == HuntOptions.TaskId(session.Progress.Level, slot));
    }

    private async Task SendHuntChoicesAsync(GameSession session, WorldNpc npc, string message)
    {
        if (session.Progress == null || session.Tasks == null) return;
        bool active = session.Tasks.Infos.Values.Any(i => HuntCatalog.IsHunt(i.TaskId) && i.Status is Running or Finished);
        var targets = HuntOptions.ForLevel(session.Progress.Level);
        var choices = active ? [] : HuntOfferSlots(session).Select(slot => new
        {
            task = HuntOptions.TaskId(session.Progress.Level, slot), npc = targets[slot].Npc,
            map = targets[slot].Map, element = targets[slot].Element
        }).ToArray();
        if (active && string.IsNullOrEmpty(message)) message = "Você já tem uma caçada em andamento.";
        await session.SendGameRawAsync(HuntChoiceDown, JsonSerializer.SerializeToUtf8Bytes(new
        { npcUid = npc.Uid, message, choices }));
    }

    internal async Task HuntChoiceAsync(GameSession session, ReadOnlyMemory<byte> body)
    {
        if (!session.HuntOptionsClient || body.Length is 0 or > 512 || session.Role == null) return;
        long npcUid, task;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.TryGetProperty("npcUid", out var n) || !n.TryGetInt64(out npcUid)
                || !root.TryGetProperty("task", out var t) || !t.TryGetInt64(out task)) return;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return; }
        if (_worldData.Npc(npcUid) is not { } npc || !CanUseHunter(session, npcUid)) return;
        await AcceptAsync(session, new TaskAcceptReq { Id = task, NpcId = npcUid });
        bool accepted = session.Tasks?.Infos.TryGetValue(task, out var info) == true && info.Status == Running;
        await SendHuntChoicesAsync(session, npc, accepted ? "Caçada aceita. Boa sorte!" : "Não foi possível aceitar. Confira sua caçada atual.");
    }
}
