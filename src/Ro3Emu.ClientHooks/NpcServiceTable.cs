using System.Collections.Generic;

namespace Ro3Emu.ClientHooks
{
    internal enum NpcService { Supply, PetShop, Headwear, Hairstyle, Refine, Wardrobe, Guild }

    /// <summary>One NPC dialogue that gets service options. <see cref="Rows"/> is the number of rows it has in the client's data.</summary>
    internal sealed class NpcDialogue
    {
        public readonly string Name; // the NPC's English name in Localization (the test reads it back, so a wrong id is caught)
        public readonly long Dialogue;
        public readonly int Rows;
        public readonly NpcService Service;
        public NpcDialogue(string name, long dialogue, int rows, NpcService service)
        {
            Name = name; Dialogue = dialogue; Rows = rows; Service = service;
        }

        /// <summary>Every NPC here has the dialogue id NPC id * 100 + 1.</summary>
        public long Npc => Dialogue / 100;
    }

    /// <summary>
    /// An NPC whose interaction is the one eight NPCs share (NPCInteractConfig 10002301: a line of unrelated chatter), while
    /// the data also holds a stub of its own (interaction and dialogue 10031101, one row with no text). Its _iInteractID is
    /// pointed at the stub and the stub gets a row of text and the service options.
    /// </summary>
    internal sealed class NpcRedirect
    {
        public readonly string Name;
        public readonly long Npc, Dialogue, SharedDialogue, Text;
        public readonly NpcService Service;
        public NpcRedirect(string name, long npc, long dialogue, long sharedDialogue, long text, NpcService service)
        {
            Name = name; Npc = npc; Dialogue = dialogue; SharedDialogue = sharedDialogue; Text = text; Service = service;
        }
    }

    /// <summary>One option row appended to a dialogue: client text id, icon and the action list (each action: id, arguments...).</summary>
    internal sealed class NpcOption
    {
        public readonly long Text;
        public readonly string Icon;
        public readonly string[][] Actions;
        public NpcOption(long text, string icon, string[][] actions) { Text = text; Icon = icon; Actions = actions; }
    }

    /// <summary>
    /// Which NPCs of the client's data get which service. The client has the services (shops, the equipment panel) and the
    /// server sells and refines; only the NPCs were never linked to them (their DialogueConfig is one line of chatter, and the
    /// original data has no NPC to shop link besides Kafra). The shops are the ones the Shop menu opens, with the parameters
    /// the shop panel reads (shop type, shop id, class id: UI_Sys_ShopPanelCtrl.OnInitOpenParams) and the function ids of
    /// ShopConfig, so the client's own level gates (FunctionConfig _kOpenCondition) still apply. No client code is involved
    /// beyond the dialogue's own option list; no extra access is granted, a locked function just shows the client's tip.
    /// </summary>
    internal static class NpcServiceTable
    {
        public const long OpenShopText = 10370000673; // "Open Shop"
        public const long LeaveText = 10370000679;    // "Leave"
        public const long RefineText = 10160000006;   // FunctionConfig 8 name: "Refine"
        public const long GuildText = 10160000011;    // FunctionConfig 13 name: "Guild"
        public const long WardrobeText = 10160000029; // FunctionConfig 32 name: "Wardrobe"

        public const string ShopIcon = "npcdialog_icon_shop.png", ChatIcon = "npcdialog_icon_chat.png", LeaveIcon = "npcdialog_icon_leave.png";

