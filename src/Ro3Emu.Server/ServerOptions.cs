namespace Ro3Emu.Server;

public sealed class ServerOptions
{
    /// <summary>Address the client is told to connect to (server list gate addresses). Must be an IP literal.</summary>
    public string PublicHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// Interface the HTTP and gate ports listen on. Empty: loopback when <see cref="PublicHost"/> is a loopback
    /// address (local play and the tests: no Windows Firewall prompt per test host), otherwise every interface.
    /// </summary>
    public string? BindAddress { get; set; }

    public System.Net.IPAddress ListenAddress() =>
        BindAddress is { Length: > 0 } bind ? System.Net.IPAddress.Parse(bind)
        : System.Net.IPAddress.TryParse(PublicHost, out var host) && System.Net.IPAddress.IsLoopback(host) ? System.Net.IPAddress.Loopback
        : System.Net.IPAddress.Any;

    /// <summary>The voice relay serves only with a port set, and off loopback only when plaintext was allowed on purpose.</summary>
    public bool VoiceServed() =>
        Voice.Port > 0 && (System.Net.IPAddress.IsLoopback(ListenAddress()) || Voice.AllowPlaintextOffLoopback);

    public int HttpPort { get; set; } = 5380;

    public int MicroGatePort { get; set; } = 20000;

    public int GameGatePort { get; set; } = 33100;

    public string DataDir { get; set; } = "data";

    /// <summary>Story dungeons whose encounter scripts are not in the client use the reconstructed waves in
    /// Reconstructed/dungeon-monsters.json (inferred, marked "odtworzone"); off refuses them as before.</summary>
    public bool ReconstructedDungeons { get; set; } = true;

    /// <summary>Game-message JSON log (data/logs/traffic-*.jsonl): "off", "on" or a comma-separated account list.
    /// Formatting and writes run off the game loop; keep it off for load tests and production.</summary>
    public string TrafficLog { get; set; } = "off";

    /// <summary>Parse all client config tables before the gates open (tools/run-server.ps1 turns it on). Off by
    /// default so the hundreds of short-lived test hosts load only the tables they touch.</summary>
    public bool PreloadGameData { get; set; }

    /// <summary>
    /// Folder with the data exported from the client: config/ (tools/ConfigExport), sceneeditor/ and nav/
    /// (tools/WorldData); default DataDir.
    /// </summary>
    public string? GameDataDir { get; set; }

    /// <summary>Walk check against the client's nav grid: "off", "log" (report only) or "enforce" (snap back).</summary>
    public string WalkCheck { get; set; } = "log";

    public int ClusterId { get; set; } = 1;

    public string ClusterName { get; set; } = "RO3Emu";

    /// <summary>The server (area) inside the cluster: the login screen shows the cluster name above it.</summary>
    public string AreaName { get; set; } = "Prontera";

    /// <summary>
    /// Stall title limit in characters. The original's PublicParamConfig.StallsNameCharacterLimit (10) was sized for
    /// Chinese; English titles need more. ClientHooks.LuaPatches sets the client's input limit to the same value.
    /// </summary>
    public int StallNameMaxLength { get; set; } = 24;

    /// <summary>
    /// Address of this server's web shop. When set, an in-game purchase button (CreateOrderReq, 2001) answers with it as the
    /// order's transaction_url, which the PC client opens in the browser (Lua_SDK_PayMgr._onCreateOrderResult); the shop does the
    /// buying. Empty: purchases stay unavailable. A "cat" query value (Cristais for recharge, Passes for products) is added.
    /// </summary>
    public string StoreUrl { get; set; } = "";

    /// <summary>
    /// In-game purchases (CreateOrderReq) are paid with the credits of the web shop kept in ro3emu.db by the admin panel
    /// (tables panel_credit and panel_ledger), 1 credit per USD of the original price; see CreditShop. Off: purchases are
    /// unavailable, or point at <see cref="StoreUrl"/> when that is set.
    /// </summary>
    public bool CreditShop { get; set; }

