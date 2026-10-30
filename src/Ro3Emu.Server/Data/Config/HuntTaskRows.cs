using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ro3Emu.Server.Data.Config;

internal static class HuntTaskRows
{
    internal static void AddTo(Dictionary<string, JsonElement> table)
    {
        if (!table.TryGetValue("201000", out var source)) return;
        for (int level = 1; level <= 99; level++)
        {
            long id = Game.HuntCatalog.FirstTask + level - 1;
            string key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (table.ContainsKey(key)) throw new InvalidOperationException($"Hunt task {id} collides with client data.");
            var (npc, map) = Game.HuntCatalog.ForLevel(level);
            var row = JsonNode.Parse(source.GetRawText())!.AsObject();
            row["_iTaskID"] = id;
            row["_iTaskType"] = 2;
            row["_iTaskSonType"] = 0;
            row["_iCondition"] = 0;
            row["_iAward"] = 0;
            row["_iIfGiveUp"] = 0;
            row["_kPreTask"] = JsonSerializer.SerializeToNode(Array.Empty<int>());
            row["_kAcceptTask"] = JsonSerializer.SerializeToNode(new[] { 3 });
            row["_kDeliverTask"] = JsonSerializer.SerializeToNode(new[] { 3 });
            row["_kAcceptAction"] = JsonSerializer.SerializeToNode(new[] { new[] { "" } });
            row["_kEXECAction"] = JsonSerializer.SerializeToNode(new[] { new[] { "" } });
            row["_kEXP"] = JsonSerializer.SerializeToNode(Array.Empty<int>());
            row["_kProgressTarget"] = JsonSerializer.SerializeToNode(new[] { new[] { 104002, npc, 30 } });
            row["_kTrack"] = JsonSerializer.SerializeToNode(new[] { new[] { "142", map.ToString(), npc.ToString(), "-1" } });
            table.Add(key, JsonSerializer.SerializeToElement(row));
        }
    }
}
