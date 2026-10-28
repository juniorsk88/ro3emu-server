using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Socketed cards' Buff(2) special effects (CardConfig._kSkillAttribute -> SpecialEffectConfig._kSpecialEffect [2, buff],
/// 304 of the 348 card operations; Attr(1) and Attr1CountAddAttr2(8) are EquipmentStunts'). Every referenced BuffConfig
/// row is a zero-time passive (iPassiveBuff 1): like totem passives they are registered per source (the card GUID) for
/// as long as the copy is socketed, which publishes their icons, applies their _kChangeAttri (Variable CT, 2024001..4)
/// and arms BuffService's lethal-hit revive for groups 20520/20521 (2052001.., 2052101..). (inferred) Their
/// _iSceneClear removes them on a scene change; the loaded scene registers them again, carrying the revive cooldown.
///
/// Executed behaviours (data-settled families; the trigger timing "when dealing damage" is read from the
/// descriptions and applied to every damaging hit on a monster, inferred):
/// - status on hit: a base passive with _kLogicData [0, status buff, ms] (2000001 Stun 901601 500 ms, Blind, Curse,
///   Immobilize, Sleep, Freeze, Silence) gains chance from every passive whose _kBuffAttributeList row
///   ["0", base group, "4", "1", chance, "0"] names its group ("Chance stacks with multiple cards"); the description's
///   third value is the internal cooldown in seconds. The status lands as a monster unit buff, as skill _kBuff2 does.
/// - life steal 20170 (2017101..): chance from the same modifier rows, heal = desc[1]% of the damage up to desc[2],
///   internal cooldown desc[3] seconds.
/// - SP absorb 2050401: _kLogicData [chance bp, ...], desc[1]% of the damage as SP.
/// - size: 20501 (2050101..) adds _kLogicData[0] bp of damage against Small/Medium/Large (NPCConfig._iSize 1..3)
///   monsters; 20500 (2050001..) adds _kLogicData[1] bp with a _kLogicData[0] bp chance.
/// Not executed (no machinery or no data link): autocast skills (Bash, Jupitel Thunder, Divine Light), race-family
/// damage (Orcs, Kobolds, ...: no monster family field), armor element changes, magic nullify/reflect, status
/// immunities, Endure, cooldown reset, monster SP drain, Splash, Double Attack, detection, indestructible gear.
/// Skill(3) grants (Heal, Disguise, Magnum Break, Envenom, Sight, Sightless Mind) go into RoleProgress.GrantedSkills.
/// </summary>
public sealed class CardEffects
{
    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly BuffService _buffs;
    private readonly SkillService _skills;
    private readonly ConditionalWeakTable<GameSession, Runtime> _runtime = new();

    private sealed class Runtime
    {
        /// <summary>Last registered buff GUID per (card GUID, config), to carry BuffEffectCds over a re-registration.</summary>
        public readonly Dictionary<(long Source, int Config), long> Registered = new();
        public readonly Dictionary<long, long> Cooldowns = new(); // proc family (base buff group) -> ready at
        public long NextHitUntil, NextHitBp; // "your next instance of damage is increased" after a critical hit
    }

    private sealed record Passive(InvItem Card, int Id, JsonElement Row);

    private const long SkillOp = 3;
    private const int LifeStealGroup = 20170, SpAbsorb = 2050401, SizeAlways = 20501, SizeChance = 20500;
    private const int DoubleAttack = 20210, CooldownReset = 20529, MagicNullify = 20510;
    private const int SplashChance = 20508, SplashAlways = 20509, NextHitAfterCrit = 20507, ImmuneSlow = 20525, ImmuneStun = 20527, Endure = 20523;
    private const int Phen = 20240, Ladybug = 20260, Angeling = 20514, AngelingHolyArmorBuff = 904407, // BuffConfig "Convert Armor to Holy Property", Angeling's _kLogicData[1]
         SilenceBuffGroup = 9023;
    private const int DivineLight = 20531, ReflectMagic = 20512, ReflectMagicVit = 20513, ReflectPhysical = 20516, ReflectPhysicalVit = 20517;
    private static readonly int[] AutocastGroups = [20190, 20200]; // Bash, Jupitel Thunder (the chance rides on the card's modifier row)

