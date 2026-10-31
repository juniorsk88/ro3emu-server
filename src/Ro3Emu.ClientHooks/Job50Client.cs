using System;
using System.Text;
using System.Threading;
using HappyEngine.Base;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    internal static class Job50Client
    {
        internal const uint Hello = 77023, State = 77024;
        private static string _pending, _state;
        internal static bool ServerRecognized => _state != null;

        internal static void OnState(ReadOnlyMemory<byte> value) =>
            Interlocked.Exchange(ref _pending, Encoding.UTF8.GetString(value.ToArray()));

        internal static void Reset()
        {
            Interlocked.Exchange(ref _pending, null);
            _state = null;
        }

        internal static void Tick(Obf_R.Obf_ff manager, Obf_R.Obf_ff global)
        {
            var received = Interlocked.Exchange(ref _pending, null);
            if (received != null) _state = received;
            try
            {
                var config = manager.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_JobLvConfig");
                var rows = PassChallenges.Values(manager, "m_kCFG_JobLvConfig");
                if (config == null || rows == null) return;
                var parts = (_state ?? "0|1|1|0,0|40|0,0|40|0,0|40|0,0|40").Split('|');
                if (parts.Length != 11 || !int.TryParse(parts[1], out int current) || !int.TryParse(parts[2], out int currentLevel)) return;
                bool enabled = parts[0] == "1";
                var totals = config.Obf_RuA<Obf_R.Obf_ff>("m_kSkillPoint");
                for (int rank = 2; rank <= 5; rank++)
                {
                    var pair = parts[3 + (rank - 2) * 2].Split(',');
                    if (pair.Length != 2 || !long.TryParse(pair[0], out long before) || !long.TryParse(pair[1], out long last)
                        || !int.TryParse(parts[4 + (rank - 2) * 2], out int reached)) return;
                    var row40 = rows.Obf_ruA<long, Obf_R.Obf_ff>(rank * 100L + 40);
                    if (row40 == null) return;
                    row40 = row40.Obf_RuA<Obf_R.Obf_ff>("_ro3Original") ?? row40;
                    if (last <= 0)
                    {
                        var row39 = rows.Obf_ruA<long, Obf_R.Obf_ff>(rank * 100L + 39);
                        if (row39 == null) return;
                        before = LuaPatches.ToLong(row39.Obf_RuA<object>("_iJobeExp"));
                        last = LuaPatches.ToLong(row40.Obf_RuA<object>("_iJobeExp"));
                    }
                    if (before <= 0 || last < before) return;
                    long cap = enabled ? 50 : rank == current ? Math.Max(40, Math.Min(50, currentLevel)) : 40;
                    var first = rows.Obf_ruA<long, Obf_R.Obf_ff>(rank * 100L + 1);
                    long addedId = rank * 100L + 41;
                    var rawget = global.Obf_LuA<Obf_R.Obf_Ef>("rawget");
                    var added = CustomNpcClient.Exists(rawget, rows, addedId) ? rows.Obf_ruA<long, Obf_R.Obf_ff>(addedId) : null;
                    bool installed = first?.Obf_RuA<Obf_R.Obf_ff>("_ro3Original") != null && added != null
                        && LuaPatches.ToLong(first.Obf_RuA<object>("_iMaxJobLv")) == cap
                        && LuaPatches.ToLong(added.Obf_RuA<object>("_iJobeExp")) == checked(last + last - before);
                    for (int level = 1; !installed && level <= 50; level++)
                    {
                        long id = rank * 100L + level;
                        var baseRow = level <= 40 ? rows.Obf_ruA<long, Obf_R.Obf_ff>(id) : row40;
                        if (baseRow == null) return;
                        baseRow = baseRow.Obf_RuA<Obf_R.Obf_ff>("_ro3Original") ?? baseRow;
                        var row = CustomNpcClient.Inherit(global, baseRow);
                        PassChallenges.RawSet(row, "_ro3Original", baseRow);
                        PassChallenges.RawSet(row, "_iID", id);
                        PassChallenges.RawSet(row, "_iJobLv", (long)level);
                        PassChallenges.RawSet(row, "_iMaxJobLv", cap);
                        if (level > 40)
                        {
                            PassChallenges.RawSet(row, "_iJobeExp", level == 50 ? 0L : checked(last + (level - 40L) * (last - before)));
                            PassChallenges.RawSet(row, "_iSkillPoint", 1L);
                        }
                        PassChallenges.RawSet(rows, id, row);
                    }
                    if (totals != null)
                        PassChallenges.RawSet(totals, (long)rank, rank < current ? (long)Math.Max(40, Math.Min(50, reached)) : cap);
                }
                SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(Hello, Array.Empty<byte>());
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.Job50] " + e.Message); }
        }
    }
}
