using System;
using Ro3Emu.Server.Game;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    internal static class PetShopClient
    {
        internal static void Inject(Obf_R.Obf_ff manager, Obf_R.Obf_ff global)
        {
            if (!Job50Client.ServerRecognized) return;
            var config = manager.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_ShopCommodityConfig");
            var rows = PassChallenges.Values(manager, "m_kCFG_ShopCommodityConfig");
            if (config == null || rows == null) return;
            var rawget = global.Obf_LuA<Obf_R.Obf_Ef>("rawget");
            if (CustomNpcClient.Exists(rawget, rows, PetShopCatalog.Commodity(PetShopCatalog.Items[PetShopCatalog.Items.Length - 1]))
                && LuaPatches.ToLong(config.Obf_RuA<object>("_ro3PetShopDone")) == 1) return;
            var template = rows.Obf_ruA<long, Obf_R.Obf_ff>(PetShopCatalog.Template);
            if (template == null) return;
            try
            {
                for (int i = 0; i < PetShopCatalog.Items.Length; i++)
                {
                    long item = PetShopCatalog.Items[i], id = PetShopCatalog.Commodity((int)item);
                    if (CustomNpcClient.Exists(rawget, rows, id))
                    {
                        var existing = rows.Obf_ruA<long, Obf_R.Obf_ff>(id);
                        if (LuaPatches.ToLong(existing.Obf_RuA<object>("_ro3PetShop")) == item) continue;
                        throw new InvalidOperationException("commodity collision " + id);
                    }
                    var row = CustomNpcClient.Inherit(global, template);
                    PassChallenges.RawSet(row, "_ro3PetShop", item);
                    PassChallenges.RawSet(row, "_iID", id);
                    PassChallenges.RawSet(row, "_iItemId", item);
                    PassChallenges.RawSet(row, "_iSortId", (long)i + 6);
                    PassChallenges.RawSet(rows, id, row);
                }
                var rebuild = config.Obf_RuA<Obf_R.Obf_Ef>("OnInitDatas");
                if (rebuild == null) return;
                rebuild.Obf_HuA(config);
                PassChallenges.RawSet(config, "_ro3PetShopDone", 1L);
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.PetShop] " + e.Message); }
        }
    }
}