    /// <summary>Buff groups by their English name (the status immunities and Endure act on these).</summary>
    private readonly Lazy<Dictionary<string, HashSet<int>>> groupsByName;
    private bool Named(string name, int group) => groupsByName.Value.TryGetValue(name, out var set) && set.Contains(group);

    /// <summary>Armor element cards (buff group -> EleConfig element): Earth 3, Water 5, Wind 2, Poison 6, Fire 4, Ghost 9, Holy 7.</summary>
    private static readonly Dictionary<long, int> ArmorElements = new()
        { [20090] = 3, [20100] = 5, [20110] = 2, [20120] = 6, [20130] = 4, [20140] = 9, [20515] = 7 };

    /// <summary>"Damage against all Orcs / Kobolds / Kara Crabs / Shellfish / Aster / Goblins +X%": group -> does this monster's name belong. (inferred)
    /// The data has no family field (NPCConfig._iRace is the race class), so the family is read from the monster's name.</summary>
    private static readonly Dictionary<long, Func<string, bool>> RaceGroups = new()
    {
        [20150] = n => System.Text.RegularExpressions.Regex.IsMatch(n, @"\bOrc\b|Zenorc") && !n.Contains("Lord") && !n.Contains("Hero")
            && !n.Contains("Skeleton") && !n.Contains("Zombie"),
        [20220] = n => n.Contains("Kobold"),
        [20270] = n => n == "Crab" || n.Contains("Kara Crab"),
        [20280] = n => n.Contains("Shellfish"),
        [20290] = n => n == "Aster",
        [20532] = n => n.Contains("Goblin"),
    };

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> monsterNames = new();
    private string MonsterName(int npcId) => monsterNames.GetOrAdd(npcId, id => _data.Row("NPCConfig", id) is { } row ? _data.TextOf(row, "_kName") : "");

    private readonly CombatService combat;

    // A test can fix each ten-thousandth roll without changing the production probability.
    internal Func<int, int> Roll { get; set; } = bound => Random.Shared.Next(bound);

    public CardEffects(GameData data, CharacterService character, BuffService buffs, CardService cards, CombatService combat,
        SceneService scenes, SkillService skills)
    {
        this._data = data;
        this._skills = skills;
        this._character = character;
        this._buffs = buffs;
        this.combat = combat;
        groupsByName = new(() =>
        {
            var map = new Dictionary<string, HashSet<int>>();
            foreach (var row in _data.Table("BuffConfig").Values)
                if (GameData.Int(row, "_iBuffID") is var g and > 0 && _data.TextOf(row, "_iName") is { Length: > 0 } name)
                    (map.TryGetValue(name, out var set) ? set : map[name] = []).Add((int)g);
            return map;
        });
        _buffs.Refuses = RefusesBuff;
        combat.HealedAsync.Add(OnHealedAsync);
        combat.PvpHitTaken.Add(OnPvpHitTakenAsync);
        cards.SocketsChanged.Add(RefreshAsync);
        scenes.Loaded.Add(RefreshAsync);
        combat.DamageAdjusters.Add(AdjustDamage);
        combat.DealtAsync.Add(OnDealtAsync);
        combat.ExtraHitDamage.Add(ExtraHit);
        combat.SkillCooldownStarted.Add(OnCooldownStartedAsync);
        combat.IncomingHitNegators.Add(NegateMagicHit);
        combat.ArmorElementOf = ArmorElement;
        combat.CastIgnoresControl = s => Passives(s).Any(p => GameData.Int(p.Row, "_iBuffID") == Phen);
        combat.IncomingDamageAdjusters.Add(LessWhileSilencedAsync);
        combat.TakenAsync.Add(OnTakenAsync);
    }