    /// <summary>
    /// The live season playbook (SeasonService). 101 is the only one this client has season content for; a value
    /// set in game with "!season" overrides it.
    /// </summary>
    public int PlayBook { get; set; } = 101;

    /// <summary>The season's first day, yyyy-MM-dd; empty means the server's first start (kept in the database).</summary>
    public string? SeasonStart { get; set; }

    /// <summary>
    /// Owner policy (2026-10-11): every piece of content the client can show is playable. Off by default so the tests
    /// keep pinning the original's rules; appsettings.json turns it on for the real server.
    /// </summary>
    public OpenContentOptions OpenContent { get; set; } = new();

    /// <summary>
    /// A game message sent before the stage it belongs to (no account signed in, or no role selected) is dropped
    /// unanswered instead of reaching its handler (upstream #36). Turn off (Ro3Emu__RefuseEarlyMessages=false) only to
    /// diagnose a client that is refused at login; the log names the first refused message of each connection.
    /// </summary>
    public bool RefuseEarlyMessages { get; set; } = true;

    /// <summary>Battle pass policy: the premium track is free for now.</summary>
    public PassOptions Pass { get; set; } = new();

    /// <summary>
    /// Accounts allowed to use GM commands in game ("!" lines in chat, the client's GM panel, 1031/1034). Empty:
    /// <see cref="GmOpenWhenEmpty"/> decides. docs/guide/gm-access.md.
    /// </summary>
    public string[] GmAccounts { get; set; } = [];

    /// <summary>
    /// What an empty <see cref="GmAccounts"/> means in game: true = every account has GM (research server, tests,
    /// local play), false = nobody has GM. Left unset, ServerHost decides from the host environment: false when it was
    /// set to Production explicitly (ASPNETCORE_ENVIRONMENT, DOTNET_ENVIRONMENT or --environment), true otherwise. The
    /// loopback HTTP POST /ro3emu/gm (the operator's tools) keeps the old rule either way: empty = every account.
    /// </summary>
    public bool? GmOpenWhenEmpty { get; set; }

    /// <summary>In-game bug reports (FeedbackService, docs/guide/feedback.md).</summary>
    public FeedbackOptions Feedback { get; set; } = new();

    /// <summary>
    /// Credential checks (SDK login, character deletion) one remote address may make per minute; past it the server
    /// answers 429 without hashing. Each check costs a 100 000-round PBKDF2 hash, and logins register accounts.
    /// Loopback is exempt (local play, the load tests, RO3Emu.exe); behind a reverse proxy every player shares the
    /// proxy's address, so raise this there.
    /// </summary>
    public int LoginAttemptsPerMinute { get; set; } = 20;

    /// <summary>Tests: the credential limit applies to loopback too.</summary>
    public bool LoginLimitIncludesLoopback { get; set; }

    /// <summary>Team voice relay proof of concept (Voice/, docs TBD). Off until a port is set.</summary>
    public VoiceOptions Voice { get; set; } = new();

    /// <summary>(inferred) Locally configured text blocklist; the original service's sensitive-word dictionary is unavailable.</summary>
    public string[] GuildBlockedWords { get; set; } = [];

    /// <summary>
    /// "instant": a guild is Normal at once unless the founder ticks the create widget's responder toggle (this server's
    /// shortcut). "responders": every guild is founded through GuildResponsePeopleLimit responders, as the original;
    /// the toggle then only waives the founder's approval of each response.
    /// </summary>
    public string GuildFounding { get; set; } = "instant";

    /// <summary>
    /// Guild League registration's membership threshold. Unset: GuildSettingConfig GuildLeaguePeopleNum (40, read as the
    /// "Guild membership reaches ${1}" requirement, inferred). A small realm or a test can lower it.
    /// </summary>
    public int? GuildLeagueMinMembers { get; set; }

