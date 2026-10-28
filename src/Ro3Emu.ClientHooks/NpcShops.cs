using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Service NPCs (merchants, the refine smiths, the stylist: <see cref="NpcServiceTable"/>) whose DialogueConfig is one line of
    /// chatter get the options the game's own shop NPCs have (DialogueConfig 2100000, 40001402: option rows whose _iEXECAction
    /// is OpenFunctionUI [25, function, layer, panel, parameters...] then CloseUI [8] of the dialogue panel).
    /// The rows are appended after the dialogue's last row, where Lua_CFG_DialogueConfig.GetOptionConfigs finds them. Option
    /// texts are the client's own (10370000673 "Open Shop", 10370000679 "Leave", FunctionConfig 8's name "Refine").
    /// Each dialogue is extended only while it still has the number of rows the client's data gives it, so running again after
    /// a config reload or a periodic check adds nothing twice, and a dialogue whose data changed is left alone (and reported once).
    /// </summary>
    internal static class NpcShops
    {
        private static readonly HashSet<long> Reported = new HashSet<long>();
        private const int MaxInteractSets = 20;
        private static int _interactSets;

        /// <summary>From PassChallenges.Inject (after every config reload) and its periodic check. Idempotent.</summary>
        internal static void Inject(Obf_R.Obf_ff dbm)
        {
            var dialogues = PassChallenges.Values(dbm, "m_kCFG_DialogueConfig");
            if (dialogues == null)
            {
                Debug.LogWarning("[Ro3Emu.NpcShops] DialogueConfig not found; the service NPCs stay silent");
                return;
            }
            var names = new StringBuilder();
            int added = 0;
            foreach (var npc in NpcServiceTable.Dialogues)
            {
                try
                {
                    if (!Extend(dialogues, npc)) continue;
                    added++;
                    names.Append(names.Length > 0 ? ", " : "").Append(npc.Name).Append(' ').Append(npc.Npc);
                }
                catch (Exception e) { Debug.LogWarning("[Ro3Emu.NpcShops] dialogue " + npc.Dialogue + ": " + e.Message); }
            }
            foreach (var redirect in NpcServiceTable.Redirects)
            {
                try
                {
                    if (!Redirect(dbm, dialogues, redirect)) continue;
                    added++;
                    names.Append(names.Length > 0 ? ", " : "").Append(redirect.Name).Append(' ').Append(redirect.Npc).Append(" (own dialogue)");
                }
                catch (Exception e) { Debug.LogWarning("[Ro3Emu.NpcShops] NPC " + redirect.Npc + ": " + e.Message); }
            }
            if (added > 0) Debug.Log("[Ro3Emu.NpcShops] service options added to " + added + " NPC dialogues: " + names);
        }

        private static bool Extend(Obf_R.Obf_ff dialogues, NpcDialogue npc)
        {
            var rows = dialogues.Obf_ruA<long, Obf_R.Obf_ff>(npc.Dialogue);
            if (rows == null) return Unexpected(npc.Dialogue, "has no rows");
            long count = PassChallenges.Length(rows);
            var options = NpcServiceTable.Options(npc.Service);
            if (count == npc.Rows + options.Length) return false; // already ours
            if (count != npc.Rows) return Unexpected(npc.Dialogue, "has " + count + " rows, not " + npc.Rows);
            AppendOptions(rows, npc.Dialogue, npc.Npc, count, options);
            return true;
        }

        /// <summary>
        /// The NPC's own stub dialogue gets a line and the options, and the NPC's _iInteractID points at it (rawset: the NPC row
        /// is the client's lazy record). Done again on its own when only one of the two configs was reloaded.
        /// </summary>
        private static bool Redirect(Obf_R.Obf_ff dbm, Obf_R.Obf_ff dialogues, NpcRedirect redirect)
        {
            var options = NpcServiceTable.Options(redirect.Service);
            var rows = dialogues.Obf_ruA<long, Obf_R.Obf_ff>(redirect.Dialogue);
            long count = rows == null ? 0 : PassChallenges.Length(rows);
            bool changed = false;
            if (count != 1 + options.Length)
            {
                if (count > 1) return Unexpected(redirect.Dialogue, "has " + count + " rows, not a one-row stub");
                var line = PassChallenges.Row(ToPairs(NpcServiceTable.LineFields(redirect.Dialogue, redirect.Npc, redirect.Text)));
                var made = PassChallenges.List(line);
                AppendOptions(made, redirect.Dialogue, redirect.Npc, 1, options);
                PassChallenges.RawSet(dialogues, redirect.Dialogue, made);
                changed = true;
            }
            var npcs = PassChallenges.Values(dbm, "m_kCFG_NPCConfig");
            var row = npcs?.Obf_ruA<long, Obf_R.Obf_ff>(redirect.Npc);
            if (row == null) return Unexpected(redirect.Npc, "NPC row not found") || changed;
            if (!InteractIs(row, redirect.Dialogue))
            {
                // A reload puts the shared interaction back, once per reload; far more than that means the read above is wrong.
                if (++_interactSets > MaxInteractSets)
                {
                    Unexpected(redirect.Npc, "keeps losing its interaction");
                    return changed;
                }
                PassChallenges.RawSet(row, "_iInteractID", PassChallenges.List(redirect.Dialogue));
                changed = true;
            }
            return changed;
        }

        private static bool InteractIs(Obf_R.Obf_ff npcRow, long interact)
        {
            try
            {
                var list = npcRow.Obf_RuA<Obf_R.Obf_ff>("_iInteractID");
                return list != null && LuaPatches.ToLong(list.Obf_ruA<long, object>(1L)) == interact;
            }
            catch (Exception) { return false; }
        }

        private static void AppendOptions(Obf_R.Obf_ff rows, long dialogue, long npc, long count, NpcOption[] options)
        {
            for (int i = 0; i < options.Length; i++)
                PassChallenges.Append(rows, PassChallenges.Row(ToPairs(NpcServiceTable.OptionFields(dialogue, npc, count + 1 + i, options[i]))));
        }

        private static bool Unexpected(long id, string what)
        {
            if (Reported.Add(id)) Debug.LogWarning("[Ro3Emu.NpcShops] " + id + " " + what + "; left as it is");
            return false;
        }

        private static object[] ToPairs(List<KeyValuePair<string, object>> fields)
        {
            var pairs = new object[fields.Count * 2];
            for (int i = 0; i < fields.Count; i++)
            {
                pairs[2 * i] = fields[i].Key;
                pairs[2 * i + 1] = ToLua(fields[i].Value);
            }
            return pairs;
        }

        /// <summary>long and string stay as they are; long[] and string[][] become Lua lists (the client's _iText and _iEXECAction).</summary>
        private static object ToLua(object value)
        {
            switch (value)
            {
                case long[] numbers:
                    {
                        var items = new object[numbers.Length];
                        for (int i = 0; i < numbers.Length; i++) items[i] = numbers[i];
                        return PassChallenges.List(items);
                    }
                case string[][] actions:
                    {
                        var items = new object[actions.Length];
                        for (int i = 0; i < actions.Length; i++) items[i] = PassChallenges.List(Array.ConvertAll(actions[i], s => (object)s));
                        return PassChallenges.List(items);
                    }
                default: return value;
            }
        }
    }
}
