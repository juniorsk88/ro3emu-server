using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// (not original behaviour) The "Challenge" button of a ten-player dungeon (Realm of the Gods, UI_Sys_Dungeon_RightInfoWidgetCtrl,
    /// enum_DungeonType.Ten) refuses a role that has no team before the server hears of it: _readyToEnterDungeon pops UIWords
    /// 56192 ("Please join a party/raid before entering the Dungeon") without a team and 56153 ("Not enough members") below the
    /// boss's minimum, and only a full enough team sends EnterDungeonReq (LC_DAL_MultiPlayerDungeon.SendEnterDungeon, message 30400).
    /// This hook wraps _readyToEnterDungeon: a role that is alone (no team, or a team of one) sends that same request itself, and
    /// the server decides. A server without the Ro3Emu:DungeonSolo switch (or the GM aid) answers DungeonTeamSize, so the entry
    /// stays closed there exactly as the original game has it; a team of two or more keeps the original checks and messages.
    /// The class table is wrapped (BaseClass's __newindex writes the shared vtbl, so widgets already open switch too).
    /// Second hook, same class: a five-player dungeon's _checkToEnterDungeon asks "Aviso da Busca: the party has fewer than 5
    /// Members. Match with teammates?" (UIWords 56237, buttons "Join Now"/"Start Search") whenever the party is below the
    /// maximum; a party that already meets the minimum goes straight in with the same request instead of asking.
    /// </summary>
    public static class SoloDungeonEntry
    {
        private static Obf_R.Obf_Ef _original;
        private static Obf_R.Obf_Ef _originalCheck;
        private static Obf_R.Obf_ff _dal;
        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly LuaPatches.SelfMethod Wrapper = ReadyToEnter;
        private static readonly LuaPatches.SelfMethod CheckWrapper = CheckToEnter;

        /// <summary>From LuaPatches.Tick, once per LuaEnv, with the loaded ui_sys_dungeon_rightinfowidgetctrl and lc_dal_multiplayerdungeon modules.</summary>
        internal static void Install(Obf_R.Obf_ff widget, Obf_R.Obf_ff multiplayerDungeon)
        {
            _original = null;
            _originalCheck = null;
            _dal = null;
            if (widget == null || multiplayerDungeon == null)
            {
                Debug.LogWarning("[Ro3Emu.SoloDungeon] dungeon panel or multi-player dungeon module not found; the Challenge button keeps the original team check");
                return;
            }
            var original = widget.Obf_RuA<Obf_R.Obf_Ef>("_readyToEnterDungeon");
            if (original == null || multiplayerDungeon.Obf_RuA<Obf_R.Obf_Ef>("SendEnterDungeon") == null)
            {
                Debug.LogWarning("[Ro3Emu.SoloDungeon] _readyToEnterDungeon/SendEnterDungeon not found; the Challenge button keeps the original team check");
                return;
            }
            _original = original;
            _dal = multiplayerDungeon;
            widget.Obf_luA<string, LuaPatches.SelfMethod>("_readyToEnterDungeon", Wrapper);
            var check = widget.Obf_RuA<Obf_R.Obf_Ef>("_checkToEnterDungeon");
            if (check != null)
            {
                _originalCheck = check;
                widget.Obf_luA<string, LuaPatches.SelfMethod>("_checkToEnterDungeon", CheckWrapper);
            }
            else Debug.LogWarning("[Ro3Emu.SoloDungeon] _checkToEnterDungeon not found; an incomplete five-player party keeps the search prompt");
            Debug.Log("[Ro3Emu.SoloDungeon] Challenge button of ten-player dungeons wrapped: a role alone asks the server to enter");
        }

        private static void ReadyToEnter(Obf_R.Obf_ff self)
        {
            try { if (EnterAlone(self)) return; }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.SoloDungeon] " + e.GetType().Name + ": " + e.Message); }
            _original?.Obf_HuA(self);
        }

        private static void CheckToEnter(Obf_R.Obf_ff self)
        {
            try { if (EnterIncompleteParty(self)) return; }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.SoloDungeon] " + e.GetType().Name + ": " + e.Message); }
            _originalCheck?.Obf_HuA(self);
        }

        /// <summary>True when the party is below the maximum but already meets the minimum and the entry request was sent.</summary>
        private static bool EnterIncompleteParty(Obf_R.Obf_ff self)
        {
            var dal = _dal;
            var data = self.Obf_RuA<Obf_R.Obf_ff>("m_kData");
            if (dal == null || data == null) return false;
            if (!(self.Obf_RuA<object>("m_bInTeam") is bool inTeam && inTeam)) return false;
            long members = LuaPatches.ToLong(self.Obf_RuA<object>("m_iTeamMemberNum"));
            long min = LuaPatches.ToLong(self.Obf_RuA<object>("m_iMinLimit"));
            long max = LuaPatches.ToLong(self.Obf_RuA<object>("m_iMaxLimit"));
            if (members < min || members >= max) return false; // below the minimum or already full: the original path
            var send = dal.Obf_RuA<Obf_R.Obf_Ef>("SendEnterDungeon");
            if (send == null) return false;
            send.Obf_HuA(dal, data.Obf_RuA<object>("_iDungeon"), data.Obf_RuA<object>("iMultiDungeonId"), data.Obf_RuA<object>("iMarkId"));
            Debug.Log("[Ro3Emu.SoloDungeon] party of " + members + " (min " + min + ", max " + max + "): entry requested without the search prompt");
            return true;
        }

        /// <summary>True when the role is alone and the entry request was sent.</summary>
        private static bool EnterAlone(Obf_R.Obf_ff self)
        {
            var dal = _dal;
            var data = self.Obf_RuA<Obf_R.Obf_ff>("m_kData");
            if (dal == null || data == null) return false;
            bool inTeam = self.Obf_RuA<object>("m_bInTeam") is bool b && b;
            if (inTeam && LuaPatches.ToLong(self.Obf_RuA<object>("m_iTeamMemberNum")) > 1) return false;
            var send = dal.Obf_RuA<Obf_R.Obf_Ef>("SendEnterDungeon");
            if (send == null) return false;
            // L9_1:SendEnterDungeon(m_kData._iDungeon, m_kData.iMultiDungeonId, m_kData.iMarkId)
            send.Obf_HuA(dal, data.Obf_RuA<object>("_iDungeon"), data.Obf_RuA<object>("iMultiDungeonId"), data.Obf_RuA<object>("iMarkId"));
            Debug.Log("[Ro3Emu.SoloDungeon] alone: entry requested for dungeon " + LuaPatches.ToLong(data.Obf_RuA<object>("_iDungeon"))
                + " multi " + LuaPatches.ToLong(data.Obf_RuA<object>("iMultiDungeonId")) + " mark " + LuaPatches.ToLong(data.Obf_RuA<object>("iMarkId")));
            return true;
        }
    }
}
