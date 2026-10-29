#pragma warning disable CS1998 // arms that only send or return keep the uniform async shape
using Google.Protobuf;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The login, role and session messages, the quest messages and the stubs of features that are not served. How
/// they are admitted: role creation under its account, the session's own continuations as runtime, the few that
/// change only the sender's role under that role.
/// </summary>
public sealed class CoreMessages(Microsoft.Extensions.Options.IOptions<ServerOptions> serverOptions, CreditShop creditShop, CurrencyExchange currencyExchange, RoleSessionService roleSessions, ChatService chat, BuffService buffs, GmCommands gm, SceneService scene, TaskService tasks, DailyActivityService daily, TeamService teams, GuildService guilds, DungeonService dungeons, Ownership.RoleHandover handover, Micro.RankService ranks) : Messaging.IMessageModule
{
    private const uint EquipGetRank = 31470;

    /// <summary>The web shop address for an in-game purchase button (ChargeType 1 = recharge, anything else a product), or null.</summary>
    private string? StoreLink(long chargeType)
    {
        var url = serverOptions.Value.StoreUrl?.Trim();
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        return url + (url.Contains('?') ? "&" : "?") + "cat=" + (chargeType == 1 ? "Cristais" : "Passes");
    }

    public void Map(Messaging.MessageRoutes routes)
    {
        // What the client sends before its role is in the world; anything else is refused until then.
        // NW_NetworkManager pings with HEARTBEAT from the moment it connects, before the account signs in.
        routes.AllowFrom(Messaging.SessionStage.Connected, id => id == MsgId.Heartbeat);
        // LC_LoginManager on the role screen: LOGIN_ROLE_CREATE, LOGIN_ROLE_SELECT, and UPLOAD_CLIENT_SDK_INFO in
        // OnChooseRoleLoginCallback, the answer to the selection.
        routes.AllowFrom(Messaging.SessionStage.RoleScreen,
            id => id is MsgId.LoginRoleCreate or MsgId.LoginRoleSelect or MsgId.UploadClientSdkInfo);
        // This fork's own client hooks (Ro3Emu.ClientHooks) use game messages 77000..77099, some repeated before a role
        // exists (the level-up hello); each handler checks what it needs, so they are never gated by stage.
        routes.AllowFrom(Messaging.SessionStage.Connected, id => id is >= 77000 and <= 77099);
        routes.AdmitRuntime(id => id is MsgId.SceneLoaded or MsgId.PreSwitchRole or MsgId.LoginRoleSelect or MsgId.Logout
            or MsgId.Heartbeat, "dispatcher/owned continuation");
        // Role creation writes only its new role (the unique name index decides between accounts).
        routes.AdmitAccount(id => id == MsgId.LoginRoleCreate);
        routes.AdmitRole(id => id is MsgId.Walk or MsgId.SaveSetting or MsgId.ClientEnterMainUi);
        // The SDK info report is not kept: its empty handler reserves nothing, also on the role screen.
        routes.AdmitRuntime(id => id == MsgId.UploadClientSdkInfo, "dispatcher/ignored report");
        routes.AdmitRole(id => id is FeedbackService.SubmitMsg); // reads the sender, appends to its own report file
        // Sets one flag on the sender's own session, nothing shared; the client repeats it, also before it has a role.
        routes.AdmitRuntime(id => id is MonsterService.LevelUpFxHello, "client/level-up effect hello");
        routes.Map(MonsterService.LevelUpFxHello, "monster-fx", (session, _, _) =>
        {
            session.LevelUpFx = true;
            return Task.CompletedTask;
        });

        routes.Map(GuildChallengeService.Notify, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(2001, "handler", async (session, msgId, body) =>
        {
            var order = CreateOrderReq.Parser.ParseFrom(body.Span);
            // Credits of the web shop pay for it when ServerOptions.CreditShop is on (CreditShop): a pending order, approved on the shop
            // page the PC client opens (transaction_url); CreditShopDelivery then gives the goods. Otherwise fall through to the shop link.
            var bought = await creditShop.RequestAsync(session, order.ChargeType, order.ChargeId); switch (bought.Outcome)
            {
                case CreditShop.Outcome.Pending:
                    await session.SendGameAsync(2001, new CreateOrderResp
                    {
                        Result = 0, ChargeId = order.ChargeId, ChargeType = order.ChargeType,
                        OrderId = "credits-" + bought.OrderId,
                        OrderData = System.Text.Json.JsonSerializer.Serialize(new { data = new { transaction_url = creditShop.ApprovalUrl(bought.OrderId) } })
                    });
                    await GmCommands.ReplyAsync(session, bought.Message);
                    return;
                case CreditShop.Outcome.Insufficient:
                    await session.SendGameAsync(2001, new CreateOrderResp
                    {
                        Result = ErrorCodes.CurrencyShort, ErrMsg = bought.Message,
                        ChargeId = order.ChargeId, ChargeType = order.ChargeType
                    });
                    return;
                case CreditShop.Outcome.Failed:
                    await session.SendGameAsync(2001, new CreateOrderResp
                    {
                        Result = ErrorCodes.FeatureUnavailable, ErrMsg = bought.Message,
                        ChargeId = order.ChargeId, ChargeType = order.ChargeType
                    });
                    return;
            }
            if (StoreLink(order.ChargeType) is not { } url)
            {
                await session.SendGameAsync(2001, new CreateOrderResp
                {
                    Result = ErrorCodes.FeatureUnavailable,
                    ChargeId = order.ChargeId, ChargeType = order.ChargeType
                });
                return;
            }
            // The PC client reads orderData as JSON and opens data.transaction_url in the browser; no payment is started.
            await session.SendGameAsync(2001, new CreateOrderResp
            {
                Result = 0, ChargeId = order.ChargeId, ChargeType = order.ChargeType,
                OrderId = "shop-" + Guid.NewGuid().ToString("N"),
                OrderData = System.Text.Json.JsonSerializer.Serialize(new { data = new { transaction_url = url } })
            });
        });
        routes.Map(2002, "handler", async (session, msgId, body) =>
        {
            await session.SendGameAsync(2002, new PayResultResp
            {
                Result = ErrorCodes.FeatureUnavailable,
                OrderId = PayResultReq.Parser.ParseFrom(body.Span).OrderId
            });
        });
        routes.Map(2003, "handler", async (session, msgId, body) =>
        {
            await session.SendGameAsync(2003, new GetRechargeFirstAwardResp { Result = ErrorCodes.FeatureUnavailable });
        });
        routes.Map(2004, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(2005, "handler", async (session, msgId, body) =>
        {
            var exchange = ItemExchangeReq.Parser.ParseFrom(body.Span);
            await session.SendGameAsync(2005, new ItemExchangeResp
            { Result = await currencyExchange.ExchangeAsync(session, exchange.ItemId, exchange.Count) });
        });
        routes.Map(WorldCoreService.TeleportObject, "handler", async (session, msgId, body) =>
        {
            // In the reverse dungeon (SceneService.MovesOnSurface) the client reports where it shows an NPC before every
            // interaction it sends (LC_DAL_Interact.SyncNpcPos: only for units in m_bReverseMode, then 32700), and waits
            // for no answer: accepted without a reply, as an error tip would show on every press. Elsewhere it is not sent.
            if (scene.MovesOnSurface(session.SceneEntry)) return;
            await WorldCoreService.ErrorAsync(session, msgId, ErrorCodes.Parameter);
        });
        routes.Map(31204, "tasks", async (session, msgId, body) =>
        {
            await tasks.ResetAsync(session, TaskResetReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31212, "tasks", async (session, msgId, body) =>
        {
            await tasks.NpcChatAsync(session, TaskNpcChatReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31213, "tasks", async (session, msgId, body) =>
        {
            await tasks.FinishPydAsync(session, FinishPYDTaskReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31215, "tasks", async (session, msgId, body) =>
        {
            await tasks.SelectBranchAsync(session, SelectTaskQuestBranchReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(31217, "tasks", async (session, msgId, body) =>
        {
            await tasks.LuckyTurnAsync(session, LuckyTurnReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(30016, "tasks", async (session, msgId, body) =>
        {
            await tasks.TrackTeleportAsync(session, TaskTrackTeleportReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(LifeJobService.CheckCollect, "handler", async (session, msgId, body) =>
        {
            // msgtable supplies romsg.null, not a collection-id request; no mutation or cast is inferred.
            await session.SendGameAsync(LifeJobService.CheckCollect, new CheckCanCollectNtf { Result = ErrorCodes.Parameter });
        });
        routes.Map(MsgId.LoginRoleCreate, "handler", async (session, msgId, body) =>
        {
            await roleSessions.CreateRoleAsync(session, AccountLoginCreateRoleReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MsgId.LoginRoleSelect, "handler", async (session, msgId, body) =>
        {
            await roleSessions.SelectRoleAsync(session, AccountLoginSelectRoleReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(DungeonService.Result, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(DungeonService.ResultNotify, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(DungeonService.Event, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(DungeonService.AwardNotify, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(TowerService.Sweep, "handler", async (session, msgId, body) =>
        {
            await session.SendGameAsync(TowerService.Sweep, new ClimTowerSweepRes { Result = ErrorCodes.FeatureUnavailable });
        });
        routes.Map(TowerService.End, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(WildAfkService.SaveCharge, "handler", async (session, msgId, body) =>
        {
            if (session.WildAfk is { } state) state.PowerSaving = WildAfkSaveChargeReq.Parser.ParseFrom(body.Span).IsSaveCharge;
        });
        routes.Map(WildAfkService.OfflineReward, "handler", async (session, msgId, body) =>
        {
            await session.SendGameAsync(WildAfkService.OfflineReward, new ReceiveOfflineRewardRsp { Result = ErrorCodes.AfkNoSummary });
        });
        routes.Map(WildAfkService.DungeonStart, "handler", async (session, msgId, body) =>
        {
            await session.SendGameAsync(WildAfkService.DungeonStart, new AfkDungeonStartRsp { Result = ErrorCodes.FeatureUnavailable });
        });
        routes.Map(EquipGetRank, "handler", async (session, msgId, body) =>
        {
            // Equipment ranking in tooltips (UI_Com_ItemTips_Equip_HeadWidgetCtrl._requestRankData, board 1401): both ids
            // echoed (an empty reply caches item 0), 0 when the instance is not on the board.
            var rank = QueryEquipRankReq.Parser.ParseFrom(body.Span);
            await session.SendGameAsync(EquipGetRank, new QueryEquipRankRes
            {
                Rankid = rank.Rankid, Itemuid = rank.Itemuid,
                Rank = await ranks.EquipRankAsync(rank.Rankid, rank.Itemuid)
            });
        });
        routes.Map(AuctionService.Sync, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(AuctionService.Changed, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(AuctionService.DungeonDone, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(AuctionService.DungeonChanged, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(AuctionService.HasAuto, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(AuctionService.Reddot, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(MsgId.Walk, "handler", async (session, msgId, body) =>
        {
            await roleSessions.WalkAsync(session, WalkReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(MsgId.SaveSetting, "handler", async (session, msgId, body) =>
        {
            await roleSessions.SaveSettingAsync(session, SaveSettingReq.Parser.ParseFrom(body.Span), body);
        });
        routes.Map(MsgId.Logout, "handover", async (session, msgId, body) =>
        {
            await handover.Begin(session);
            await session.SendGameAsync(MsgId.Logout, new LogoutResp());
        });
        routes.Map(MsgId.PreSwitchRole, "handler", async (session, msgId, body) =>
        {
            // The client then calls the avatar RPC SwitchToAccount (see RoleSessionService.SwitchToAccountAsync).
            await session.SendGameAsync(MsgId.PreSwitchRole, new PreSwitchRoleResp { Result = 0 });
        });
        routes.Map(MsgId.ClientEnterMainUi, "handler", async (session, msgId, body) =>
        {
            // newLv 0: no queued level-up popup.
            await session.SendGameAsync(MsgId.ClientEnterMainUi, new ClientEnterRes { NewLv = 0 });
        });
        routes.Map(MsgId.UploadClientSdkInfo, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(BuffService.ClientRemoveBuff, "buffs", async (session, msgId, body) =>
        {
            await buffs.RemoveByRequestAsync(session, ClientRemoveBuffReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(BuffService.ImmuneDeathConfirm, "buffs", async (session, msgId, body) =>
        {
            await buffs.ConfirmImmuneDeathAsync(session, PlayerConfirmImmuneDeadReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SceneService.ClientNpcInteract, "tasks", async (session, msgId, body) =>
        {
            await tasks.OnDialogAsync(session, ClientNpcInteractReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(SceneService.ClientEnterArea, "tasks", async (session, msgId, body) =>
        {
            var area = ClientEnterAreaReq.Parser.ParseFrom(body.Span);
            await tasks.OnAreaAsync(session, area);
            // A region whose action is server-hosted (613 EnterDungeon_Sever) is the server's to run.
            if (scene.ServerHostedRegion(session, area.AreaId) is { } region) await dungeons.EnterFromRegionAsync(session, region);
        });
        routes.Map(TaskService.TriggerTaskEvent, "tasks", async (session, msgId, body) =>
        {
            await tasks.OnClientInteractAsync(session, TriggerTaskEventReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TaskService.PlotActingEnd, "tasks", async (session, msgId, body) =>
        {
            var plotEnd = PlotActingEndReq.Parser.ParseFrom(body.Span);
            await tasks.OnPlotEndAsync(session, plotEnd);
            await dungeons.PlotEndedAsync(session, plotEnd.PlotId); // a scripted scene's story
        });
        // A story's end runs both client actions of its plot: 601 VideoPerformEndClient (84011 above) and 150
        // PlotActingTrigger (LC_TaskHelper.PlotActingTriggerReq, 84012). Both are fire-and-forget: no client code
        // waits on their NullArg response type, so neither is answered.
        routes.Map(DungeonService.PlotSceneEvent, "tasks", async (session, msgId, body) =>
            await dungeons.PlotEndedAsync(session, PlotActingTriggerReq.Parser.ParseFrom(body.Span).PlotId));
        routes.Map(SceneService.DialogueActionTrigger, "handler", async (session, msgId, body) =>
        {
        });
        routes.Map(TaskService.ExitCutSceneState, "tasks", async (session, msgId, body) =>
        {
            await tasks.ExitCutSceneAsync(session);
        });
        routes.Map(TaskService.TaskTrack, "tasks", async (session, msgId, body) =>
        {
            await tasks.TrackAsync(session, TaskTrackReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TaskService.GoodsStack, "tasks", async (session, msgId, body) =>
        {
            await tasks.GoodsStackAsync(session);
        });
        routes.Map(TaskService.TaskFollow, "tasks", async (session, msgId, body) =>
        {
            await tasks.FollowAsync(session, TaskFollowReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TaskService.TaskAccept, "tasks", async (session, msgId, body) =>
        {
            await tasks.AcceptAsync(session, TaskAcceptReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TaskService.TaskDelivery, "tasks", async (session, msgId, body) =>
        {
            await tasks.DeliverAsync(session, TaskDeliveryReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(TaskService.TaskGiveUp, "tasks", async (session, msgId, body) =>
        {
            await tasks.GiveUpAsync(session, TaskGiveUpReq.Parser.ParseFrom(body.Span));
        });
        routes.Map(GmCommands.GeneralProcGm, "gm", async (session, msgId, body) =>
        {
            await gm.PanelAsync(session, GeneralGMProcReq.Parser.ParseFrom(body.Span));
        });
        // The menu's Feedback button (Ro3Emu.ClientHooks.FeedbackButton): the report text as UTF-8.
        routes.Map(FeedbackService.SubmitMsg, "feedback", async (session, msgId, body) =>
        {
            if (session.Role == null) return;
            var text = System.Text.Encoding.UTF8.GetString(body.Span[..Math.Min(body.Length, 16 * 1024)]);
            await chat.Feedback.SubmitAsync(session, text, "button");
        });
        routes.Map(MsgId.SceneLoaded, "scene", async (session, msgId, body) =>
        {
            var loadedRole = session.Role;
            var loadedAttachment = session.Attachment;
            long sceneGeneration = session.SceneGeneration;
            session.WorldState.NameBoardLimit = Math.Min(SceneLoadedReq.Parser.ParseFrom(body.Span).NameBoardMaxCount, 1000);
            await scene.SceneLoadedAsync(session, SceneLoadedReq.Parser.ParseFrom(body.Span));
            using var scope = session.Owners is { } loadedOwners ? await roleSessions.EnterScenePublicationAsync(loadedOwners, session) : null;
            if (!ReferenceEquals(session.Role, loadedRole) || !ReferenceEquals(session.Attachment, loadedAttachment)
                || session.SceneGeneration != sceneGeneration) return;
            await teams.OnEnteredAsync(session);
            session.GuildOnlinePublished = true; // undo even if the publication throws after saving
            await guilds.OnlineChangedAsync(session, true);
            await guilds.SceneGuildIdAsync(session);
        });
        routes.Map(MsgId.Heartbeat, "daily", async (session, msgId, body) =>
        {
            // C/D 16: every player's heartbeat used to be one Realm scope (S3: 10k per 5 min, the top Realm waiter,
            // other replies queued behind it up to 2.5 s). The role's own reconciliation runs under its reservation;
            // realm guild maintenance (at most once a second) admits its own guild keys.
            async Task OwnAsync() { await daily.ReconcileAsync(session); await dungeons.ReconcileAsync(session); }
            if (session.Owners is { } owners && session.Role is { } role)
                await owners.RunSessionAsync("heartbeat",
                    [Ownership.ReservationKey.Role(role.Id), Ownership.ReservationKey.Lifecycle(role.Id)], session, OwnAsync);
            else await OwnAsync();
            await guilds.ReconcileOnHeartbeatAsync();
            await session.SendGameAsync(MsgId.Heartbeat, new Pong { ServerTimeMs = roleSessions.NowMs(), StrDebugTag = HeartbeatTag(body.Span) });
        });
    }

    /// <summary>
    /// The client's ping readout (RO3.ClientTiming.HeartbeatCore) puts a tag in Ping.strDebugTag and times the Pong that
    /// carries the same tag back in strDebugTag; a Pong without it is never matched and the HUD stays at 0 ms. An empty
    /// or malformed Ping (the bots send an empty one) has no tag to return.
    /// </summary>
    internal static string HeartbeatTag(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty) return "";
        try { return Ping.Parser.ParseFrom(body).StrDebugTag; }
        catch (InvalidProtocolBufferException) { return ""; }
    }
}
