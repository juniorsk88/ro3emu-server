using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class LifeJobService
{
    private const int LifeLevelCondition = 11001; // LC_InteractCondition.CheckLifeLevel

    /// <summary>
    /// A gathering resource unlocks itself once its LifeCollectionConfig._iCondition holds. Every collection has
    /// _iMapIds, and for those the client's Unlock button (UI_Sys_Life_ContentWidgetCtrl.OnClickUnlock) only walks to
    /// the gathering area: it never sends StudyCollection, so Lv. 2 ore stayed locked at Mining 3. The conditions are
    /// the profession's own level (LC_ConditionList 11001 [job, 1, level]), read from <paramref name="next"/> so a
    /// level gained by this very gather counts before it is committed. (Ported from upstream aethai/ro3emu-server #87.)
    /// </summary>
    private void UnlockEarnedCollections(GameSession s, DBLifeJobInfo next)
    {
        foreach (var row in data.Table("LifeCollectionConfig").Values)
        {
            long job = GameData.Int(row, "_iJobId"), id = GameData.Int(row, "_iCollectionId");
            if (!Current(row) || !next.ActiveLifejob.Values.Contains(job) || !next.LifeJobInfoMap.TryGetValue(job, out var info)
                || info.JobLevel <= 0 || info.GatherInfo.ItemDetails.ContainsKey(id)
                || !EarnedBy(s, next, GameData.Int(row, "_iCondition"))) continue;
            UnlockCollection(next, row);
        }
    }

    private bool EarnedBy(GameSession s, DBLifeJobInfo next, long conditionId)
    {
        if (conditionId == 0) return true;
        if (data.Row("LC_ConditionList", conditionId) is not { } list
            || !list.TryGetProperty("m_kSubConditionList", out var subs) || subs.ValueKind != JsonValueKind.Array)
            return functions.ConditionMet(s, conditionId);
        var levels = new List<(long Job, long Level)>();
        foreach (var sub in subs.EnumerateArray())
        {
            var d = Numbers(sub, "m_kData");
            bool inverse = sub.TryGetProperty("m_bInverse", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (d.Length < 3 || d[0] != LifeLevelCondition || inverse) return functions.ConditionMet(s, conditionId);
            levels.Add((d[1], d[2]));
        }
        return levels.All(l => next.ActiveLifejob.Values.Contains(l.Job) && next.LifeJobInfoMap.TryGetValue(l.Job, out var info)
            && info.JobLevel >= l.Level);
    }
}