    /// <summary>
    /// Seconds an in-game role stays in the world after its game socket drops, waiting for the client's silent
    /// reconnect (Phonest ConnectServerReq ReConnection). 60 is the reference
    /// AVATAR_LOSE_CONNECTION_TIMEOUT_SECONDS; it must exceed the client's 20 s + 10 s budgets. 0 turns grace off.
    /// </summary>
    public int ReconnectGraceSeconds { get; set; } = 60;

    /// <summary>
    /// TEST AID, not original behaviour: Survivor Battle (match entry 2) starts with this many entrants in teams of
    /// <see cref="ChickenTestTeamSize"/> instead of EatChickenMatchConfig's 100 in teams of 5 (the client gives no sign of
    /// server-side filler bots). 0 keeps the config. GM "!chicken shape" sets the same in memory.
    /// </summary>
    public int ChickenTestPlayers { get; set; }
    public int ChickenTestTeamSize { get; set; }

    /// <summary>
    /// (inferred) Seconds of a Realm of the Gods raid's preparation phase: the boss waits until the team leader presses the
    /// scene's "Start Battle" button (DungeonLua_30201xx DungeonBase.FunctionButtons, sent as 30125) or this time runs out.
    /// The client has the button, not the phase length.
    /// </summary>
    public int RaidPreparationSeconds { get; set; } = 120;

    /// <summary>
    /// Not original behaviour, off by default: a lone player may enter and match into a team boss (the 30400 entry and the
    /// 82681 matching) without waiting for a full team, as if every role had run the GM test aid "!dungeonsolo on". The boss
    /// keeps its team-sized stats, so it is harder alone. Environment variable: Ro3Emu__DungeonSolo=true.
    /// </summary>
    public bool DungeonSolo { get; set; }

    /// <summary>
    /// Not original behaviour (the original refuses with "no permission" unless the leader starts): any member of a party may
    /// start its team dungeon with 30400, e.g. to re-enter a story dungeon after dying in it. The party leader stays the
    /// room's leader and confirms like every other member. Default on; Ro3Emu__DungeonAnyMemberEnter=false restores the original.
    /// </summary>
    public bool DungeonAnyMemberEnter { get; set; } = true;

    /// <summary>
    /// Not original behaviour: a team boss met by a party that has its minimum but not its maximum (e.g. 3 or 4 of 5) is this many
    /// percent weaker in life and attack for each missing member (default 5, so 3 of 5 meets it 10% weaker); 0 = unchanged.
    /// Capped at 50% in all. Below the minimum dungeon-solo.json applies instead. Environment variable: Ro3Emu__DungeonMissingMemberPercent.
    /// </summary>
    public int DungeonMissingMemberPercent { get; set; } = 5;

    /// <summary>
    /// Not original behaviour: a skill being cast (chant and action) is never cancelled by another skill, whatever its _bCancelCast:
    /// a new cast during it is refused (busy) and the client's "interrupt and cast the next skill" request (31124 with NxtSkill) is
    /// refused too, so channelled skills (Storm Gust, Lord of Vermilion...) finish even under the client's auto-battle. Moving
    /// still cancels a cast that cannot be moved, and a plain interrupt (the cast bar's button) still works. Default on;
    /// Ro3Emu__SkillNoCancel=false restores the original.
    /// </summary>
    public bool SkillNoCancel { get; set; } = true;

    /// <summary>
    /// Not original behaviour, ON by default (owner's choice): every card slot of every equipment grid counts as open. The game data gives the head
    /// accessory (and face and mouth) grids no unlock condition at all, so their cards ("part 7 to 9") were refused with SlotSealed;
    /// with this on the sockets are open (the worn-weapon rule of the weapon grids still applies) and face cards (part 8) go to the
    /// face grid 9. Turn it off for the original rule with the environment variable Ro3Emu__OpenCardSlots=false.
    /// </summary>
    public bool OpenCardSlots { get; set; } = true;

