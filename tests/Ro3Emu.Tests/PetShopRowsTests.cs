using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class PetShopRowsTests
{
    [Fact]
    public void AdditionalOffersKeepTemplatePriceAndLimitAndHaveDistinctIds()
    {
        var template = JsonSerializer.SerializeToElement(new
        {
            _iID = PetShopCatalog.Template, _iItemId = 42, _iClassID = 12,
            _iPurchaseNum = 9, _iPurchaseTimesLimit = 3, _kItemPerPrice = new[] { 17, 23 }
        });
        var rows = new Dictionary<string, JsonElement> { [PetShopCatalog.Template.ToString()] = template };
        PetShopRows.AddTo(rows);
        Assert.Equal(PetShopCatalog.Items.Length + 1, rows.Count);
        foreach (int item in PetShopCatalog.Items)
        {
            int id = PetShopCatalog.Commodity(item);
            var row = rows[id.ToString()];
            Assert.Equal(id, row.GetProperty("_iID").GetInt32());
            Assert.Equal(item, row.GetProperty("_iItemId").GetInt32());
            Assert.Equal(12, row.GetProperty("_iClassID").GetInt32());
            Assert.Equal(9, row.GetProperty("_iPurchaseNum").GetInt32());
            Assert.Equal(3, row.GetProperty("_iPurchaseTimesLimit").GetInt32());
            Assert.Equal(new[] { 17, 23 }, row.GetProperty("_kItemPerPrice").EnumerateArray().Select(x => x.GetInt32()));
        }
        Assert.Equal(42, rows[PetShopCatalog.Template.ToString()].GetProperty("_iItemId").GetInt32());
    }

    [Fact]
    public void ExistingCommodityIsNeverOverwritten()
    {
        var template = JsonSerializer.SerializeToElement(new { _iID = PetShopCatalog.Template });
        var rows = new Dictionary<string, JsonElement>
        {
            [PetShopCatalog.Template.ToString()] = template,
            [PetShopCatalog.Commodity(PetShopCatalog.Items[0]).ToString()] = template
        };
        Assert.Throws<InvalidOperationException>(() => PetShopRows.AddTo(rows));
        Assert.Equal(template.GetRawText(), rows[PetShopCatalog.Commodity(PetShopCatalog.Items[0]).ToString()].GetRawText());
    }
}
