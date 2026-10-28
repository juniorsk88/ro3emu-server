# Fixes and improvements log

One entry per sprite/visual improvement, NPC change and bug fix: what the player saw, the cause, how it was fixed, where, and
how it is checked. Newest first. Add the entry in the same task as the change.

## 2026-10-25: story task stuck in its cutscene ("Strange Occurences", the pendant falls)

**Symptom:** on the main task "Strange Occurences" (10105050, plot 130061) the client hangs when the pendant falls to the ground. The server finishes a cutscene task only when the client reports the cutscene's end (84011), which never comes; the player closes the game and the cutscene starts again at the next login (arcus_e_flecha, 25 Oct 19:53-20:00 UTC: three relogins at the same spot, task never delivered).

**Fix:** `Ro3Emu:PlotStallSeconds` (default 20, 0 = off, `Ro3Emu__PlotStallSeconds=0`): `TaskService.SkipStalledPlots`, at login, counts the plot target (309002) of a running task as played when the task has been running for at least that long, and `SettleAsync` delivers it and accepts the next task. 26 tasks have a plot target. The client hang itself is not fixed (the cutscene lives in the client's assets); the player still has to close and reopen the game once. Tests `PlotStallTests` (stays at once, goes on after 60 s, switch off). Status: DEPLOYED 25 Oct 20:45 UTC (rollback `server/Ro3Emu.Server.dll.prev-20261025-204501-plot-range`).

## 2026-10-25: party shares kill EXP and kill-quest credit

**Symptom:** only the player who struck the last blow got the EXP of a kill, and a kill quest ("kill 10 X") counted only for the killer (or for players who hit the monster, for "involved" targets). Party members next to each other did not progress together.

**Fix:** `Ro3Emu:PartyKillShare` (default on, `Ro3Emu__PartyKillShare=false` restores the original rule). `TeamService.KillSharers` = other party members who are online, alive, on the killer's map, within `PartyKillShareRange` (0 = whole map, the default; the owner asked for the whole map) and no more than `PartyKillShareLevelGap` levels (20, 0 = no limit) from the killer. `RewardService.ShareExpAsync` gives each the kill's raw EXP corrected for their own level against the monster, times the existing party bonus (TeamExpBuffNum: x1.25 with 2 ... x2 with 5), Job EXP equal to Base EXP; items stay with the killer. `TaskObjectives` counts the kill (104002 and 310001 targets) for the sharers' running quests, in the same durable transaction as the killer's. Not original behaviour. Test `PartyKillShareTests` (no party, party, other monster, switch off, range, level gap). Related suites: 441 passed, 1 failed (`TowerTests.ATeamBelowTheRecommendedPowerMeetsASuppressedFloor`: solo power suppression, no kills or party involved; not checked whether it already failed before this change). Status: DEPLOYED 25 Oct 19:52 UTC (rollback `server/Ro3Emu.Server.dll.prev-20261025-195219-party-share`); 20:45 UTC: range changed to the whole map and a log line per shared kill ("X shares Y's kill of N: exp E"). Not yet confirmed in game.

## 2026-10-25: cleared story dungeons left the player inside with no hint

**Symptom:** after a story dungeon (Culvert Louie's game, Baphomet's maze, ...) is cleared the quest advances, but the player stays in the closed room, the tracker keeps the dungeon's text ("Obter aparelho", "Vença Baphomet (1/1)") and nothing says to press the exit button; the next quest step is outside. The client shows no result window for these Plane scenes.

**Fix:** `Ro3Emu:StoryDungeonExitSeconds` (default 8, 0 = off, `Ro3Emu__StoryDungeonExitSeconds=0`): when a reconstructed or scripted story dungeon (no team boss) is won, `DungeonService.ReturnToFieldAsync` tells the players (system text) and leaves them out after the delay as the exit button would (`LeaveAsync`). Not original behaviour. DungeonTests + StoryWalk: 106 passed. Status: DEPLOYED 25 Oct 12:56 UTC (rollback `server/Ro3Emu.Server.dll.prev-20261025-125629-story-exit`). Not yet confirmed in game.

## 2026-10-25: Culvert "Retrieve the Professor's instrument" had no guide

**Symptom:** quest 10106270 (The Mysterious Thief Gang), last step of the Culvert dungeon 3120402 ("Obter aparelho / Recup. equip. Prof."): after Louie's game nothing told the player where to go; the step only completes within 4 m of scene point 37 (32, 42.5).

**Cause:** the client draws a floor line for dungeon var 10021 `PGuideWayList` (LC_SceneCom_GuideGfx, guide point ids of the scene editor's `m_kGuidePoints`); the server never sent it. Guide point 38 runs from (49.3, 59.3) to (32.2, 42.9), right at point 37.

**Fix:** `ScriptedStep.Guide` (json `"guide": [..]`), sent as a DungeonEventNotify var change at the step's start (`DungeonService.GuideWayVar`), emptied when a later step has none; scene 3120402 step 4 lists guide point 38. Test: `LouiesGamePlaysItsStoriesAndOnlyKatiesBellWins`. Of the six scripted scenes only this one has a guide point. Status: DEPLOYED 25 Oct 12:22 UTC (rollback `server/Ro3Emu.Server.dll.prev-20261025-122121-guide-line`). Not yet confirmed in game.

## 2026-10-25: head/face card slots were "locked" (server, optional, on by default)

- **Seen:** "Slot travado" when inserting head accessory cards (`_iCardPart` 7, e.g. 1420700/1430702); the server log showed
  `inserts card ... into 8/1: 1811` (SlotSealed) on every try.
- **Cause:** `EquipGridConfig` gives the head (8) and face (9) grids no `_kSocketOpenN` unlock condition at all (only grids 1-7 and
  the alternates 1-3 have one), so `CardService.SocketError` always answered SlotSealed; face cards (part 8) had no grid and mouth
  cards (part 9) have none in the data.
- **Fix:** option `Ro3Emu:OpenCardSlots` (`ServerOptions.OpenCardSlots`, **on by default** at the owner's request; environment variable
  `Ro3Emu__OpenCardSlots=false` restores the original rule): every valid socket is open (the worn-weapon rule of grids 1 and 2 stays)
  and face cards go to grid 9. Mouth cards (part 9) still have no grid. Not original behaviour.
- **Checked by:** `CardProgressTests.HeadCardsFindAnOpenSlotUnlessOpenCardSlotsIsOff` (off: 1811; on: the head card goes into 8/1) and the
  77 card/totem/plan tests (the two remaining failures: this one before its fix, and `CardCrashRecoveryTests`, which cannot find the SDK in this shell).
- **Status:** deployed 2026-10-25 02:54 UTC (rollback: `server/Ro3Emu.Server.dll.prev-20261025-025430-card-slots`). Not yet tried in game.

## 2026-10-25: Challenge button of ten-player dungeons refused a role alone (client hook, optional, launcher 1.3.6)

- **Seen:** in the Realm of the Gods panel (Isti-Normal) a role without a team got "Please join a party/raid before entering the
  Dungeon" (UIWords 56192) on Challenge, and "Not enough members" (56153) in a team of one; "Find Party" there only lists other
  players' parties (it is not the automatic match of the five-player Otherworld Mirage).
- **Cause:** `UI_Sys_Dungeon_RightInfoWidgetCtrl._readyToEnterDungeon` checks team, leader and member count in the client and only then
  sends the entry (`LC_DAL_MultiPlayerDungeon.SendEnterDungeon`, message 30400); the server never hears of it.
- **Fix:** client hook `SoloDungeonEntry` (ClientHooks, installed from `LuaPatches.Tick`) wraps `_readyToEnterDungeon`: with no team or a
  team of one it sends that same 30400 itself; a team of two or more keeps the original checks. The server stays the authority: without
  `Ro3Emu:DungeonSolo` (or the GM aid) it answers DungeonTeamSize, as the original game. Server side nothing changed.
- **Checked by:** `GroupContentTests.ALoneRoleEntersTheRealmOfTheGodsWithTheServerOptionAndMeetsABossWeakenedByDungeonSoloJson` (off:
  DungeonTeamSize; on: enters, Start Battle, boss with 50% hp/attack, defence unchanged). The Lua wrapper itself is **not tested in a
  real client**; the hook DLL builds against the original managed DLLs.
- **Status:** PUBLISHED 2026-10-25 as launcher 1.3.6 (only `client/Ro3Emu.ClientHooks.dll` differs from 1.3.5; the owner said "pode publicar"; backup of 1.3.5 in `/opt/ro3-bridge/dl-prev-1.3.5`).
  Previous DLL kept as `work/pack/RO3-Online-hooks-1.3.5.dll`. After publishing, look in `Player.log` for
  `[Ro3Emu.SoloDungeon] ... wrapped` and `alone: entry requested`.

## 2026-10-24: currency exchange window ("Troca de Moeda") did nothing (server)

- **Seen:** in the exchange window (Zeny to Minted Coin, rate 1:100, cost 2 receive 200) the "Trocar?" button gave no result and
  the balances did not change.
- **Cause:** `ItemExchangeReq` 2005 was a placeholder that answered `FeatureUnavailable` to every request.
- **Fix:** `CurrencyExchange` (new) reads the `ItemExchangeConfig` row keyed by the paid item (`_iTargetID`, `_iRate`,
  `_iSingleLimit`, `_iFunction`), refuses a bad id, a count outside 1..limit, or a closed function (7006 base level 10, 7005 level 20),
  and pays and grants in one transaction (`CharacterService.ExchangeAsync`); a short balance answers 192 and changes nothing. Message
  admission is unchanged (the dispatcher snapshot test passes). Not original behaviour in one point: none known; the rate and limits
  come from the exported rows.
- **Checked by:** `CurrencyExchangeTests` (2 paid, 200 received; short balance, zero or negative count, over the limit and unknown id
  change nothing) and the dispatcher admission tests.
- **Status:** deployed 2026-10-24 23:27 UTC; the owner confirmed in game that the exchange changes the balances.

## 2026-10-24: solo entry to team bosses as a server switch (server, optional)

- **Seen:** the team-boss screen (Otherworld Mirage) stayed on "Pareando..." forever for a player alone on the server; the only
  way in was typing the GM test aid `!dungeonsolo on` after every login.
- **Cause:** the matching queue only forms a match with enough players, and the solo allowance lived only in a per-session GM flag.
- **Fix:** option `Ro3Emu:DungeonSolo` (default off, `ServerOptions.DungeonSolo`); `DungeonService.SoloAllowed` is the single rule
  (switch or session flag) used by the 30400 entry, the 82681 matching and the matched start. Not original behaviour; the boss keeps
  its team-sized stats (a per-player scaling was left out until the owner has tried one).
- **Checked by:** `DungeonMatchTests.ServerOptionLetsALoneRoleMatchIntoATeamBossWithoutTheGmTestAid` (off: waits in the queue; on:
  the match forms at once with no GM aid) and the 128 dungeon tests.
- **Status:** deployed 2026-10-24 23:27 UTC with `Ro3Emu__DungeonSolo=true` in the drop-in `zz-dungeon-solo.conf` (remove it and restart to
  turn off). The owner found the team boss "too strong" alone, so the same day `DungeonSoloScale` was added: `<data>/dungeon-solo.json`
  (`hpPercent`, `attackPercent`, 5 to 100, hot reload) weakens a team boss met by fewer players than its minimum; started at 50/50
  ("suave") to be tuned by the owner's tests. Checked by `DungeonSoloScaleTests` and the boss-stats integration test; 156 dungeon tests.

## 2026-10-24: "Claim" of the Area Prize (Prontera) paid nothing (server)

- **Seen:** in the Monster Hunt window, "Prontera Prize" panel (12/186, first stage at 10), the player pressed Claim and
  got nothing; no error in the log.
- **Cause:** two different counters. The panel enables Claim when the monster stages *reached* by kills hold the stage's
  threshold (`LC_MonsterCrusadeHelper.GetAreaAchievedAwardCount`, shown as "12/186"; 186 is the total of stages of the area's
  monsters). The server asked for stages already *claimed* (`ClaimedInArea`, only 6 for this player, the rule of the red dot),
  found none eligible, answered an empty reply and logged nothing.
- **Fix:** `MonsterHuntService.AchievedInArea` counts the area's reached monster stages (capped at `_iAreaLimit`) and
  `ClaimAreaAsync` uses it. The stored area score keeps meaning "claimed monster stages", as before.
- **Checked by:** `MonsterHuntTests.AnAreaStagePaysWhenEnoughMonsterStagesAreReachedEvenIfUnclaimed` (12 reached, none claimed:
  stage 1 pays, stage 2 needing 35 is refused) and the existing `MonsterAndAreaStagesPayOnceWhenReached`.
- **Status:** deployed 2026-10-24 13:51 UTC with the batch below.

## 2026-10-24: deploy of the batch (ported fixes, security gates, Area Prize claim)

Live at 13:51 UTC after the owner's OK: DLL sha `0952b6ac...`, rollback `server/Ro3Emu.Server.dll.prev-20261024-135059-upstream-ports`
(stop `ro3emu`, copy it over `Ro3Emu.Server.dll`, start), database copy taken just before
(`/home/deploy/ro3emu/data-backup-20261024-135038-antes-portar-upstream.db`, 99 MB; the batch has no schema change). Suite before
the deploy: 1826 passed, 5 tests rewritten for the stage gate (now they check that no answer arrives), the quest walk passed alone
(it timed out only under full-suite load). After the restart the log shows no `refused at stage` and no `no account is bound`; the
only refusals are the clients that were online trying to reconnect to the restarted server. Needs a real login to confirm.

## 2026-10-24: Sanctuary and Gravitation Field hit once; PR for the original repository prepared (not opened, not in this code yet)

- **Seen (by data, not yet in game):** Sanctuary (14115) healed once instead of every 0.6 s for 4 s and healed allies up to 7 m
  away (a circle) instead of the 7 m x 7 m square; Gravitation Field (12212) struck once instead of 8 times in 4 s. Same cause as
  Magnus Exorcismus: their `_kScriptLogic` had no case in `CombatService.CreateSkillObjectAsync`.
- **Fix, written against the original repository's code** (it renamed every private field): branch
  `fix/mage-priest-floor-skills` in the fork `juniorsk88/ro3emu-server`, 4 commits (Magnus, Sanctuary, Gravitation Field, docs). The
  two new skills have no NPC, so the fix adds a timed area that is not an AOI entity (`SkillObject.HasEntity`). Tests:
  `SkillCombatTests.GroundSkills.cs` (5, each failing before and passing after).
- **Not in `ro3emu-src` and not deployed.** The PR text is in [pr-draft-mage-priest-skills.md](pr-draft-mage-priest-skills.md);
  it is opened on `aethai/ro3emu-server` only after the user says yes. Taking the fix into our own tree waits for the 227-commit
  catch-up with the original.

## 2026-10-24: ten fixes ported by hand from the original repository (server)

Source: `aethai/ro3emu-server` (the repository this fork comes from), compared in [upstream-comparison.md](upstream-comparison.md).
The original renamed every private field in its PR #31, so a `cherry-pick` conflicts even where nothing else changed; each fix
below was read from its PR (`gh pr diff N -R aethai/ro3emu-server`) and re-written against our names. Not deployed yet unless
a line says so.

| PR | What the player saw | Cause and fix | Where |
|---|---|---|---|
| #80 | Power lost if the client dropped right after the power notice | The notice (31502) went out before the snapshot was saved. Now it is saved first; if the role or attachment changed meanwhile, nothing is published | `PowerService.PublishAsync` |
| #81 | Chat lines shown twice after joining a channel again | A repeated join replayed the history. It still answers the join, but replays only for a new subscription | `ChatService.JoinAsync/Join` |
| #85 | Errors when a role handover happened during a revive | `PublishReviveAsync` kept going after `Role` became null. Each step now checks the role first (also dungeon, guild league and item revive callers) | `CombatService`, `DungeonService`, `GuildLeagueService.Match`, `WorldCoreService.Revive` |
| #82 | Slow server stop while HTTP connections were open | Kestrel waits for every HTTP connection (up to 30 s) before the game gates stop. A tracker aborts what is left 2 s after the stop begins | `ServerHost` (`HttpConnectionReaper`) |
| #87 | Level 2 ore stayed locked at Mining 3 | The client's Unlock button only walks to the map and never sends the study request. The server now unlocks every collection whose profession-level condition holds, at login, when a profession is learned again and when it levels up | `LifeJobService.Collections.cs` |
| #86 | Kafra warp to Southern Payon landed inside a rock | The arrival point of the scene data can sit in scenery. `MapWorld.Standable` moves it to the nearest walkable cell within 8 m | `Maps/WorldData.cs` |
| #91 | Potion quick-use buttons (Add, Replace, Add All) did nothing | The client builds its HP/SP configs only from the 5009 field and indexes `nil` without it. Every role now gets both configs with 3 slots (condition 60, inferred), and slots of 0 (empty) are accepted | `CharacterService.RoleOperations/Sync` |
| #78 | Gear unequipped for the first time did not show in the bag; shortcut bar slots did not update | 5009 is resent whole when it carries a pack the client never got; the shortcut bar is always sent with its 6 slots (the prefab has `PropsItem01..06`) and moving a GUID clears its old slot | `CharacterService.Sync/RoleOperations/EconomicNotices` |
| #88 | Dodo's Request Wheel froze (hard lock) | The wheel waits for a reward notice of type 18 with the slot numbers; the old draw paid Zeny the wheel never shows. The 8 slots are `_kPreview` of the award of `PublicParamConfig.RunRingTaskTurnReward`; each turn rolls that award and the notice names the slot it stopped on | `TaskService.RoleOperations`, `CharacterService.Awards` |
| #89 | Egg Thief portal waves stayed out of the screen | The first-wave Pupa cannot move and the portals lie outside the 20 m home leash. In that dungeon only (1021301) the wave monsters get 550 cm/s and a 100 m leash (inferred) | `DungeonService.SpawnWaveAsync` |
| #93 | Clicking a far enemy attacked only once | The client sends the attack and its arrival stop a few ms apart; the stop moved the player off the loop origin and ended the auto-attack. A stop within 300 ms of an attack started while walking, and within the distance of the walk, re-anchors the loop (the same rule the cast already had) | `CombatService.PlayerMoved` |
| #44 + `0183849` | Profession talent tree 126 did not open; the profession choice was not saved | New handler for the choice nodes (Profession_L/M): it resolves the path, replaces only that choice inside the plan and saves it; the "total Talent Lv" that unlocks trees now counts what the panel shows (everything but the SkillPoint nodes, the oath at its shared level) | `SeasonTalentService.Profession.cs`, `SeasonTalentService.cs` |

## 2026-10-24: security hardening ported from the original repository (server)

Backup taken first: `D:\RO3Emu\backups\ro3emu-src-20261024-antes-portar-upstream.tar.gz` (sources, tests and docs without
`bin`/`obj`/`data`, 1438 files). Not deployed yet.

| PR | What it protects | Change | Where |
|---|---|---|---|
| #36 | A game connection that never signed in, or never chose a role, could run world handlers and take the realm's reservation for each message | The dispatcher checks the connection stage before admission: not signed in accepts only `HEARTBEAT`; the role screen also accepts `LOGIN_ROLE_CREATE/SELECT` and `UPLOAD_CLIENT_SDK_INFO`; the world accepts everything. A refused message is dropped unanswered; the first one per connection is logged as a warning. **This fork also lets game messages 77000..77099 through at every stage** (its client hooks use them, the level-up hello is sent before a role) | `Messaging/SessionStage.cs`, `MessageRoutes.AllowFrom/Accepts`, `GameMessageHandler`, `CoreMessages` |
| #37 | A micro-gate connection could call any service before an account was bound | `MicroServiceRouter.InvokeAsync` answers "not verified" before reading the arguments, except `LoginService.verifySdk` and `PhonestGate.bind` | `Micro/MicroServiceRouter.cs`, `MicroSession.RefusalLogged` |
| #38 | `/ro3emu/status`, `/metrics` and `/online` read live session state from request threads (a map's scene and line could be read half changed) | A loop ticker (`SessionCensus`) takes a snapshot once a second; status and metrics read it, `/online` builds its list on the loop | `Diagnostics/SessionCensus.cs`, `ServerMetrics`, `ServerHost` |

Both gates answer to one switch, `Ro3Emu__RefuseEarlyMessages` (default `true`); setting it to `false` restores the old behavior
without a new build, in case a client is refused at login. After a deploy: log in with a real client and check that the log has
no line `refused at stage` or `refused: no account is bound` for a normal login.

## 2026-10-24: Magnus Exorcismus left no cross on the floor (server)

- **Seen:** the Exorciser's "Cruz Exorcista" (skill Magnus Exorcismus, 14109) was cast and nothing stayed on the ground.
- **Cause:** the skill's `_kScriptLogic` is `Magnus_Exorcismus`, which `CombatService.CreateSkillObjectAsync` did not know. Only
  Fire Wall, Quagmire, Fire Pillar, Meteor Storm, Lord of Vermilion, Storm Gust, Safety Wall, Pneuma and the Hunter traps
  spawned their ground NPC; for any other script `npc` stayed 0, no NPC 22000009 (the cross, `_iNPCType` 4, unit 40010) was
  created and the cast only dealt one instant hit.
- **Fix:** a `Magnus_Exorcismus` case in `CreateSkillObjectAsync` (`src/Ro3Emu.Server/Game/CombatService.Effects.cs`). The data
  gives everything: `_kLogicData` `[22000009, 4, 8, 10]` is the NPC and the seconds it lasts (the description says "for the next
  4 sec."), `_kDescData[4]` (`0.5`) is the seconds between hits ("every 0.5 sec."), `_iTargetMax` 4 caps the targets per hit.
  The cross is owned by the caster, despawns when the time is up, when the caster dies, changes map or logs out (the same
  teardown as Fire Wall). The Undead/Demon/Shadow bonus of the description is not part of this change.
- **Checked by:** `SkillCombatTests.MagnusExorcismusLeavesItsCrossOnTheFloorForFourSeconds` (the NPC 22000009 appears for another
  player at the cast point, interval 500 ms, lasts about 4 s, then leaves the view) and
  `MagnusExorcismusHitsEveryHalfSecondForFourSeconds` (one cast lands 7 to 9 hits on a monster standing in it). The number of
  hits does not depend on the skill level: `_kDescData[3]` (4 s) and `[4]` (0.5 s) are the same on all 10 levels, only the damage
  per hit grows. Needs the server deploy; the client needs nothing.
- **Deployed:** 2026-10-24 12:10 UTC (after the player's OK) to `/home/deploy/ro3emu/server`: DLL sha `a2cde6aa...`; rollback is
  `Ro3Emu.Server.dll.prev-20261024-121039-magnus` (stop `ro3emu`, copy it over `Ro3Emu.Server.dll`, start). The service started
  with no errors in its log.
- **Not yet seen in game:** the cross sprite itself comes from the client's own unit 40010.

## 2026-10-23: NPCs with no service options (client hook, releases 1.3.3 to 1.3.5)

- **Seen:** merchants, smiths and other service NPCs only said one line; no Open Shop, Refine, Wardrobe or Guild option.
- **Cause:** in the original data those NPCs have a one-row dialogue and no link to a shop. 1.3.3 added the options but they never
  showed: the injected rows lacked the fields the client's rows inherit from a defaults table (`_iCondition` 0 ...), and
  `UI_Sys_NpcDialogPanelCtrl.RefreshOptions` drops an option whose `_iCondition ~= 0` (log: `ProcessCondition Invalid
  iConditionId null`).
- **Fix:** `NpcShops`/`NpcServiceTable` in `src/Ro3Emu.ClientHooks` write all 15 fields; 1.3.4 covers every vendor/merchant,
  1.3.5 adds Ariel (Wardrobe) and the guild recruiters (Guild). Full description, the table and the per-NPC decision list:
  [npc-services.md](npc-services.md) and [npc-services-catalog.md](npc-services-catalog.md).
- **Checked by:** `NpcServiceTableTests` and the `Player.log` lines `[Ro3Emu.NpcShops] service options added ...`. Visual check
  in game by the player is still to be confirmed.

## 2026-10-22: level-up sprite for evolving monsters (client hook, release 1.3.2)

- A monster that evolves by defeating players plays the character's own level-up sprite. The client says so with message 77017
  (`MonsterService.LevelUpFxHello`); clients without the hook keep the stand-in effects. Test:
  `SkillCombatTests.AnEvolvingMonsterPlaysTheLevelUpSpriteForClientsThatHaveIt`.

## 2026-10-22: HUD ping

- The Pong must echo the Ping tag; see [hud-ping.md](hud-ping.md).

## 2026-10-26 - Any party member may start a team dungeon; no search prompt; weaker boss for incomplete parties

Status: DEPLOYED 00:29 UTC (server DLL 0069b6e1..., rollback `Ro3Emu.Server.dll.prev-20261026-002930-anymember`) and client package 1.3.7 (hooks DLL 4061ed92, rollback `dl-prev-1.3.6` on the VPS).
- Problem: the story quest "Bandidos Sombrios" dungeon refused a non-leader re-entering after death with "Sem permissao" (NoPermission, DungeonService.EnterAsync).
- Server: `Ro3Emu:DungeonAnyMemberEnter` (default true) lets any member start; the leader stays owner and confirms. `Ro3Emu:DungeonMissingMemberPercent` (default 5): a party with its minimum but not its maximum meets a team boss 5% weaker (life and attack) per missing member, capped at 50% (DungeonSoloScale.For third argument).
- Client: SoloDungeonEntry hook wraps `_checkToEnterDungeon` so a party at or above the minimum and below the maximum sends 30400 directly instead of the "Grupo tem menos de 5" prompt (UIWords 56237). Untested in a real client.

## 2026-10-26 - Panel gave gear in the wrong bag tab; misfiled items move at login
- Panel "Dar este item" stored every item as pack 1 (item tab) with the whole count in one row; gear belongs to pack 2 (`_iBagTab`, one piece per row), totems 4, Soul Echoes 5. Worn gear stored in pack 1 was not read as worn by the server.
- Panel: `item_bag_info`/`add_item_row` (admin.py) now pick the pack like `CharacterService.ConfigPack` and split stacks; with the character online it calls the server's own `!item` through `/ro3emu/gm` (instant, no queue).
- Server: `MigrateMisfiledItems` at login moves every pack-1 item whose expected pack differs (worn gear keeps its grid). Test `GearStoredInTheItemTabMovesToTheEquipmentTabAtLogin`.
- Rollback: server `Ro3Emu.Server.dll.prev-20261026-033220-equippack` / `-033537-equippack2`; panel `/home/deploy/ro3admin/backup-20261026-equippack/`.

## 2026-10-26 - Player trade, send to a character and account vault (new)
- New `PlayerTradeService` (game messages 77020 up / 77021 down, JSON; docs/systems/player-trade-vault.md): direct trade between two online players (offers, ready flags, 2 s anti-scam delay, commit in one `MarketStore` transaction), `send` of items/zeny to any character by name, and a 60-slot account vault shared by all characters of the account. Received goods arrive as system mail (new raw market letter: `MailService.DeliverMarketRaw`, `MarketTransaction.DeliverRawMail`).
- Economy ledger: new compartment `EconomyCompartment.Vault = 24` and `vault` market records in both snapshot readers; vault entries/zeny stay owned by the depositing role.
- Chat commands `/troca [name]`, `/enviar`, `/cofre`; every committed move is appended to `<data>/player-trade-log.jsonl`.
- Client: `TradeWindow` (IMGUI) in Ro3Emu.ClientHooks, F9 or server `show`; kill switch env `RO3EMU_NO_TRADE_WINDOW=1`. Not verified in a real client.
- Tests: `PlayerTradeTests` (trade swap, refusals, send to offline role, vault put/take) over the real protocol with the economy ledger active.

## 2026-10-26 - Skills in progress are no longer cancelled by another skill (Ro3Emu:SkillNoCancel)
- Seen: in the client's auto-battle, Storm Gust and Lord of Vermilion were cancelled right after the cast by the next skill.
- Cause: the client sends 31124 "interrupt + cast next" (NxtSkill) whatever `_bCancelCast` says, and the server accepted it (and let `_bCancelCast` 1 skills be replaced by a new cast).
- Fix (`CombatService.Casting.cs`, option `Ro3Emu:SkillNoCancel`, default on, `Ro3Emu__SkillNoCancel=false` restores the original): while a skill (not a basic attack) is in its chant/action, a new cast is refused (busy) and 31124 with NxtSkill answers `IsInterruptSucc=false`. Moving still cancels and a plain interrupt (cast bar button) still works.
- Checked by: `SkillCombatTests.ASkillInProgressIsNotCancelledByAnotherSkill`; 196 combat tests pass.
- Rollback: `Ro3Emu.Server.dll.prev-*-nocancel`.