    /// <summary>
    /// Not original behaviour: after a story dungeon (a reconstructed fight or scripted scene, no team boss) is cleared the
    /// client shows no result window and the quest tracker keeps the dungeon's text, so nothing tells the player to press the
    /// exit button. The players get a notice and are returned to the field after this many seconds. 0 turns it off
    /// (Ro3Emu__StoryDungeonExitSeconds=0); the exit button still works at any time.
    /// </summary>
    public int StoryDungeonExitSeconds { get; set; } = 8;

    /// <summary>
    /// Not original behaviour: a party shares its kills. Every other member who is online, alive, on the killer's map and within
    /// <see cref="PartyKillShareRange"/> gets the kill's EXP (at their own level, with the party EXP bonus) and the kill counts for
    /// their running kill quests as if they had struck the last blow. Items still go to the killer only.
    /// Ro3Emu__PartyKillShare=false restores the original rule (EXP and quest credit for the killer and the players who hit it).
    /// </summary>
    public bool PartyKillShare { get; set; } = true;

    /// <summary>
    /// DEX shortens the variable part of a skill's cast time, as the client's skill tooltip shows it: every DEX point gives 2 ChantSpeed
    /// (AttributeConversionConfig) and the variable chant is multiplied by max(0, 1 - sqrt(ChantSpeed / 600)); the fixed part is untouched.
    /// Ro3Emu__ChantSpeedReduction=false makes every cast last exactly _iChantFix + _iChantVar.
    /// </summary>
    public bool ChantSpeedReduction { get; set; } = true;

    /// <summary>
    /// Skill points are one pool shared by every class of the tree: the points left in any rank can be spent on skills of any rank you can
    /// reach, and every class tab shows the whole pool. Ro3Emu__SharedSkillPoints=false restores one pool per rank.
    /// </summary>
    public bool SharedSkillPoints { get; set; } = true;

    /// <summary>
    /// Players are reported as battling (state bit 16) while they fight, which locks the skill panel, build switch, stat points, mounts,
    /// stalls and other actions for a few seconds after the last hit. Off by default: nothing is locked in combat.
    /// Ro3Emu__CombatLocks=true restores the lock.
    /// </summary>
    public bool CombatLocks { get; set; }

    /// <summary>
    /// Seconds a role must wait between two skill Preset switches (1 to 5, each with its own skill bar). The original game waits
    /// PublicParamConfig.SchemeCD (3 s); 0 (default) switches at once. A GM can change it while the server runs with !presetcd.
    /// </summary>
    public int PresetSwitchCooldown { get; set; }

    /// <summary>
    /// A player's damage bonuses (Magic/Physical Damage Bonus% 213/211, Damage Increase% 215, Skill Damage Increase% 225, melee/ranged,
    /// Crit Damage% 209) scale the damage it deals to monsters; cards and gear that grant them take effect. Ro3Emu__DamageBonusAttributes=false
    /// leaves the damage at ATK/MATK, skill ratio, defense and element only.
    /// </summary>
    public bool DamageBonusAttributes { get; set; } = true;

    /// <summary>
    /// Cast-time and cooldown attributes of the player (cards, buffs, potions): Variable CT 202 / Fixed CT 204 change the chant, Skill CD% 205 and
    /// Global CD% 206 shorten the skill's own and the public cooldown (positive = shorter, as Arcane Acceleration and the Cooldown Potion list them).
    /// Ro3Emu__CastAttributes=false leaves chant and cooldown at the data's values (and the DEX reduction).
    /// </summary>
    public bool CastAttributes { get; set; } = true;

    /// <summary>Diagnostics: logs, for every damaging player skill hit on a monster, the attacker's Matk/Atk, the monster's level, defences and element and the damage before/after rolls. Ro3Emu__DamageTrace=true.</summary>
    public bool DamageTrace { get; set; }

    /// <summary>Metres (map units) from the killer within which a party member shares the kill; 0 (default) = anywhere on the map.</summary>
    public int PartyKillShareRange { get; set; }

    /// <summary>Largest level difference (either way) between the killer and a party member who shares the kill; 0 = no limit.</summary>
    public int PartyKillShareLevelGap { get; set; } = 20;

