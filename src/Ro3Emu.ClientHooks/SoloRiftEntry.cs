using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>Allow a lone player to ask the server to enter a Rift using the original scene request.</summary>
    public static class SoloRiftEntry
    {
        public delegate void Enter(object stone, object scene);
        private static readonly Enter Wrapper = EnterRift;
        private static Obf_R.Obf_ff _global, _team;
        private static Obf_R.Obf_Ef _original;

        internal static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff actions, Obf_R.Obf_ff team)
        {
            _global = global;
            _team = team;
            _original = actions?.Obf_RuA<Obf_R.Obf_Ef>("OnePieceEnter");
            if (_original != null && _team?.Obf_RuA<Obf_R.Obf_Ef>("IsInTeam") != null)
                actions.Obf_luA<string, Enter>("OnePieceEnter", Wrapper);
        }

        private static void EnterRift(object stone, object scene)
        {
            try
            {
                var inTeam = _team.Obf_RuA<Obf_R.Obf_Ef>("IsInTeam")?.Obf_HuA();
                var sceneManager = _global.Obf_RuA<Obf_R.Obf_ff>("LC_SceneManager");
                var send = sceneManager?.Obf_RuA<Obf_R.Obf_Ef>("SendEnterDungeonWithOnePieceData");
                if (inTeam != null && inTeam.Length > 0 && inTeam[0] is bool grouped && !grouped
                    && long.TryParse(Convert.ToString(stone), out long stoneId) && stoneId > 0
                    && long.TryParse(Convert.ToString(scene), out long sceneId) && sceneId > 0 && send != null)
                {
                    send.Obf_HuA(stone, scene);
                    return;
                }
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.SoloRift] " + e.Message); }
            _original?.Obf_HuA(stone, scene);
        }
    }
}