        public static readonly NpcDialogue[] Dialogues =
        {
            // Supply Shop 140 (consumables, wings), function 70105.
            new NpcDialogue("Item Merchant", 10001901, 1, NpcService.Supply),
            new NpcDialogue("Item Merchant", 10014301, 1, NpcService.Supply),
            new NpcDialogue("Item Merchant", 10014401, 1, NpcService.Supply),
            new NpcDialogue("Item Merchant", 10017001, 1, NpcService.Supply),
            // Pet Shop 210, function 70113.
            new NpcDialogue("Pet Merchant", 10006401, 1, NpcService.PetShop),
            new NpcDialogue("Pet Merchant", 10014501, 1, NpcService.PetShop),
            new NpcDialogue("Pet Merchant", 10016601, 1, NpcService.PetShop),
            new NpcDialogue("Pet Merchant", 20000901, 1, NpcService.PetShop),
            // Appearance Shop, headwear class 50204, function 70505.
            new NpcDialogue("Headwear Merchant", 40005101, 1, NpcService.Headwear),
            // Appearance Shop, hairstyle class 50203, function 70504.
            new NpcDialogue("Stylist", 10032301, 1, NpcService.Hairstyle),
            // Equipment panel, refine page (function 8). Leavren and Sebuyalian speak of "Enhance", whose function (9) the data
            // keeps closed (open condition: level 9999); refine is the equivalent that is open.
            new NpcDialogue("Antonio", 10016301, 1, NpcService.Refine),
            new NpcDialogue("Hollgrehenn", 20000701, 1, NpcService.Refine),
            new NpcDialogue("Leavren", 10015201, 1, NpcService.Refine),
            new NpcDialogue("Sebuyalian", 10016401, 1, NpcService.Refine),

            // The other sellers (the ones named Vendor or Merchant, the shop clerk and the NPCs whose line says they sell): the
            // Supply Shop, the consumables shop, as a default. A dialogue of two or three lines gets the options after its last
            // line, where the options of the dialogues that have them are. None of these dialogues is shared with another NPC.
            new NpcDialogue("Vendor", 10029701, 1, NpcService.Supply),
            new NpcDialogue("Vendor", 10029801, 1, NpcService.Supply),
            new NpcDialogue("Vendor", 10029901, 2, NpcService.Supply),
            new NpcDialogue("Food Vendor", 10029001, 2, NpcService.Supply),
            new NpcDialogue("Map Vendor", 10029101, 2, NpcService.Supply),
            new NpcDialogue("Merchant", 10013801, 2, NpcService.Supply),
            new NpcDialogue("Merchant", 10013901, 1, NpcService.Supply),
            new NpcDialogue("Merchant", 10014001, 1, NpcService.Supply),
            new NpcDialogue("Merchant", 10014101, 1, NpcService.Supply),
            new NpcDialogue("Merchant", 10025001, 1, NpcService.Supply),
            new NpcDialogue("Merchant", 10028101, 2, NpcService.Supply),
            new NpcDialogue("Merchant", 40022101, 1, NpcService.Supply),
            new NpcDialogue("Food Merchant", 10006001, 1, NpcService.Supply),
            new NpcDialogue("Fruit Merchant", 10004301, 1, NpcService.Supply),
            new NpcDialogue("Fruit Merchant", 10008701, 1, NpcService.Supply),
            new NpcDialogue("Milk Merchant", 10008801, 1, NpcService.Supply),
            new NpcDialogue("Shrimp Merchant", 10008901, 1, NpcService.Supply),
            new NpcDialogue("Trade Merchant", 10014201, 1, NpcService.Supply),
            new NpcDialogue("Resting Merchant", 10026901, 1, NpcService.Supply),
            new NpcDialogue("Sighing Merchant", 10026501, 3, NpcService.Supply),
            new NpcDialogue("Foreign Merchant", 10017301, 3, NpcService.Supply),
            new NpcDialogue("Merchant for Hire", 10023901, 3, NpcService.Supply),
            new NpcDialogue("Shop Clerk", 10019301, 1, NpcService.Supply),
            new NpcDialogue("Collectibles Merchant", 10014701, 1, NpcService.Supply),
            new NpcDialogue("Collectibles Merchant", 10014801, 1, NpcService.Supply),
            new NpcDialogue("Gift Merchant", 10001801, 1, NpcService.Supply),
            new NpcDialogue("Jewelry Merchant", 10014601, 1, NpcService.Supply),
            new NpcDialogue("Jewelry Merchant", 10014901, 1, NpcService.Supply),
            new NpcDialogue("Café Server", 10020801, 1, NpcService.Supply),
            new NpcDialogue("Butcher", 10004201, 1, NpcService.Supply),
            new NpcDialogue("Flower Girl", 10001701, 1, NpcService.Supply),
            new NpcDialogue("Granny Broom", 10017801, 1, NpcService.Supply),
            new NpcDialogue("Buddy", 40006901, 1, NpcService.Supply),
            new NpcDialogue("Daisha", 10017201, 1, NpcService.Supply),
            new NpcDialogue("Seultae", 10012901, 3, NpcService.Supply),
            new NpcDialogue("Traveler from Afar", 10004101, 1, NpcService.Supply),

            // Two NPCs whose line is about a system the client and server have. Ariel talks about the Wardrobe (function 32);
            // the Novice Guildmaster and the Promoter who says the same recruit for a guild (function 13, GuildService).
            new NpcDialogue("Ariel", 10004901, 1, NpcService.Wardrobe),
            new NpcDialogue("Ariel", 20001201, 1, NpcService.Wardrobe),
            new NpcDialogue("Novice Guildmaster", 10004501, 2, NpcService.Guild),
            new NpcDialogue("Promoter", 10007501, 1, NpcService.Guild),
        };

        public static readonly NpcRedirect[] Redirects =
        {
            // The Item Merchant of the shared dialogue; 10370000708 is the line the other Item Merchants say.
            new NpcRedirect("Item Merchant", 100311, 10031101, 10002301, 10370000708, NpcService.Supply),
        };

