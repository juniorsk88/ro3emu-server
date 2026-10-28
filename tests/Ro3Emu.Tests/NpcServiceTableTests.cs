using System.Text.Json;
using Ro3Emu.ClientHooks;

namespace Ro3Emu.Tests;

/// <summary>
/// The service options the client hooks add to NPC dialogues (src/Ro3Emu.ClientHooks/NpcServiceTable.cs). The rows have to
/// carry every field the client's own rows have (the dialogue panel drops an option whose _iCondition is not 0), and the
/// table has to match the client's data: NPC names, row counts, shop parameters, interactions.
/// </summary>
public class NpcServiceTableTests
{
    // The fields the client's DialogueConfig rows end up with: the ones a row lists plus the shared defaults
    // (Config/DataConfig/DialogueConfig, its defaults table: _iSortID 1, _iType 0, _iNPCSpine "", ..., _iCondition 0, _iBubbleType "").
    private static readonly string[] ClientRowFields =
    {
        "_iID", "_iSortID", "_iType", "_iNPCID", "_iNPCSpine", "_iTalkNPCID", "_kTalkNPCImage", "_iNPCAnimation", "_iTextType",
        "_iText", "_kVoice", "_iIcon", "_iCondition", "_iEXECAction", "_iBubbleType",
    };

    [Fact]
    public void OptionRowsCarryEveryFieldTheClientRowsHave()
    {
        foreach (NpcService service in Enum.GetValues<NpcService>())
        {
            var options = NpcServiceTable.Options(service);
            Assert.Equal(2, options.Length);
            for (int i = 0; i < options.Length; i++)
            {
                var fields = NpcServiceTable.OptionFields(10016301, 100163, 2 + i, options[i]);
                Assert.Equal(ClientRowFields.OrderBy(f => f), fields.Select(f => f.Key).OrderBy(f => f));
                var byName = fields.ToDictionary(f => f.Key, f => f.Value);
                Assert.Equal(0L, byName["_iCondition"]); // not nil, not another condition: the panel would drop the option
                Assert.Equal(2L, byName["_iTextType"]);  // an option row
                Assert.Equal((long)(2 + i), byName["_iSortID"]);
                Assert.Equal(10016301L, byName["_iID"]);
                Assert.Equal(new[] { options[i].Text }, (long[])byName["_iText"]);
                Assert.NotEmpty((string)byName["_iIcon"]);
                Assert.NotEmpty((string[][])byName["_iEXECAction"]);
            }
        }
    }

    [Fact]
    public void ALineRowIsANormalRowWithEveryField()
    {
        var fields = NpcServiceTable.LineFields(10031101, 100311, 10370000708);
        Assert.Equal(ClientRowFields.OrderBy(f => f), fields.Select(f => f.Key).OrderBy(f => f));
        var byName = fields.ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal(1L, byName["_iTextType"]);
        Assert.Equal(1L, byName["_iSortID"]);
        Assert.Equal(0L, byName["_iCondition"]);
    }

    [Fact]
    public void EveryServiceOpensItsPanelThenClosesTheDialogueAndLastComesLeave()
    {
        foreach (NpcService service in Enum.GetValues<NpcService>())
        {
            var options = NpcServiceTable.Options(service);
            var open = options[0].Actions;
            Assert.Equal("25", open[0][0]);                                  // OpenFunctionUI
            Assert.Equal(new[] { "8", "TOPBAR", "UI_Sys_NpcDialogPanel" }, open[1]); // CloseUI of the dialogue
            Assert.Equal(NpcServiceTable.LeaveText, options[1].Text);
            Assert.Equal(new[] { new[] { "8", "TOPBAR", "UI_Sys_NpcDialogPanel" } }, options[1].Actions);
        }
    }

    [Fact]
    public void TheTableHasNoDuplicatesAndFollowsTheNpcDialogueNumbering()
    {
        var ids = NpcServiceTable.Dialogues.Select(d => d.Dialogue).Concat(NpcServiceTable.Redirects.Select(r => r.Dialogue)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var d in NpcServiceTable.Dialogues) Assert.Equal(d.Npc * 100 + 1, d.Dialogue);
        foreach (var r in NpcServiceTable.Redirects) Assert.Equal(r.Npc * 100 + 1, r.Dialogue);
    }

    // ------------------------------------------------------------ against the client's data (data/config; skipped without it)

