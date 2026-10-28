using System;
using System.Text;
using HappyEngine.Base;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The character's level-up sprite on other units (a monster that evolved). The client plays fx_levelup.prefab by name from
    /// its level-up panel (UI_Sys_MainLevelUpPanelCtrl: PublicParamConfig.PersonaLevelUpResource, Lua_RD_Utils.AllocGfx), so no
    /// effect row names it and a server cannot ask for it with SkillEffectDisplayNotify. Effects the server does ask for are
    /// looked up by id in the table the client's ServerLocation "GetEffectInfo" reads (lua_config_effectdata, which the
    /// ReloadSkillData call replaces with a freshly required copy); this adds one row of our own to it, <see cref="EffectId"/>,
    /// in the shape of the game's own single-gfx rows (e.g. 40018): the prefab on the unit's DRoot for three seconds.
    /// Nothing original is touched: a new id, set with rawset, and put back whenever the table is replaced.
    /// The client then tells the server it has the row (game message <see cref="MsgId"/>, repeated while it plays): a client
    /// without this patch never sends it, and the server keeps showing it the effects that exist in the original data.
    /// </summary>
    public static class LevelUpEffect
    {
        /// <summary>Must equal MonsterService.LevelUpFxHello.</summary>
        public const uint MsgId = 77017;

        /// <summary>Must equal MonsterService.LevelUpFxId; not in the exported EffectData (its ids end near 1.2 billion).</summary>
        public const long EffectId = 990000001;

        private const string ConfigModule = "lua_config_effectdata";
        private const string Prefab = "fx_levelup.prefab";
        private const double Seconds = 3;

        private static Obf_R.Obf_ff _global;
        private static int _ticks;
        private static bool _logged;

        /// <summary>From LuaPatches.Tick, once per LuaEnv, after PassChallenges.Install (its table helpers need that LuaEnv).</summary>
        internal static void Install(Obf_R.Obf_ff global)
        {
            _global = global;
            _ticks = 0;
            _logged = false;
            try { Report(Present() || Inject()); }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.LevelUpFx] install failed: " + e.Message); }
        }

        /// <summary>From UiTweaks.Tick (every 10th frame): about every 5 s checks the row, and tells the server now and then.</summary>
        internal static void Tick()
        {
            if (_global == null || ++_ticks % 30 != 0) return;
            try
            {
                bool ready = Present() || Inject();
                Report(ready);
                if (!ready) return;
                int period = _ticks / 30;
                if (period <= 3 || period % 4 == 0) Hello(); // the first may precede the connection: a few early ones, then every ~20 s
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.LevelUpFx] " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Report(bool ready)
        {
            if (ready == _logged) return;
            _logged = ready;
            if (ready) Debug.Log("[Ro3Emu.LevelUpFx] effect " + EffectId + " plays " + Prefab);
        }

        /// <summary>The game's effect table, only once the client has loaded it (GetEffectInfo captured that same table).</summary>
        private static Obf_R.Obf_ff Effects() => _global?.Obf_LuA<Obf_R.Obf_ff>("package.loaded." + ConfigModule);

        private static bool Present()
        {
            var effects = Effects();
            return effects != null && _global.Obf_LuA<Obf_R.Obf_Ef>("rawget").Obf_HuA(effects, EffectId) is { Length: > 0 } got && got[0] != null;
        }

        private static bool Inject()
        {
            var effects = Effects();
            if (effects == null) return false;
            var gfx = PassChallenges.Row(
                "m_fGfxTime", Seconds, "m_iCasterTargetEffect", 1L, "m_iMulTypeId", 0L,
                "m_kBindName", "DRoot", "m_kBindType", 1L, "m_kGfxName", Prefab);
            PassChallenges.RawSet(effects, EffectId, PassChallenges.Row("m_kPoint", PassChallenges.Row("m_kGfxList", PassChallenges.List(gfx))));
            return Present();
        }

        private static void Hello() =>
            SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(MsgId, Encoding.UTF8.GetBytes("fx"));
    }
}