    /// <summary>
    /// Not original behaviour: a story task that waits for a cutscene (target 309002) is finished only when the client reports the
    /// cutscene's end (84011). When the client hangs inside the cutscene (the "Strange Occurences" pendant scene) it never does, the player
    /// has to close the game and the cutscene starts again at the next login. A role that logs in with such a task running for at least
    /// this many seconds has the cutscene counted as played, so the story goes on. 0 turns it off (Ro3Emu__PlotStallSeconds=0).
    /// </summary>
    public int PlotStallSeconds { get; set; } = 20;

    /// <summary>
    /// (inferred) Seconds from a raid boss fight's start to its enrage, against which the Final Trial "more than N minutes
    /// remaining until enrage" honors (TenGameplayFinalTrialConfig, condition 7004) are judged. The enrage timer lives in
    /// the boss's server script, which the client does not carry.
    /// </summary>
    public int RaidEnrageSeconds { get; set; } = 600;

    /// <summary>Boss special skills (BossSkillSets, MonsterService.BossSkills): the scheduler's inferred numbers.</summary>
    public BossSkillOptions BossSkills { get; set; } = new();
}

/// <summary>
/// The boss skill scheduler. The skill rows, their cooldowns, cast and action times, ranges, damage and buffs come from
/// SkillConfig; the original server's AI scripts that decided when to cast are not in the client, so every number here
/// is (inferred) and can be tuned without a code change.
/// </summary>
public sealed class BossSkillOptions
{
    /// <summary>Off: bosses only swing their normal attack, as before the scheduler.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after a boss engages before its first special skill.</summary>
    public int FirstCastDelayMs { get; set; } = 3000;

    /// <summary>Pause after a special's action ends before the next one (the boss swings meanwhile); a row's longer
    /// _iPubCD wins.</summary>
    public int GlobalCooldownMs { get; set; } = 2000;

    /// <summary>Cooldown of a skill whose family carries only placeholder cooldowns (every row 0-2 s: Realm of the
    /// Gods, story Baphomet and Pharaoh), where the encounter script set the pace.</summary>
    public int PlaceholderCooldownMs { get; set; } = 15000;

    /// <summary>HP percent at which a boss with phase variants enters each next phase (phase 1, phase 2...).</summary>
    public int[] PhaseHpPercents { get; set; } = [50, 25];

    /// <summary>Area radius (m) when neither the row nor its show template gives one.</summary>
    public float DefaultRadius { get; set; } = 5f;

    /// <summary>Longest distance (m) to its target at which a boss starts a targeted skill; rows with "anywhere"
    /// ranges (_iDistanceMax 9999) are capped to it.</summary>
    public float MaxReach { get; set; } = 15f;

    /// <summary>Most hits one cast lands (a show's attack points; Ring of Light has three).</summary>
    public int MaxHitsPerCast { get; set; } = 6;

    /// <summary>Boss skill damage as a percent of the row's formula result.</summary>
    public int DamagePercent { get; set; } = 100;

    /// <summary>Minions of a summon skill whose description gives no count.</summary>
    public int SummonCount { get; set; } = 3;

    /// <summary>Life of a summoned minion without a self-destruct born buff.</summary>
    public int SummonLifeMs { get; set; } = 30000;
}

/// <summary>In-game bug reports: the menu's Feedback button and "/bug" in chat (FeedbackService).</summary>
public sealed class FeedbackOptions
{
    /// <summary>Longest report text kept (characters); longer text is cut and marked truncated.</summary>
    public int MaxLength { get; set; } = 1000;

    /// <summary>Server log lines about the player stored with a report (RecentLogBuffer); 0 keeps none and leaves the
    /// buffer out of the logging pipeline.</summary>
    public int ContextLines { get; set; } = 40;

    /// <summary>How far back those log lines may reach.</summary>
    public int ContextMinutes { get; set; } = 15;

    /// <summary>Minimum seconds between two reports of one account.</summary>
    public int CooldownSeconds { get; set; } = 5;
}

