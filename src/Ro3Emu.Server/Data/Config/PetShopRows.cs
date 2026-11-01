using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server.Data.Config;

internal static class PetShopRows
{
    internal static void AddTo(Dictionary<string, JsonElement> table)
    {
        if (!table.TryGetValue(PetShopCatalog.Template.ToString(), out var source)) return;
        for (int i = 0; i < PetShopCatalog.Items.Length; i++)
        {
            int item = PetShopCatalog.Items[i], id = PetShopCatalog.Commodity(item);
            string key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (table.ContainsKey(key)) throw new InvalidOperationException($"Pet shop commodity {id} collides with client data.");
            var row = JsonNode.Parse(source.GetRawText())!.AsObject();
            row["_iID"] = id;
            row["_iItemId"] = item;
            row["_iSortId"] = i + 6;
            table.Add(key, JsonSerializer.SerializeToElement(row));
        }
    }
}
