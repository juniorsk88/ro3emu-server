using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Skills whose buff answers the hits a monster lands on the player: Shield Reflect, Reflect Damage, Parry, Recoil and the Auto Counter
/// that ends its parry. Each buff names its triggered skill in BuffConfig._kLogicData (a SkillConfig group, level = the buff row's last two
/// digits); the casting skill's _kDescData holds the numbers. Not done: Death Bound (it answers enemy players only), Devotion and King's Grace
/// (they redirect damage between players). The monster's normal attack stands for every hit it lands (melee Physical when its normal skill is).
/// </summary>
public sealed class ReactiveSkills
{
    private const string ShieldReflect = "SReflect", ReflectDamage = "RDamage", Parry = "Counter_Attack2", Recoil = "ShrinkReflect", AutoCounter = "Counter_Attack";
    private const int AutoGuardGroup = 11403, AutoCounterGroup = 11105;

    private readonly GameData _data;
    private readonly CombatService _combat;
    private readonly GameLoop _loop;
    private readonly ConditionalWeakTable<GameSession, State> _states = [];
    private Dictionary<long, long>? _castGroupOfBuff;

    private sealed class State
    {
        public readonly Dictionary<long, long> Cooldowns = [];
        public bool HitWhileParrying;
    }

    public ReactiveSkills(GameData data, CombatService combat, GameLoop loop)
    {
        _data = data;
        _combat = combat;
        _loop = loop;
        combat.TakenAsync.Add(OnTakenAsync);
        combat.CastLanded.Add(OnCastAsync);
    }

    private bool Ready(GameSession s, long key, double seconds)
    {
        var state = _states.GetOrCreateValue(s);
        long now = MonsterService.NowMs;
        lock (state)
        {
            if (state.Cooldowns.GetValueOrDefault(key) > now) return false;
            state.Cooldowns[key] = now + (long)(seconds * 1000);
            return true;
        }
    }

    /// <summary>The skill group whose _kBuff1 first lists a buff row, for the numbers in its description.</summary>
    private long? CastGroupOf(int configId)
    {
        _castGroupOfBuff ??= BuildCastGroups();
        return _castGroupOfBuff.TryGetValue(configId / 100, out long group) ? group : null;
    }

    private Dictionary<long, long> BuildCastGroups()
    {
        var map = new Dictionary<long, long>();
        foreach (var skill in SkillConfig.All(_data))
            if (BuffTuple(skill.Row) is { } buff) map.TryAdd(buff / 100, skill.SkillId);
        return map;
    }

    private static double Part(string[] desc, int index) =>
        index < desc.Length && double.TryParse(desc[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    /// <summary>The numbers of the casting skill at the level the buff row carries.</summary>
    private string[] CastNumbers(ActiveBuff buff) =>
        CastGroupOf(buff.ConfigId) is { } group && SkillConfig.Get(_data, group * 100 + buff.ConfigId % 100) is { } row ? row.DescData : [];

    private async Task OnTakenAsync(MonsterUnit monster, GameSession player, long damage)
    {
        if (player.Role == null || player.Dead || damage <= 0 || monster.Dead || monster.Hp <= 0) return;
        var normal = _data.Row("SkillConfig", monster.Template.NormalSkill);
        bool meleePhysical = normal is { } n && GameData.Int(n, "_iDmgType") == 1 && GameData.Int(n, "_iDistanceType") != 2;
        long now = MonsterService.NowMs;
        var live = player.Buffs.Where(b => b.ExpireMs > now).ToArray();
        if (meleePhysical && live.Any(b => b.ConfigId / 100 == AutoCounterGroup)) _states.GetOrCreateValue(player).HitWhileParrying = true;
        foreach (var buff in live)
        {
            if (BuffConfig.Get(_data, buff.ConfigId) is not { LogicData: [var sub, ..] }) continue;
            if (SkillConfig.Get(_data, sub * 100 + buff.ConfigId % 100) is not { } subRow) continue;
            var cast = CastNumbers(buff);
            switch (subRow.ScriptLogic)
            {
                case ShieldReflect when meleePhysical && MonsterService.Distance(monster, player) <= Math.Max(1, Part(cast, 4)) && Ready(player, sub, 1):
                    await _combat.AutocastOnMonsterAsync(player, monster, (int)subRow.Id);
                    break;
                case ReflectDamage when Ready(player, sub, Part(cast, 3) is var gap and > 0 ? gap : 0.5):
                    foreach (var near in _combat.MonstersNear(player, (float)Math.Max(1, Part(cast, 6)), (int)Math.Max(1, Part(cast, 2))))
                        await _combat.AutocastOnMonsterAsync(player, near, (int)subRow.Id);
                    break;
                case Parry when meleePhysical && Ready(player, sub, Part(cast, 2)):
                    await _combat.AutocastOnMonsterAsync(player, monster, (int)subRow.Id);
                    break;
                case Recoil when live.Any(b => b.ConfigId / 100 == AutoGuardGroup) && MonsterService.Distance(monster, player) <= Math.Max(1, Part(cast, 4))
                    && Ready(player, sub, Part(cast, 3)) && Random.Shared.NextDouble() * 100 < Part(cast, 1):
                    await _combat.StunMonsterAsync(monster, subRow.Row, player.Role.Id);
                    break;
            }
        }
    }

    /// <summary>Auto Counter: "enter a Parry state for ${1} sec ... When Parry ends, perform an Auto Counter against up to ${5} enemies within a ${6}m * ${7}m
    /// area ahead ... If you take melee Physical damage while parrying, Auto Counter damage increases by ${4}%." The area ahead is taken as a circle
    /// of the longer side around the player. (inferred)</summary>
    private Task OnCastAsync(GameSession player, JsonElement row)
    {
        var skill = new SkillConfig(row);
        if (skill.ScriptLogic != AutoCounter || BuffTuple(row) is not { } buff
            || BuffConfig.Get(_data, buff) is not { LogicData: [var sub, ..] } || SkillConfig.Get(_data, sub * 100 + buff % 100) is not { } subRow)
            return Task.CompletedTask;
        var desc = skill.DescData;
        var state = _states.GetOrCreateValue(player);
        lock (state) state.HitWhileParrying = false;
        var role = player.Role;
        _loop.ScheduleRuntime(TimeSpan.FromMilliseconds(Math.Max(1, Part(desc, 0)) * 1000), async () =>
        {
            if (!ReferenceEquals(player.Role, role) || player.Dead) return;
            bool hit;
            lock (state) { hit = state.HitWhileParrying; state.HitWhileParrying = false; }
            int percent = 100 + (hit ? (int)Part(desc, 3) : 0);
            foreach (var near in _combat.MonstersNear(player, (float)Math.Max(Part(desc, 5), Part(desc, 6)), (int)Math.Max(1, Part(desc, 4))))
                await _combat.AutocastOnMonsterAsync(player, near, (int)subRow.Id, percent);
        });
        return Task.CompletedTask;
    }

    private static long? BuffTuple(JsonElement row) =>
        row.TryGetProperty("_kBuff1", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
        && list[0].ValueKind == JsonValueKind.Array && list[0].GetArrayLength() > 0 && GameData.Number(list[0][0]) is double id ? (long)id : null;
}