        /// <summary>The client's close of the dialogue panel, run after the service panel opens (as DialogueConfig 40001402 does).</summary>
        private static string[] Close() => new[] { "8", "TOPBAR", "UI_Sys_NpcDialogPanel" };

        private static string[] Shop(string function, string type, string id, string cls) =>
            new[] { "25", function, "TOPBAR", "UI_Sys_ShopPanel", type, id, cls };

        /// <summary>The options a service adds, in order: the service itself, then "Leave".</summary>
        public static NpcOption[] Options(NpcService service)
        {
            NpcOption open;
            switch (service)
            {
                case NpcService.Supply: open = new NpcOption(OpenShopText, ShopIcon, new[] { Shop("70105", "1", "140", "1401"), Close() }); break;
                case NpcService.PetShop: open = new NpcOption(OpenShopText, ShopIcon, new[] { Shop("70113", "2", "210", "210"), Close() }); break;
                case NpcService.Headwear: open = new NpcOption(OpenShopText, ShopIcon, new[] { Shop("70505", "7", "502", "50204"), Close() }); break;
                case NpcService.Hairstyle: open = new NpcOption(OpenShopText, ShopIcon, new[] { Shop("70504", "7", "502", "50203"), Close() }); break;
                // OpenFunctionUI(function, layer, panel, page): the page is the first parameter of UI_Sys_StrengthenPanel (1 craft, 2 refine, 3 enchant),
                // the one the equipment tip's Refine button opens.
                case NpcService.Refine:
                    open = new NpcOption(RefineText, ChatIcon, new[] { new[] { "25", "8", "TOPBAR", "UI_Sys_StrengthenPanel", "2" }, Close() });
                    break;
                // The panels the main menu opens for these functions, which take no initial data (an empty one is read as none).
                case NpcService.Wardrobe:
                    open = new NpcOption(WardrobeText, ChatIcon, new[] { new[] { "25", "32", "TOPBAR", "UI_Sys_WardrobePanel" }, Close() });
                    break;
                case NpcService.Guild:
                    open = new NpcOption(GuildText, ChatIcon, new[] { new[] { "25", "13", "TOPBAR", "UI_Sys_GuildPanel" }, Close() });
                    break;
                default: throw new System.ArgumentOutOfRangeException(nameof(service));
            }
            return new[] { open, new NpcOption(LeaveText, LeaveIcon, new[] { Close() }) };
        }

        /// <summary>
        /// The fields of an option row, every one the client's own rows have. The client's rows get the ones they do not list
        /// from a shared defaults table (_iCondition 0, _iType 0, empty strings); a plain table has no such table, and the
        /// dialogue panel drops an option whose _iCondition is not 0 (UI_Sys_NpcDialogPanelCtrl.RefreshOptions asks
        /// LC_ConditionManager about nil, which is false), so none may be left out. Values: long, string, long[] (_iText) or
        /// string[][] (_iEXECAction).
        /// </summary>
        public static List<KeyValuePair<string, object>> OptionFields(long dialogue, long npc, long sort, NpcOption option) =>
            Fields(dialogue, npc, sort, 2L, new[] { option.Text }, option.Icon, option.Actions);

        /// <summary>The spoken line of a row made here (a normal row, the type DialogueConfig defaults to).</summary>
        public static List<KeyValuePair<string, object>> LineFields(long dialogue, long npc, long text) =>
            Fields(dialogue, npc, 1L, 1L, new[] { text }, "", new[] { new[] { "" } });

        private static List<KeyValuePair<string, object>> Fields(long dialogue, long npc, long sort, long textType, long[] text, string icon, string[][] actions) =>
            new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("_iID", dialogue),
                new KeyValuePair<string, object>("_iSortID", sort),
                new KeyValuePair<string, object>("_iType", 0L),
                new KeyValuePair<string, object>("_iNPCID", npc),
                new KeyValuePair<string, object>("_iNPCSpine", ""),
                new KeyValuePair<string, object>("_iTalkNPCID", npc),
                new KeyValuePair<string, object>("_kTalkNPCImage", ""),
                new KeyValuePair<string, object>("_iNPCAnimation", ""),
                new KeyValuePair<string, object>("_iTextType", textType),
                new KeyValuePair<string, object>("_iText", text),
                new KeyValuePair<string, object>("_kVoice", ""),
                new KeyValuePair<string, object>("_iIcon", icon),
                new KeyValuePair<string, object>("_iCondition", 0L),
                new KeyValuePair<string, object>("_iEXECAction", actions),
                new KeyValuePair<string, object>("_iBubbleType", ""),
            };
    }
}