    private IEnumerable<Passive> Passives(GameSession s)
    {
        foreach (var (card, id) in EquipmentStunts.CardEffects(s, _data))
            if (_data.Row("SpecialEffectConfig", id) is { } effect)
                foreach (var op in EquipmentAffixes.Rows(effect, "_kSpecialEffect"))
                    if (op.Length == 2 && op[0] == 2 && _data.Row("BuffConfig", op[1]) is { } buff)
                        yield return new Passive(card, (int)op[1], buff);
    }

    /// <summary>The passives currently registered for this session's socketed cards.</summary>
    internal IReadOnlyList<(long Source, int Config)> Registered(GameSession s) =>
        s.Buffs.Where(b => b.IsPassive && !b.IsTotemOwned).Select(b => (b.SourceId, b.ConfigId)).ToList();

    /// <summary>Reconcile card-owned passives with the socketed copies (insert, remove, transfer, plan switch,
    /// star-up, scene load).</summary>
    public async Task RefreshAsync(GameSession s)
    {
        if (s.Role == null) return;
        var wanted = Passives(s).Select(p => (Source: p.Card.Guid, p.Id)).ToHashSet();
        var state = _runtime.GetOrCreateValue(s);
        bool statsChanged = false;
        foreach (var stale in s.Buffs.Where(b => b.IsPassive && !b.IsTotemOwned && !wanted.Contains((b.SourceId, b.ConfigId))).ToArray())
        {
            statsChanged |= stale.Changes.Count > 0;
            state.Registered.Remove((stale.SourceId, stale.ConfigId));
            await _buffs.RemoveAsync(s, stale);
        }
        foreach (var (source, id) in wanted)
        {
            if (s.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned && b.SourceId == source && b.ConfigId == id)) continue;
            await _buffs.AddPassiveAsync(s, id, source, totemOwned: false);
            if (s.Buffs.FirstOrDefault(b => b.IsPassive && !b.IsTotemOwned && b.SourceId == source && b.ConfigId == id) is not { } added)
                continue;
            statsChanged |= added.Changes.Count > 0;
            // A scene change cleared the previous registration; its internal cooldown stays with the card.
            if (state.Registered.TryGetValue((source, id), out long previous) && s.BuffEffectCds.Remove(previous, out long readyAt))
                s.BuffEffectCds[added.Guid] = readyAt;
            state.Registered[(source, id)] = added.Guid;
        }
        await GrantSkillsAsync(s);
        if (!statsChanged) return;
        if (s.SceneLoading) _character.Refresh(s);
        else await _character.RefreshStatsAsync(s);
    }

    /// <summary>SpecialEffectConfig op Skill(3) [3, SkillConfig row] (enum_SpecialEffectType.Skill): the socketed card grants
    /// that row's group at its level while it stays socketed (Heal, Disguise, Magnum Break, Envenom, Sight, Sightless Mind;
    /// _iSystemType 3, enum_SkillSystemType.CardSkill). The skill panel lists them as card skills and they can be loaded
    /// and cast; the highest level of a group counts.</summary>
    private async Task GrantSkillsAsync(GameSession s)
    {
        if (s.Progress is not { } progress) return;
        Dictionary<int, int> granted = [];
        foreach (var (_, id) in EquipmentStunts.CardEffects(s, _data))
            if (SpecialEffectConfig.Get(_data, id) is { } effect)
                foreach (var op in effect.SpecialEffect)
                    if (op is [SkillOp, var row] && SkillConfig.Get(_data, row) is { SkillId: > 0 } skill)
                        granted[(int)skill.SkillId] = Math.Max(granted.GetValueOrDefault((int)skill.SkillId), (int)skill.Level);
        if (granted.Count == progress.GrantedSkills.Count && granted.All(g => progress.GrantedSkills.GetValueOrDefault(g.Key) == g.Value)) return;
        progress.GrantedSkills.Clear();
        foreach (var (group, level) in granted) progress.GrantedSkills[group] = level;
        if (s.SkillPlans != null) await _skills.SyncAsync(s);
    }

    private static long[] Numbers(JsonElement row, string field) => row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];

    private static double[] Description(JsonElement row) => row.TryGetProperty("_kDescData", out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Select(v => GameData.Number(v) ?? 0).ToArray() : [];

    /// <summary>_kBuffAttributeList ["0", group, "4", "1", value, "0"]: the passive adds value to field 4 (the trigger
    /// chance, basis points) of the passive group named.</summary>
    private static IEnumerable<(long Group, long Value)> ChanceModifiers(JsonElement row)
    {
        if (!row.TryGetProperty("_kBuffAttributeList", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 5) continue;
            var values = entry.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
            if (values[2] == 4 && values[3] == 1) yield return (values[1], values[4]);
        }
    }

    private bool Ready(GameSession s, long family, long cooldownMs)
    {
        var state = _runtime.GetOrCreateValue(s);
        long now = MonsterService.NowMs;
        lock (state)
        {
            if (state.Cooldowns.GetValueOrDefault(family) > now) return false;
            if (cooldownMs > 0) state.Cooldowns[family] = now + cooldownMs;
            return true;
        }
    }

    /// <summary>Base proc passives and the chance / cooldown their modifiers give them.</summary>
    private IEnumerable<(JsonElement Base, long Group, long Chance, double[] Description)> Procs(IReadOnlyList<Passive> passives)
    {
        var modifiers = passives.SelectMany(p => ChanceModifiers(p.Row).Select(m => (m.Group, m.Value, Description: Description(p.Row)))).ToList();
        foreach (var basis in passives.DistinctBy(p => p.Id))
        {
            long group = GameData.Int(basis.Row, "_iBuffID");
            var own = modifiers.Where(m => m.Group == group).ToList();
            if (own.Count == 0) continue;
            yield return (basis.Row, group, own.Sum(m => m.Value), own.OrderByDescending(m => m.Description.Length).First().Description);
        }
    }

    internal async Task OnDealtAsync(GameSession s, MonsterUnit target, JsonElement skillRow, long damage)
    {
        if (damage <= 0 || s.Role == null || s.Dead) return;
        var passives = Passives(s).ToList();
        if (passives.Count == 0) return;
        foreach (var (basis, group, chance, desc) in Procs(passives))
        {
            var logic = Numbers(basis, "_kLogicData");
            if (group == LifeStealGroup)
            {
                // "has a @{1}% Chance to restore Life equal to @{2}% of the damage dealt, up to @{3}. Cooldown @{4} sec."
                if (desc.Length < 4 || Roll(10000) >= chance || !Ready(s, group, (long)(desc[3] * 1000))) continue;
                long heal = Math.Min((long)desc[2], (long)Math.Floor(damage * desc[1] / 100));
                if (heal <= 0) continue;
                var stats = _character.Stats(s);
                await _character.SetHpSpAsync(s, stats[2000] + heal, null);
            }
            else if (logic.Length == 3 && logic[0] == 0 && logic[2] > 0 && _data.Row("BuffConfig", logic[1]) is not null)
            {
                // "has a @{1}% Chance to inflict a status for @{2} sec. Internal Cooldown: @{3} sec."
                if (target.Dead || target.Hp <= 0) continue;
                long cooldown = desc.Length >= 3 ? (long)(desc[2] * 1000) : 0;
                if (Roll(10000) >= chance || !Ready(s, group, cooldown)) continue;
                await _buffs.ShowOnUnitAsync(target, (int)logic[1], (int)logic[2], s.Role.Id);
            }
        }
        foreach (var passive in passives.Where(p => p.Id == SpAbsorb).DistinctBy(p => p.Id))
        {
            // "has a @{1}% Chance to absorb @{2}% of the damage dealt to the enemy as SP."
            var logic = Numbers(passive.Row, "_kLogicData");
            var desc = Description(passive.Row);
            if (logic.Length < 1 || desc.Length < 2 || Roll(10000) >= logic[0]) continue;
            long sp = (long)Math.Floor(damage * desc[1] / 100);
            if (sp <= 0) continue;
            var stats = _character.Stats(s);
            await _character.SetHpSpAsync(s, null, stats[2001] + sp);
        }
        await SplashAndAutocastAsync(s, target, skillRow, damage, passives);
    }

    /// <summary>"Ignores monster Size. Damage against Small, Medium, and Large monsters +X%" (2050101..4 always,
    /// 2050001..4 with a chance; size 0 monsters are unaffected, inferred from the three named sizes) and the family bonuses
    /// ("Damage against all Orcs +X%", RaceGroups): all add up (basis points).</summary>
    internal long AdjustDamage(GameSession s, MonsterUnit target, JsonElement skillRow, DamageHitResult result, long damage)
    {
        if (damage <= 0) return damage;
        long bonus = 0;
        var state = _runtime.GetOrCreateValue(s);
        long now = MonsterService.NowMs;
        lock (state)
        {
            if (state.NextHitUntil > now) bonus += state.NextHitBp; // armed by the last critical hit: this instance of damage
            state.NextHitUntil = 0;
        }
        bool sized = target.Template.Size is >= 1 and <= 3;
        string? name = null;
        foreach (var passive in Passives(s).DistinctBy(p => (p.Card.Guid, p.Id)))
        {
            long group = GameData.Int(passive.Row, "_iBuffID");
            var logic = Numbers(passive.Row, "_kLogicData");
            if (sized && group == SizeAlways && logic.Length >= 1) bonus += logic[0];
            else if (sized && group == SizeChance && logic.Length >= 2 && Roll(10000) < logic[0]) bonus += logic[1];
            else if (group == NextHitAfterCrit && result == DamageHitResult.Critical && logic.Length >= 2 && Description(passive.Row) is { Length: >= 1 } crit)
                lock (state) { state.NextHitUntil = now + logic[1]; state.NextHitBp = (long)(crit[0] * 100); }
            else if (RaceGroups.TryGetValue(group, out var belongs) && logic.Length >= 1 && belongs(name ??= MonsterName(target.Template.NpcId))) bonus += logic[0];
        }
        return bonus <= 0 ? damage : damage + (long)Math.Floor(damage * bonus / 10000d);
    }

    /// <summary>"Melee Basic Attacks (have a @{1}% Chance to) trigger a Splash effect within a @{2}-meter radius ... Affects up to @{3} Extra
    /// targets, with damage reduced to @{4}% of the original. Internal Cooldown: @{5} seconds." (groups 20508 with a chance, 20509 always) and
    /// the autocast cards: "When making a Physical attack, has a ${1}% Chance to autocast Bash / Jupitel Thunder Lv. @{1}. Internal Cooldown ${2}".</summary>
    private async Task SplashAndAutocastAsync(GameSession s, MonsterUnit target, JsonElement skillRow, long damage, List<Passive> passives)
    {
        bool magic = GameData.Int(skillRow, "_iDmgType") == 2;
        if (magic) return;
        bool melee = GameData.Int(skillRow, "_iIsNormalAtk") == 1 && GameData.Int(skillRow, "_iDistanceType") != 2;
        foreach (var passive in passives.DistinctBy(p => (p.Card.Guid, p.Id)))
        {
            long group = GameData.Int(passive.Row, "_iBuffID");
            var desc = Description(passive.Row);
            if (melee && group == SplashChance && desc.Length >= 5 && Numbers(passive.Row, "_kLogicData") is { Length: >= 1 } logic)
            {
                if (Roll(10000) < logic[0] && Ready(s, group, (long)(desc[4] * 1000))) await Splash(s, target, desc[1], (int)desc[2], desc[3], damage);
            }
            else if (melee && group == SplashAlways && desc.Length >= 4)
            {
                if (Ready(s, group, (long)(desc[3] * 1000))) await Splash(s, target, desc[0], (int)desc[1], desc[2], damage);
            }
        }
        // Autocast: the modifier row (…01) of Bash / Jupitel Thunder names the base group in _kBuffAttributeList and the skill in its description link.
        foreach (var passive in passives.DistinctBy(p => (p.Card.Guid, p.Id)))
        {
            long chance = ChanceModifiers(passive.Row).Where(m => AutocastGroups.Contains((int)m.Group)).Sum(m => m.Value);
            int skillRow2 = AutocastSkill(passive.Row);
            if (chance <= 0 || skillRow2 <= 0 || Description(passive.Row) is not { Length: >= 2 } desc) continue;
            long family = ChanceModifiers(passive.Row).First(m => AutocastGroups.Contains((int)m.Group)).Group;
            if (Roll(10000) >= chance || !Ready(s, family, (long)(desc[1] * 1000)) || target.Dead || target.Hp <= 0) continue;
            await combat.AutocastOnMonsterAsync(s, target, skillRow2);
        }
    }

    private async Task Splash(GameSession s, MonsterUnit target, double radius, int count, double percent, long damage)
    {
        long amount = (long)Math.Floor(damage * percent / 100);
        if (amount <= 0 || count <= 0) return;
        foreach (var other in combat.MonstersAround(s, target, (float)radius, count))
            await combat.HitMonsterAsync(s, other, amount, 0);
    }

    /// <summary>The skill row an autocast card names: the digits of its description's skill link ("skill/2002801") plus the level
    /// (last numeric description parameter) minus one, since the link is the level-1 row.</summary>
    private static int AutocastSkill(JsonElement row)
    {
        if (!row.TryGetProperty("_iDescription", out var list) || list.ValueKind != JsonValueKind.Array) return 0;
        int id = 0, level = 1;
        foreach (var part in list.EnumerateArray().Skip(1))
        {
            string text = part.ValueKind == JsonValueKind.String ? part.GetString() ?? "" : part.ToString();
            int at = text.IndexOf("skill/", StringComparison.Ordinal);
            if (at >= 0 && int.TryParse(new string(text[(at + 6)..].TakeWhile(char.IsDigit).ToArray()), out int linked)) id = linked;
            else if (!text.StartsWith('<') && int.TryParse(text, out int lv)) level = lv;
        }
        return id > 0 ? id + Math.Max(0, level - 1) : 0;
    }

    /// <summary>"When applying a healing skill effect, has a ${1}% Chance to trigger Divine Light Lv@{1}. Internal Cooldown: ${2} seconds."
    /// (group 20531, _kLogicData [chance bp, skill row]): Divine Light on the nearest monster within 8 m of the healer.</summary>
    private async Task OnHealedAsync(GameSession s, long healed)
    {
        if (s.Role == null || s.Dead) return;
        foreach (var passive in Passives(s).Where(p => GameData.Int(p.Row, "_iBuffID") == DivineLight).DistinctBy(p => (p.Card.Guid, p.Id)).ToList())
        {
            var logic = Numbers(passive.Row, "_kLogicData");
            var desc = Description(passive.Row);
            if (logic.Length < 2 || desc.Length < 2 || Roll(10000) >= logic[0] || !Ready(s, DivineLight, (long)(desc[1] * 1000))) continue;
            if (combat.NearestMonster(s, 8) is { } near) await combat.AutocastOnMonsterAsync(s, near, (int)logic[1]);
            return;
        }
    }

    /// <summary>"When hit by a player's ST magic/Physical skill, reflects @{1}% of the damage taken, up to @{2}% of the attacker's Max Life. (For
    /// every @{3} VIT, reflects an Extra @{4}% ..., up to @{5}%.) Internal Cooldown: seconds." (groups 20512/20513 magic, 20516/20517 physical;
    /// single-target skills only). _kDescData is [pct, cap%, cd] or [pct, cap%, vit step, extra pct, extra cap%, cd].</summary>
    private async Task OnPvpHitTakenAsync(GameSession attacker, GameSession target, JsonElement row, long damage)
    {
        if (damage <= 0 || target.Role == null || GameData.Int(row, "_iRangeType") == 2) return;
        bool magic = GameData.Int(row, "_iDmgType") == 2;
        foreach (var passive in Passives(target).DistinctBy(p => (p.Card.Guid, p.Id)).ToList())
        {
            long group = GameData.Int(passive.Row, "_iBuffID");
            bool plain = group == (magic ? ReflectMagic : ReflectPhysical), withVit = group == (magic ? ReflectMagicVit : ReflectPhysicalVit);
            var desc = Description(passive.Row);
            if (!(plain && desc.Length >= 3 || withVit && desc.Length >= 6)) continue;
            double percent = desc[0], cap = desc[1];
            long cooldown = (long)((plain ? desc[2] : desc[5]) * 1000);
            if (withVit && desc[2] > 0)
            {
                long vit = _character.Stats(target)[3]; // VIT
                percent = Math.Min(desc[4], percent + Math.Floor(vit / desc[2]) * desc[3]); // the extra reflect share, up to desc[4]%
            }
            if (!Ready(target, group, cooldown)) continue;
            long maxHp = _character.Stats(attacker)[101];
            long amount = Math.Min((long)Math.Floor(damage * percent / 100), (long)Math.Floor(maxHp * cap / 100));
            await combat.ReflectToPlayerAsync(attacker, target, amount);
            return;
        }
    }

    /// <summary>Immune to Slow / Immune to Stun / Endure (immune to knockback): the card refuses those buffs from other sources.</summary>
    private bool RefusesBuff(GameSession s, int group)
    {
        bool stun = Named("Stun", group), slow = Named("Slow", group), knock = Named("Knockback", group);
        if (!stun && !slow && !knock) return false;
        foreach (var passive in Passives(s))
        {
            long g = GameData.Int(passive.Row, "_iBuffID");
            if (stun && g == ImmuneStun || slow && g == ImmuneSlow || knock && g == Endure) return true;
        }
        return false;
    }

    /// <summary>"Basic Attack has a @{1}% Chance to trigger Double Attack, dealing Extra Neutral melee Physical damage equal to @{2}%.
    /// Internal Cooldown: @{3} sec." (group 20210; the chance comes from the card's own modifier row, the other two numbers are read from
    /// its description). The extra hit is that share of the hit just dealt. (inferred)</summary>
    internal long ExtraHit(GameSession s, MonsterUnit target, JsonElement skillRow, long damage)
    {
        if (damage <= 0 || s.Role == null || GameData.Int(skillRow, "_iIsNormalAtk") != 1 || GameData.Int(skillRow, "_iDmgType") == 2
            || GameData.Int(skillRow, "_iDistanceType") == 2) return 0;
        foreach (var passive in Passives(s).DistinctBy(p => (p.Card.Guid, p.Id)))
        {
            long chance = ChanceModifiers(passive.Row).Where(m => m.Group == DoubleAttack).Sum(m => m.Value);
            if (chance <= 0 || DescriptionNumbers(passive.Row) is not { Length: >= 3 } text) continue;
            if (Roll(10000) >= chance || !Ready(s, DoubleAttack, (long)(text[2] * 1000))) continue;
            return (long)Math.Floor(damage * text[1] / 100);
        }
        return 0;
    }

    /// <summary>_iDescription [text id, p1, p2...] as numbers (p1..).</summary>
    private static double[] DescriptionNumbers(JsonElement row) => row.TryGetProperty("_iDescription", out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Skip(1).Select(v => GameData.Number(v) ?? 0).ToArray() : [];

    /// <summary>"After using a skill, has a @{1}% Chance to reset that skill's Cooldown. Its Cooldown cannot be reset again for a period of
    /// time: @{2} times that skill's Cooldown. (Does not reset the global Cooldown.)" (group 20529, _kLogicData [chance bp, ...])</summary>
    internal async Task OnCooldownStartedAsync(GameSession s, JsonElement row)
    {
        if (s.Role == null || s.Dead || GameData.Int(row, "_iIsNormalAtk") == 1) return;
        long cooldown = GameData.Int(row, "_iCD");
        if (cooldown <= 0) return;
        int group = (int)GameData.Int(row, "_iSkillID");
        foreach (var passive in Passives(s).Where(p => GameData.Int(p.Row, "_iBuffID") == CooldownReset).DistinctBy(p => (p.Card.Guid, p.Id)).ToList())
        {
            var logic = Numbers(passive.Row, "_kLogicData");
            var desc = Description(passive.Row);
            if (logic.Length < 1 || desc.Length < 2 || Roll(10000) >= logic[0]) continue;
            if (!Ready(s, 1_000_000_000L + group, (long)(desc[1] * cooldown))) continue;
            await CombatService.ResetCooldownAsync(s, group);
            return;
        }
    }

    /// <summary>"When hit by a magic attack, has a @{1}% Chance to nullify its damage. Internal Cooldown: @{2} seconds." (group 20510)</summary>
    internal bool NegateMagicHit(GameSession s, bool magic)
    {
        if (!magic || s.Role == null) return false;
        foreach (var passive in Passives(s).Where(p => GameData.Int(p.Row, "_iBuffID") == MagicNullify).DistinctBy(p => (p.Card.Guid, p.Id)))
        {
            var logic = Numbers(passive.Row, "_kLogicData");
            var desc = Description(passive.Row);
            if (logic.Length < 1 || desc.Length < 2 || Roll(10000) >= logic[0] || !Ready(s, MagicNullify, (long)(desc[1] * 1000))) continue;
            return true;
        }
        return false;
    }

    /// <summary>Ladybug: "Damage taken while Silenced -@{1}%" (group 20260, _kLogicData [basis points]).</summary>
    private Task<long> LessWhileSilencedAsync(MonsterUnit monster, GameSession player, DamageHitResult result, long damage)
    {
        long now = MonsterService.NowMs;
        if (!player.Buffs.Any(b => b.ExpireMs > now && b.ConfigId / 100 == SilenceBuffGroup)) return Task.FromResult(damage);
        long cut = Passives(player).Where(p => GameData.Int(p.Row, "_iBuffID") == Ladybug).Sum(p => Numbers(p.Row, "_kLogicData").FirstOrDefault());
        return Task.FromResult(cut <= 0 ? damage : Math.Max(1, damage - damage * Math.Min(cut, 10000) / 10000));
    }

    /// <summary>Angeling: "When taking damage, has a @{1}% Chance to change your armor's element to Holy for @{2} seconds. Internal Cooldown: @{3} seconds."
    /// (group 20514, _kLogicData [chance bp, the Holy-armor buff, its ms]).</summary>
    private async Task OnTakenAsync(MonsterUnit monster, GameSession player, long damage)
    {
        if (damage <= 0 || player.Role == null) return;
        foreach (var passive in Passives(player).Where(p => GameData.Int(p.Row, "_iBuffID") == Angeling).DistinctBy(p => (p.Card.Guid, p.Id)).ToList())
        {
            var logic = Numbers(passive.Row, "_kLogicData");
            var desc = Description(passive.Row);
            if (logic.Length < 3 || desc.Length < 3 || Roll(10000) >= logic[0] || !Ready(player, Angeling, (long)(desc[2] * 1000))) continue;
            await _buffs.AddAsync(player, (int)logic[1], (int)logic[2], player.Role.Id);
            return;
        }
    }

    /// <summary>"Changes armor property to Earth/Water/Wind/Poison/Fire/Ghost/Holy" (groups 20090..20140, 20515): the element monsters
    /// hit this role's armor with; 0 when no card changes it.</summary>
    internal int ArmorElement(GameSession s)
    {
        long now = MonsterService.NowMs;
        if (s.Buffs.Any(b => b.ExpireMs > now && b.ConfigId == AngelingHolyArmorBuff)) return 7; // Angeling: the armor is Holy for a while
        foreach (var passive in Passives(s))
            if (ArmorElements.TryGetValue(GameData.Int(passive.Row, "_iBuffID"), out int element)) return element;
        return 0;
    }
}