    private sealed class ClientData
    {
        public readonly JsonElement Dialogues, Npcs, Interactions, Localization, Shops, Functions;
        public ClientData(string dir)
        {
            JsonElement Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, name))).RootElement;
            Dialogues = Load("DialogueConfig.json"); Npcs = Load("NPCConfig.json"); Interactions = Load("NPCInteractConfig.json");
            Localization = Load("Localization_en.json"); Shops = Load("ShopConfig.json"); Functions = Load("FunctionConfig.json");
        }

        public string Text(long id) => Localization.TryGetProperty(id.ToString(), out var row) ? row.GetProperty("_kDes").GetString()! : "";

        public List<JsonElement> Rows(long dialogue) =>
            Dialogues.TryGetProperty(dialogue.ToString(), out var rows) && rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray().ToList() : new();

        /// <summary>The dialogue ids an NPC's interactions start (action 53), in order.</summary>
        public List<string> DialoguesOf(JsonElement npc)
        {
            var found = new List<string>();
            // (an NPC with none has "_iInteractID": {})
            if (!npc.TryGetProperty("_iInteractID", out var interacts) || interacts.ValueKind != JsonValueKind.Array) return found;
            foreach (var interact in interacts.EnumerateArray())
            {
                if (!Interactions.TryGetProperty(interact.GetInt64().ToString(), out var record)) continue;
                foreach (var action in record.GetProperty("_iEXECAction").EnumerateArray())
                    if (action.GetArrayLength() > 1 && action[0].GetString() == "53") found.Add(action[1].GetString()!);
            }
            return found;
        }
    }

    private static ClientData? Data()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        var config = dir == null ? null : Path.Combine(dir.FullName, "data", "config");
        return config != null && File.Exists(Path.Combine(config, "DialogueConfig.json")) ? new ClientData(config) : null;
    }

    [Fact]
    public void EveryTableEntryIsTheNpcItNamesWithTheDialogueAndRowsTheDataGives()
    {
        var data = Data();
        if (data == null) return; // the exported game data is not part of the source tree on every machine
        foreach (var entry in NpcServiceTable.Dialogues)
        {
            var rows = data.Rows(entry.Dialogue);
            Assert.True(rows.Count == entry.Rows, $"{entry.Dialogue}: {rows.Count} rows in the data, {entry.Rows} in the table");
            Assert.DoesNotContain(rows, r => r.TryGetProperty("_iTextType", out var type) && type.GetInt64() == 2); // no option yet
            // Chatter only: the plain dialogue panel (types 1 to 3 are other panels) and no row that runs an action.
            Assert.DoesNotContain(rows, r => r.TryGetProperty("_iType", out var kind) && kind.GetInt64() != 0);
            Assert.DoesNotContain(rows, r => r.TryGetProperty("_iEXECAction", out var actions)
                && actions.EnumerateArray().Any(a => a.GetArrayLength() > 0 && a[0].GetString() != ""));
            Assert.Equal(entry.Npc, rows[0].GetProperty("_iNPCID").GetInt64());

            Assert.True(data.Npcs.TryGetProperty(entry.Npc.ToString(), out var npc), $"NPC {entry.Npc} not in NPCConfig");
            Assert.Equal(entry.Name, data.Text(npc.GetProperty("_kName")[0].GetInt64()));
            Assert.Contains(entry.Dialogue.ToString(), data.DialoguesOf(npc)); // the NPC really starts this dialogue

            // ... and no other NPC does (a shared dialogue would hand the options to NPCs of another kind).
            var users = data.Npcs.EnumerateObject()
                .Where(n => n.Value.ValueKind == JsonValueKind.Object && data.DialoguesOf(n.Value).Contains(entry.Dialogue.ToString()))
                .Select(n => n.Name).ToList();
            Assert.Equal(new[] { entry.Npc.ToString() }, users);
        }
    }

    [Fact]
    public void NoVendorOrMerchantOfTheDataIsLeftWithoutAnOption()
    {
        var data = Data();
        if (data == null) return;
        // The job-advance mentor is a Merchant Mentor, not a seller.
        var notSellers = new HashSet<string> { "Merchant Mentor" };
        var covered = NpcServiceTable.Dialogues.Select(d => d.Npc).Concat(NpcServiceTable.Redirects.Select(r => r.Npc)).ToHashSet();
        var missing = new List<string>();
        foreach (var npc in data.Npcs.EnumerateObject().Where(n => n.Value.ValueKind == JsonValueKind.Object))
        {
            if (!npc.Value.TryGetProperty("_kName", out var nameIds) || nameIds.ValueKind != JsonValueKind.Array || nameIds.GetArrayLength() == 0) continue;
            var name = data.Text(nameIds[0].GetInt64());
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\b(Vendor|Merchant)\b") || notSellers.Contains(name)) continue;
            foreach (var dialogue in data.DialoguesOf(npc.Value))
            {
                var rows = data.Rows(long.Parse(dialogue));
                bool hasOption = rows.Any(r => r.TryGetProperty("_iTextType", out var type) && type.GetInt64() == 2);
                if (!hasOption && !covered.Contains(long.Parse(npc.Name))) missing.Add($"{name} {npc.Name} (dialogue {dialogue})");
            }
        }
        Assert.True(missing.Count == 0, "sellers without an option: " + string.Join(", ", missing));
    }

    [Fact]
    public void ShopOptionsOpenAShopTheDataDefinesWithItsFunction()
    {
        var data = Data();
        if (data == null) return;
        var shops = data.Shops.EnumerateObject().Where(s => s.Value.ValueKind == JsonValueKind.Object).Select(s => s.Value).ToList();
        foreach (var service in new[] { NpcService.Supply, NpcService.PetShop, NpcService.Headwear, NpcService.Hairstyle })
        {
            var a = NpcServiceTable.Options(service)[0].Actions[0]; // 25, function, layer, panel, type, id, class
            Assert.Equal("UI_Sys_ShopPanel", a[3]);
            Assert.Contains(shops, s => s.GetProperty("_iFunctionId").GetInt64().ToString() == a[1]
                && s.GetProperty("_iShopType").GetInt64().ToString() == a[4]
                && s.GetProperty("_iShopId").GetInt64().ToString() == a[5]
                && s.GetProperty("_iClassID").GetInt64().ToString() == a[6]);
        }
    }

    [Fact]
    public void RefineOpensTheEquipmentPanelsRefinePageAndTheTextsExist()
    {
        var data = Data();
        if (data == null) return;
        var refine = NpcServiceTable.Options(NpcService.Refine)[0].Actions[0];
        Assert.Equal(new[] { "25", "8", "TOPBAR", "UI_Sys_StrengthenPanel", "2" }, refine);
        // Function 8 is "Refine", and its name is the text the option shows.
        Assert.True(data.Functions.TryGetProperty("8", out var function));
        Assert.Equal(NpcServiceTable.RefineText, function.GetProperty("_iName")[0].GetInt64());
        Assert.Equal("Refine", data.Text(NpcServiceTable.RefineText));
        Assert.Equal("Open Shop", data.Text(NpcServiceTable.OpenShopText));
        Assert.Equal("Leave", data.Text(NpcServiceTable.LeaveText));
    }

    [Fact]
    public void MenuServicesOpenTheFunctionTheirOptionIsNamedAfterWithThePanelTheMenuOpens()
    {
        var data = Data();
        if (data == null) return;
        // (service, function id, English name of the function and of the option text, the panel the main menu opens for it:
        // LC_FunctionHelper.FunctionOpenPanelInfo, whose entries for these three carry no initial data)
        var expected = new[]
        {
            (NpcService.Wardrobe, "32", "Wardrobe", "UI_Sys_WardrobePanel"),
            (NpcService.Guild, "13", "Guild", "UI_Sys_GuildPanel"),
            (NpcService.Refine, "8", "Refine", "UI_Sys_StrengthenPanel"),
        };
        foreach (var (service, function, name, panel) in expected)
        {
            var option = NpcServiceTable.Options(service)[0];
            Assert.Equal("25", option.Actions[0][0]);
            Assert.Equal(function, option.Actions[0][1]);
            Assert.Equal("TOPBAR", option.Actions[0][2]);
            Assert.Equal(panel, option.Actions[0][3]);
            Assert.True(data.Functions.TryGetProperty(function, out var row));
            Assert.Equal(option.Text, row.GetProperty("_iName")[0].GetInt64());
            Assert.Equal(name, data.Text(option.Text));
        }
        // Wardrobe and Guild take no parameters at all; Refine takes only the page.
        Assert.Equal(4, NpcServiceTable.Options(NpcService.Wardrobe)[0].Actions[0].Length);
        Assert.Equal(4, NpcServiceTable.Options(NpcService.Guild)[0].Actions[0].Length);
    }

    [Fact]
    public void TheRedirectedNpcSharesItsInteractionAndHasAnUnusedStubOfItsOwn()
    {
        var data = Data();
        if (data == null) return;
        foreach (var redirect in NpcServiceTable.Redirects)
        {
            Assert.True(data.Npcs.TryGetProperty(redirect.Npc.ToString(), out var npc));
            Assert.Equal(redirect.Name, data.Text(npc.GetProperty("_kName")[0].GetInt64()));
            Assert.Equal(new[] { redirect.SharedDialogue.ToString() }, data.DialoguesOf(npc)); // today: the shared line
            var users = data.Npcs.EnumerateObject()
                .Where(n => n.Value.ValueKind == JsonValueKind.Object && data.DialoguesOf(n.Value).Contains(redirect.Dialogue.ToString())).ToList();
            Assert.Empty(users); // the stub is unused
            var stub = data.Rows(redirect.Dialogue);
            Assert.Single(stub);
            Assert.False(stub[0].TryGetProperty("_iText", out _)); // and has no text of its own
            Assert.Equal(redirect.Npc, stub[0].GetProperty("_iNPCID").GetInt64());
            // The interaction with that id exists and starts that dialogue, like the NPCs' own.
            Assert.True(data.Interactions.TryGetProperty(redirect.Dialogue.ToString(), out var interaction));
            Assert.Equal(redirect.Dialogue.ToString(), interaction.GetProperty("_iEXECAction")[0][1].GetString());
            Assert.NotEqual("", data.Text(redirect.Text));
            // The text is the one the other Item Merchants say.
            Assert.Contains(data.Rows(10001901), r => r.GetProperty("_iText")[0].GetInt64() == redirect.Text);
        }
    }
}