/// <summary>
/// Team voice relay (Talk/Listen/Mute in the party panel). The listener only opens when <see cref="Port"/> is set;
/// an off relay still answers the 77015 ticket request with port 0 so the client's menu can disable itself cleanly.
/// </summary>
public sealed class VoiceOptions
{
    /// <summary>TCP port the voice gate listens on; 0 keeps the relay off.</summary>
    public int Port { get; set; }

    /// <summary>Members a single voice room (one party/team) may hold.</summary>
    public int MaxRoomSize { get; set; } = 40;

    /// <summary>Audio frames a connection may send per second before the token bucket drops the extra ones.</summary>
    public int FramesPerSecond { get; set; } = 50;

    /// <summary>Extra frames the token bucket allows in a short burst on top of <see cref="FramesPerSecond"/>.</summary>
    public int Burst { get; set; } = 10;

    /// <summary>Control frames (Join, Leave, Mic, Speaker, Ping, anything else after Auth) a connection may send per
    /// second; the extra ones are dropped, and a sustained flood (3x this rate over 5 s) disconnects it. A real
    /// client sends a few per minute: a ping every 10 s plus the player's own clicks.</summary>
    public int ControlPerSecond { get; set; } = 10;

    /// <summary>Extra control frames allowed in a short burst on top of <see cref="ControlPerSecond"/>.</summary>
    public int ControlBurst { get; set; } = 20;

    /// <summary>Longest opus payload accepted in one Audio frame, in bytes.</summary>
    public int MaxPayload { get; set; } = 512;

    /// <summary>How long a 77015 ticket stays valid before a connection must present it.</summary>
    public int TicketSeconds { get; set; } = 60;

    /// <summary>Voice connections the listener accepts at once; past this new connections are refused.</summary>
    public int MaxConnections { get; set; } = 1024;

    /// <summary>Voice connections one IP address may hold at once (a LAN party behind one router needs a few).</summary>
    public int MaxConnectionsPerIp { get; set; } = 8;

    /// <summary>Connections from one IP that end without a valid Auth before that IP is refused for a minute.</summary>
    public int MaxFailedAuthPerIp { get; set; } = 10;

    /// <summary>
    /// The voice stream is not encrypted yet (only the ticket travels inside the encrypted game gate), so the relay
    /// refuses to listen on anything but loopback unless this is set on purpose.
    /// </summary>
    public bool AllowPlaintextOffLoopback { get; set; }

    /// <summary>Seconds a fresh connection has to send its Auth frame before it is closed. A test host lowers this.</summary>
    public int FirstFrameTimeoutSeconds { get; set; } = 5;

    /// <summary>Seconds without any frame before an authed connection is closed as idle. A test host lowers this.</summary>
    public int IdleTimeoutSeconds { get; set; } = 30;
}

/// <summary>Both off by default so the tests keep the original's pass; a realm turns them on (docs/systems/activities-battlepass-monthcard.md).</summary>
public sealed class PassOptions
{
    /// <summary>Every role gets the premium track and its privileges without the premium item 703.</summary>
    public bool FreePremium { get; set; }

    /// <summary>Pass EXP from play: challenges, daily activity, monsters, dungeons, MVPs and story (NewActivityService.PassPlay).</summary>
    public bool Play { get; set; }
}

