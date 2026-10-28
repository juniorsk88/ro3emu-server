using System.Collections.Concurrent;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What the server owes the client to draw one skill. The client plays the cast timeline (skill_N.json
/// m_kShows) by itself; the server only picks the hit effect (SkillHit.ShowPoint) and, for projectile skills
/// that the timeline does not spawn, sends the projectile (SkillFixTimeBulletNotify, 31120).
/// </summary>
/// <param name="HitEffect">Effect id with m_kActionDamge (DamageType 3): hit gfx, flinch animation, hit sound.</param>
/// <param name="BulletEffect">Effect id with m_kBulletData (DamageType 2), or 0 when the client needs no server projectile.</param>
/// <param name="Bullet">The m_kBulletData of <paramref name="BulletEffect"/>; flight time and track come from it.</param>
/// <param name="HitHasGfx">The skill's own hit effect draws a gfx (Blessing's angel glow, Increase AGI's swirl); the generic hit never counts.</param>
internal sealed record SkillVisual(int HitEffect, int BulletEffect, JsonElement? Bullet, bool HitHasGfx = false)
{
    public const int GenericHitEffect = 1000002;
    public static readonly SkillVisual Generic = new(GenericHitEffect, 0, null);

    /// <summary>
    /// Client facts (re-derived from the decompiled Lua, see admin/CONHECIMENTO-SKILLS.md):
    /// effect ids live in [scriptShow*100, scriptShow*100+99]; Lua_Config_EffectData.m_kDamage.m_kDamageType
    /// says what each one is (2 projectile, 3 hit reaction). LC_DamageHelper.CalcDamageHitPoint draws a hit
    /// only when ShowPoint is a type-3 effect, so the first m_kDamage of the range is wrong for projectile skills.
    /// </summary>
    internal static SkillVisual Build(GameData data, JsonElement row)
    {
        long scriptShow = GameData.Int(row, "_iScriptShow");
        var effects = data.SkillTemplateTable("Lua_Config_EffectData");
        var actions = new List<int>();
        var bullets = new List<(int Id, JsonElement Config)>();
        for (long id = scriptShow * 100; id < scriptShow * 100 + 100; id++)
        {
            if (!effects.TryGetValue(id.ToString(), out var effect)
                || !effect.TryGetProperty("m_kDamage", out var damage) || damage.ValueKind != JsonValueKind.Object) continue;
            int type = (int)(damage.TryGetProperty("m_kDamageType", out var t) ? GameData.Number(t) ?? 0 : 0);
            if (type == 3 && damage.TryGetProperty("m_kActionDamge", out _)) actions.Add((int)id);
            else if (type == 2 && damage.TryGetProperty("m_kBulletData", out var bullet) && bullet.ValueKind == JsonValueKind.Object)
                bullets.Add(((int)id, bullet));
        }

        // SkillConfig._kLogicData is the original script's own effect list: a type-2 id there is its projectile and a
        // type-3 id its hit (Soul Destroyer [1531002, 1531003]); about 190 skills carry one, some outside the range.
        int scriptBullet = 0, scriptHit = 0;
        if (row.TryGetProperty("_kLogicData", out var logic) && logic.ValueKind == JsonValueKind.Array)
            foreach (var item in logic.EnumerateArray())
            {
                if (GameData.Number(item) is not { } number || number < 1_000_000 || !effects.TryGetValue(((long)number).ToString(), out var named)
                    || !named.TryGetProperty("m_kDamage", out var namedDamage) || namedDamage.ValueKind != JsonValueKind.Object) continue;
                int type = (int)(namedDamage.TryGetProperty("m_kDamageType", out var nt) ? GameData.Number(nt) ?? 0 : 0);
                if (type == 2 && scriptBullet == 0 && namedDamage.TryGetProperty("m_kBulletData", out var nb) && nb.ValueKind == JsonValueKind.Object)
                { scriptBullet = (int)number; if (!bullets.Any(b => b.Id == scriptBullet)) bullets.Add((scriptBullet, nb)); }
                else if (type == 3 && scriptHit == 0 && namedDamage.TryGetProperty("m_kActionDamge", out _))
                { scriptHit = (int)number; if (!actions.Contains(scriptHit)) actions.Add(scriptHit); }
            }

        // The timeline's own m_kClientPoints make the client launch its projectiles (LC_Skill_RunTimeContext.PlayClientEffect);
        // a server projectile on top would draw them twice.
        bool clientBullets = data.SkillTemplate(scriptShow) is { } template
            && template.TryGetProperty("m_kClientPoints", out var points) && points.ValueKind == JsonValueKind.Array && points.GetArrayLength() > 0;

        // Variants share a range (Fire Ball has an empty-prefab projectile and a real one); an empty
        // m_kBulletName draws nothing, so prefer the first projectile that has a prefab.
        (int Id, JsonElement Config)? chosen = null;
        if (!clientBullets && bullets.Count > 0)
            chosen = scriptBullet != 0 ? bullets.First(b => b.Id == scriptBullet)
                : bullets.FirstOrDefault(b => !string.IsNullOrEmpty(BulletName(b.Config)), bullets[0]);

        // The hit that belongs to the projectile is the next type-3 effect after it (1200503 -> 1200504); a skill
        // with only one such effect, or a projectile that comes last (Fire Bolt 1200411), uses the first one.
        // A range with no type-3 effect (only projectile / anim-group / pet-show data) gets the generic hit, because
        // CalcDamageHitPoint ignores any other kind.
        int hit = scriptHit != 0 ? scriptHit : actions.Count == 0 ? GenericHitEffect
            : chosen is { } c ? actions.FirstOrDefault(a => a > c.Id, actions[0]) : actions[0];
        bool gfx = hit != GenericHitEffect && effects.TryGetValue(hit.ToString(), out var hitEffect)
            && hitEffect.TryGetProperty("m_kDamage", out var hitDamage) && hitDamage.ValueKind == JsonValueKind.Object
            && hitDamage.TryGetProperty("m_kActionDamge", out var action) && action.ValueKind == JsonValueKind.Object
            && action.TryGetProperty("m_kGfxList", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0;
        return new SkillVisual(hit, chosen?.Id ?? 0, chosen?.Config, gfx);
    }

    private static string BulletName(JsonElement bullet) =>
        bullet.TryGetProperty("m_kBulletName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "";
}

public sealed partial class CombatService
{
    private const int MinFlightMs = 120; // LC_Bullet_Helper clamps the client flight time to 0.12 s
    private const int MaxFlightMs = 2500;

    private readonly ConcurrentDictionary<long, SkillVisual> visuals = new();

    /// <summary>The effect plan of one SkillConfig row (cached by row id; rows of a skill share a script show).</summary>
    internal SkillVisual VisualOf(JsonElement row) =>
        GameData.Int(row, "_iScriptShow") <= 0 && !row.TryGetProperty("_kLogicData", out _) ? SkillVisual.Generic
            : visuals.GetOrAdd(GameData.Int(row, "_iID"), _ => SkillVisual.Build(_data, row));

    /// <summary>
    /// SkillConfig._kShowDmg[0] is how many hits the client draws (Fire Bolt level 5 = [5]). The client divides the
    /// damage by SkillHit.split and replays the hit effect at m_fDisplayInterval (LC_DamageHelper._ActionJumpDamage),
    /// and spawns that many projectiles from one SkillFixTimeBullet spaced by m_fBulletHatchInterval.
    /// </summary>
    internal static int HitSplit(JsonElement row)
    {
        if (!row.TryGetProperty("_kShowDmg", out var show) || show.ValueKind != JsonValueKind.Array || show.GetArrayLength() == 0) return 1;
        return (int)Math.Clamp(GameData.Number(show[0]) ?? 1, 1, 20);
    }

    /// <summary>Projectile flight in ms: LC_Bullet_Helper uses m_fBulletLifeTime for time-controlled bullets, distance / m_fBulletSpeed otherwise.</summary>
    internal static int FlightMs(JsonElement bullet, float distance)
    {
        double control = bullet.TryGetProperty("m_iBulletControlType", out var c) ? GameData.Number(c) ?? 0 : 0;
        double seconds;
        if (control == 0)
            seconds = (bullet.TryGetProperty("m_fBulletLifeTime", out var life) ? GameData.Number(life) ?? 0 : 0) / 1000;
        else
        {
            double speed = bullet.TryGetProperty("m_fBulletSpeed", out var v) ? GameData.Number(v) ?? 0 : 0;
            seconds = speed > 0 ? distance / speed : 0;
        }
        return (int)Math.Clamp(seconds * 1000, MinFlightMs, MaxFlightMs);
    }

    /// <summary>
    /// Optional tuning, read on every cast (no restart): &lt;data&gt;/skills/bullet-timing.json
    /// { "sendBullet": true, "extraDelayMs": 0 }. Defaults keep the client's own timing.
    /// </summary>
    private static (bool Send, int ExtraDelay) BulletSettings()
    {
        try
        {
            string dir = Environment.GetEnvironmentVariable("Ro3Emu__DataDir") ?? "data";
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "skills", "bullet-timing.json")));
            var root = doc.RootElement;
            bool send = !(root.TryGetProperty("sendBullet", out var s) && s.ValueKind == JsonValueKind.False);
            int extra = root.TryGetProperty("extraDelayMs", out var e) && e.TryGetInt32(out int n) ? Math.Clamp(n, 0, 3000) : 0;
            return (send, extra);
        }
        catch
        {
            return (true, 0);
        }
    }

    /// <summary>
    /// Sends the projectile for a cast and returns the ms the damage should wait so the numbers land with it
    /// (0 when nothing was sent). One notify draws <c>split</c> projectiles; the client spaces them.
    /// A Sag track (Fire Bolt) is dropped onto a point: the client takes SourcePosition only when SourceId is 0.
    /// </summary>
    private async Task<int> SendProjectileAsync(GameSession s, ActiveSkillCast action, SkillVisual visual)
    {
        if (visual.Bullet is not { } bullet || visual.BulletEffect == 0) return 0;
        var settings = BulletSettings();
        if (!settings.Send) return 0;
        var (targetId, to) = ProjectileTarget(s, action);
        if (to == null) return 0;
        var point = new Vec3 { X = to.X, Y = to.Y, Z = to.Z };
        double track = bullet.TryGetProperty("m_eBulletType", out var tr) ? GameData.Number(tr) ?? 0 : 0;
        bool dropped = track == 2;
        int flight = FlightMs(bullet, Distance(s.Position, to));
        int split = HitSplit(action.Row);
        try
        {
            await SendSkillBulletAsync(s, new SkillFixTimeBulletNotify
            {
                SourceId = dropped ? 0 : s.Role!.Id,
                TargetId = targetId,
                EffectId = visual.BulletEffect,
                FixedTime = flight,
                Split = split,
                SkillConfigId = action.Request.SkillId,
                MasterId = s.Role!.Id,
                SourcePosition = dropped ? point : new Vec3 { X = s.Position.X, Y = s.Position.Y, Z = s.Position.Z },
                TargetPosition = point,
                StartTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Projectile for skill {Skill} failed", action.Request.SkillId);
            return 0;
        }
        return flight + settings.ExtraDelay;
    }

    /// <summary>The unit the projectile follows (monster, ally, self) or, for ground casts, the cast point.</summary>
    private (long Id, Vec3? Pos) ProjectileTarget(GameSession s, ActiveSkillCast action)
    {
        long id = action.Request.TargetId;
        if (id != 0)
        {
            if (FindMonster(s.WorldScene, id) is { } monster && monster.Hp > 0) return (monster.Uid, monster.Pos);
            if (id == s.Role!.Id) return (id, s.Position);
            if (FindPlayer(s, id) is { } player) return (id, player.Position);
        }
        return action.Request.TargetPos is { } ground ? (0, ground) : (0, null);
    }

    private int ChantFor(GameSession s, JsonElement row) => ChantOf(row, options.Value.ChantSpeedReduction ? _character.Stats(s)[ChantSpeedAttr] : 0);

    /// <summary>AttributeConfig 116 ("cspd"). AttributeConversionConfig gives DEX +2 per point, equipment and buffs add to it.</summary>
    private const int ChantSpeedAttr = 116;

    /// <summary>Cast time in ms with no ChantSpeed: <c>_iChantFix + _iChantVar</c> as configured.</summary>
    internal static int ChantOf(JsonElement row) => ChantOf(row, 0);

    /// <summary>
    /// Cast time in ms as the client's skill tooltip computes it (UI_Com_Skill_PopupItemWidgetCtrl): only the variable
    /// part shrinks, by <c>max(0, 1 - sqrt(ChantSpeed / 600))</c>; the fixed part is untouched. With ChantSpeed 0 the
    /// result is the configured value, which is what the CBT video shows for Fire Bolt Lv.10 ("CT 1.0s (0.0s+1.0s)").
    /// </summary>
    internal static int ChantOf(JsonElement row, long chantSpeed)
    {
        double fix = Math.Max(0, GameData.Int(row, "_iChantFix"));
        double variable = Math.Max(0, GameData.Int(row, "_iChantVar"));
        double factor = chantSpeed > 0 ? Math.Max(0, 1 - Math.Sqrt(chantSpeed / 600.0)) : 1;
        return (int)Math.Round(fix + variable * factor);
    }
}
