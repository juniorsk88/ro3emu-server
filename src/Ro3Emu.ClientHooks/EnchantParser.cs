using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    // Skill-only enchantments carry no attribute entries; the client decodes empty maps as nil.
    internal static class EnchantParser
    {
        public delegate object Parse(Obf_R.Obf_ff info);
        private static readonly Parse Wrapper = ParseEquip;
        private static Obf_R.Obf_Ef _original, _next, _rawset;
        private static Obf_R.Obf_Cf _env;
        private static bool _reported;

        internal static void Install(Obf_R.Obf_Cf env, Obf_R.Obf_ff global, Obf_R.Obf_ff parser)
        {
            _env = env;
            _original = parser?.Obf_RuA<Obf_R.Obf_Ef>("ParseEquipInfo");
            _next = global.Obf_LuA<Obf_R.Obf_Ef>("next");
            _rawset = global.Obf_LuA<Obf_R.Obf_Ef>("rawset");
            if (_original != null && _next != null && _rawset != null) parser.Obf_luA<string, Parse>("ParseEquipInfo", Wrapper);
        }

        private static object ParseEquip(Obf_R.Obf_ff info)
        {
            try
            {
                foreach (string name in new[] { "enchantment", "playBookEnchantment" })
                {
                    var map = info?.Obf_RuA<Obf_R.Obf_ff>(name);
                    if (map == null) continue;
                    object key = null;
                    while (true)
                    {
                        var pair = _next.Obf_HuA(map, key);
                        if (pair == null || pair.Length < 2 || pair[0] == null) break;
                        key = pair[0];
                        if (pair[1] is not Obf_R.Obf_ff entry) continue;
                        var attrs = entry.Obf_RuA<Obf_R.Obf_ff>("attr");
                        if (attrs == null) entry.Obf_luA<string, Obf_R.Obf_ff>("attr", _env.Obf_wTA());
                        else if (LuaPatches.ToLong(attrs.Obf_ruA<long, object>(0L)) == 0)
                            _rawset.Obf_HuA(attrs, 0L, null);
                    }
                }
            }
            catch (Exception e)
            {
                if (!_reported) Debug.LogWarning("[Ro3Emu.EnchantParser] " + e.Message);
                _reported = true;
            }
            var result = _original?.Obf_HuA(info);
            return result != null && result.Length > 0 ? result[0] : null;
        }
    }
}