/// <summary>
/// "Everything open" ("Policy: all content open"). The playbook stays 101: it is the only one
/// with its own content in this client, so seasons need no switch.
/// </summary>
public sealed class OpenContentOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Functions the original never opened (base level 999/9999) whose client UI and data work: 7201 Realm War map,
    /// 70122 Totem Shop, 41 Feedback, 1000000 Adv. Guild (AdventureGroupService), 96 Valhalla Trials (ValhallaService)
    /// (Feedback's panel comes from Ro3Emu.ClientHooks.FeedbackButton, its backend is FeedbackService), 69 the season page (this export gates it at base level 9999; its backend is SeasonPageService and it was opened at level 50 before). Shells without a working panel (Title 14, Academy 34...) stay closed so the menu shows no dead
    /// buttons; a function joins this list together with its backend. Novice Challenge (90, activity 112) is left out
    /// (owner, 2026-10-11): its 15 random event texts are "Event Text..."/"Option A" filler and the first node's
    /// dialogues 9901-9904 do not exist, so the original never finished it.
    /// </summary>
    public long[] Functions { get; set; } = [7201, 70122, 41, 1000000, 96, 69];

    /// <summary>
    /// NewActivityConfig rows whose own open condition is a level the cap never reaches (e.g. 112 Novice Challenge).
    /// 200 Pass I asks for base level 99 (condition 21000019). Pass II (201, the same condition and EXP currency) opens
    /// after Pass I, when the owner adds it here.
    /// </summary>
    public long[] Activities { get; set; } = [200];

    /// <summary>
    /// The season page's stage activities (MultiverseStage._kStageActivity of the playbook: for 101 that is 105 Fragment
    /// Gather, 106 Treasure, 107 Middle Season, 109/110 Racing, 111 World Exploration, 115 Emblem) are open whatever
    /// their own condition: every one is [1002,99], the final level cap (BaseLvLimitConfig day 198), so on a fresh realm
    /// the season page would list them all as locked for months. Their time windows still apply (107 keeps its
    /// settlement bounds, KeepTimeLimits), and World Exploration's chapters open with them (NewActivityService.WorldPhaseOpen).
    /// </summary>
    public bool SeasonStageActivities { get; set; } = true;

    /// <summary>Server-day conditions (3031 open days, 3030 playbook seconds) count as met from the first day.</summary>
    public bool IgnoreServerDay { get; set; } = true;

    /// <summary>
    /// TimeLimitConfig windows bounded by the server opening or by season stages open at the server opening and never
    /// close (every such id has a single state row: card pools, passes, Novice Challenge, Battle for Prontera, tower
    /// races...). Windows of activities given by absolute dates that already passed repeat with their own length.
    /// </summary>
    public bool PerpetualWindows { get; set; } = true;

    /// <summary>Windows that keep their original bounds: 1021 Midterm Settlement, 1023 Top 10 Guilds, 1036 Feedback (settlements, not content).</summary>
    public long[] KeepTimeLimits { get; set; } = [1021, 1023, 1036];

    /// <summary>NewActivityConfig rows that stay closed: 1011-1014, the original's "Test ... Pool" rows.</summary>
    public long[] ClosedActivities { get; set; } = [1011, 1012, 1013, 1014];

    /// <summary>Absolute windows that also become perpetual: 1060, the Territory War activity around the May block.</summary>
    public long[] PerpetualAbsolute { get; set; } = [1060];

    /// <summary>
    /// Territory Wars: the exported block of battles (2026-05-19..27, absolute dates) repeats every N days; 0 keeps the
    /// absolute dates. Each repeat is its own territory season (TerritoryWarsClock.Epoch), so rewards settle every cycle.
    /// </summary>
    public int TerritoryWarCycleDays { get; set; } = 14;

    public bool Perpetual(long timeLimitId) => Enabled && PerpetualWindows && !KeepTimeLimits.Contains(timeLimitId);
    public bool PerpetualAbsoluteWindow(long timeLimitId) => Enabled && PerpetualWindows && PerpetualAbsolute.Contains(timeLimitId);
    public long TerritoryWarPeriod => Enabled && TerritoryWarCycleDays > 0 ? TerritoryWarCycleDays * 86400L : 0;
    public bool Closes(long activityId) => Enabled && ClosedActivities.Contains(activityId);
    public bool Forces(long functionId) => Enabled && Functions.Contains(functionId);
    public bool ForcesActivity(long activityId) => Enabled && Activities.Contains(activityId);
    public bool OpensStageActivities => Enabled && SeasonStageActivities;
    public bool ServerDayFree => Enabled && IgnoreServerDay;
}
