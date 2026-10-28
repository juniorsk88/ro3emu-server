using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Which effect ids the server hands the client for a skill. The ranges below are trimmed copies of
/// Lua_Config_EffectData rows from the shipped client; the rules come from the decompiled client Lua.
/// </summary>
public sealed class SkillVisualTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-skill-visual-" + Guid.NewGuid().ToString("N"));

    private static string Hit(string gfx) =>
        "{\"m_kDamage\":{\"m_kActionDamge\":{\"m_kGfxList\":[{\"m_kGfxName\":\"" + gfx + "\"}]},\"m_kDamageType\":3}}";

    private static string Bullet(string name, int track, int control, string extra) =>
        "{\"m_kDamage\":{\"m_kBulletData\":{\"m_eBulletType\":" + track + ",\"m_iBulletControlType\":" + control
        + ",\"m_kBulletName\":\"" + name + "\"" + extra + "},\"m_kDamageType\":2}}";

    private const string Point = """{"m_kPoint":{"m_kAni":"Chant"}}""";

    private GameData Data(string timelineFile = "", string timeline = "")
    {
        Directory.CreateDirectory(Path.Combine(_dir, "skills"));
        File.WriteAllText(Path.Combine(_dir, "skills", "Lua_Config_EffectData.json"), "{"
            + $"\"1200401\":{Point},\"1200404\":{Hit("hit_bolt")},\"1200411\":{Bullet("fx_bullet_magicfirearrow_new.prefab", 2, 0, ",\"m_fBulletLifeTime\":400")},"
            + $"\"1200503\":{Bullet("", 1, 0, ",\"m_fBulletLifeTime\":300")},\"1200504\":{Hit("hit_lightning")},"
            + $"\"1201208\":{Bullet("", 0, 1, ",\"m_fBulletSpeed\":24")},\"1201209\":{Hit("fireball_2")},"
            + $"\"1201210\":{Bullet("fx_skill_fireball_1.prefab", 0, 1, ",\"m_fBulletSpeed\":24")},\"1201211\":{Hit("warlockhit3")},"
            + $"\"1300001\":{Bullet("arrow.prefab", 0, 0, ",\"m_fBulletLifeTime\":200")},\"1300002\":{Hit("arrow_hit")},"
            + $"\"1400001\":{Bullet("only_bullet.prefab", 0, 0, "")}"
            + "}");
        if (timelineFile != "") File.WriteAllText(Path.Combine(_dir, "skills", timelineFile), timeline);
        return new GameData(Options.Create(new ServerOptions { DataDir = _dir, GameDataDir = _dir }), NullLogger<GameData>.Instance);
    }

    private static JsonElement Row(long scriptShow, string logicData = "{}")
    {
        using var doc = JsonDocument.Parse("{\"_iID\":" + scriptShow + ",\"_iScriptShow\":" + scriptShow + ",\"_kLogicData\":" + logicData + "}");
        return doc.RootElement.Clone();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void ProjectileComesAfterTheHit_FirstHitIsUsed()
    {
        var visual = SkillVisual.Build(Data(), Row(12004));
        Assert.Equal(1200404, visual.HitEffect);
        Assert.Equal(1200411, visual.BulletEffect);
    }

    [Fact]
    public void ProjectileBeforeTheHit_PairsWithTheNextHit_NotTheBullet()
    {
        // The old "first m_kDamage" rule returned the projectile 1200503, which the client cannot draw a hit with.
        var visual = SkillVisual.Build(Data(), Row(12005));
        Assert.Equal(1200504, visual.HitEffect);
        Assert.Equal(1200503, visual.BulletEffect);
    }

    [Fact]
    public void EmptyPrefabVariantIsSkipped()
    {
        var visual = SkillVisual.Build(Data(), Row(12012));
        Assert.Equal(1201210, visual.BulletEffect);
        Assert.Equal(1201211, visual.HitEffect);
    }

    [Fact]
    public void ClientLaunchedProjectilesAreNotSentByTheServer()
    {
        var visual = SkillVisual.Build(Data("skill_13000.json", """{"13000":{"m_kClientPoints":[[0.3,1300001,0]]}}"""), Row(13000));
        Assert.Equal(0, visual.BulletEffect);
        Assert.Equal(1300002, visual.HitEffect);
    }

    [Fact]
    public void RangeWithoutAHitEffect_UsesTheGenericHit()
    {
        var visual = SkillVisual.Build(Data(), Row(14000));
        Assert.Equal(SkillVisual.GenericHitEffect, visual.HitEffect);
        Assert.Equal(1400001, visual.BulletEffect);
    }

    [Fact]
    public void LogicDataNamesTheScriptsOwnProjectileAndHit()
    {
        // Soul Destroyer: _kLogicData [1531002, 1531003] lies outside nothing here, but outranks the range heuristics.
        var data = Data();
        File.WriteAllText(Path.Combine(_dir, "skills", "Lua_Config_EffectData.json"), "{"
            + $"\"1531001\":{Bullet("decoy.prefab", 0, 0, ",\"m_fBulletLifeTime\":100")},\"1531002\":{Bullet("soul.prefab", 0, 1, ",\"m_fBulletSpeed\":30")},"
            + $"\"1531003\":{Hit("soul_hit")},\"1531004\":{Hit("decoy_hit")}}}");
        var fresh = new GameData(Options.Create(new ServerOptions { DataDir = _dir, GameDataDir = _dir }), NullLogger<GameData>.Instance);
        var visual = SkillVisual.Build(fresh, Row(15310, "[1531002,1531003]"));
        Assert.Equal(1531002, visual.BulletEffect);
        Assert.Equal(1531003, visual.HitEffect);
    }

    [Fact]
    public void OnlyASkillsOwnHitEffectCountsAsDrawingAGfx()
    {
        // Blessing / Increase AGI: the glow is the type-3 hit effect; the generic hit must not be drawn on allies.
        var data = Data();
        Assert.True(SkillVisual.Build(data, Row(12004)).HitHasGfx);
        Assert.False(SkillVisual.Build(data, Row(14000)).HitHasGfx);
        Assert.False(SkillVisual.Build(data, Row(99999)).HitHasGfx);
    }

    [Fact]
    public void UnknownSkill_IsGeneric()
    {
        var visual = SkillVisual.Build(Data(), Row(99999));
        Assert.Equal(SkillVisual.GenericHitEffect, visual.HitEffect);
        Assert.Equal(0, visual.BulletEffect);
    }

    [Fact]
    public void DamageBonusAttributesScaleTheHitByItsKind()
    {
        long Stat(int id) => id switch { 213 => 1000, 211 => 500, 215 => 200, 225 => 300, 250 => 100, 252 => 400, 209 => 3000, _ => 0 };
        // A magic skill, ranged: 215 + 213 + 252 + 225 = 200 + 1000 + 400 + 300 = 1900 -> x1.19
        Assert.Equal(1190, CombatService.ApplyDamageBonuses(1000, magic: true, ranged: true, normalAttack: false, critical: false, Stat));
        // The normal attack gets no skill bonus: 200 + 1000 + 400 -> x1.16
        Assert.Equal(1160, CombatService.ApplyDamageBonuses(1000, magic: true, ranged: true, normalAttack: true, critical: false, Stat));
        // A melee physical skill uses 211 (+ 227 = 0): 200 + 500 + 300 -> x1.10
        Assert.Equal(1100, CombatService.ApplyDamageBonuses(1000, magic: false, ranged: false, normalAttack: false, critical: false, Stat));
        // A critical hit also scales by (15000 + 3000) / 15000 = 1.2
        Assert.Equal(1320, CombatService.ApplyDamageBonuses(1000, magic: false, ranged: false, normalAttack: false, critical: true, Stat));
        // No attributes: unchanged.
        Assert.Equal(777, CombatService.ApplyDamageBonuses(777, true, true, false, false, _ => 0));
    }

    [Fact]
    public void ElementRaceSizeAndBossAttributesFollowTheMonsterAndTheSkillElement()
    {
        // Fire skill (304) +10%, vs earth monsters (323) +5%, vs race 4 (plant, 404) +20%, vs large (505) +10%, vs boss (601) +15%.
        long Stat(int id) => id switch { 304 => 1000, 323 => 500, 404 => 2000, 505 => 1000, 601 => 1500, 603 => 7000, _ => 0 };
        var plantBoss = new CombatService.HitSide(Element: 3, Race: 4, Size: 3, Boss: true);
        Assert.Equal(1600, CombatService.ApplyDamageBonuses(1000, false, false, true, false, Stat, plantBoss, skillElement: 4));
        // A non-boss uses 603 instead of 601; a neutral skill misses 304.
        var plain = plantBoss with { Boss = false };
        Assert.Equal(2050, CombatService.ApplyDamageBonuses(1000, false, false, true, false, Stat, plain, skillElement: 1));
        // Reductions read the "taken" twins: 312 wind skill, 333 earth monster, 414 plant, 506 large, 602 boss.
        long Cut(int id) => id switch { 312 => 1000, 333 => 500, 414 => 1000, 506 => 500, 602 => 1000, _ => 0 };
        Assert.Equal(600, CombatService.ApplyDamageReductions(1000, false, false, false, Cut, plantBoss, skillElement: 2));
        Assert.Equal(1000, CombatService.ApplyDamageReductions(1000, false, false, false, Cut, null, skillElement: 2));
    }

    [Fact]
    public void AnArrowGivesItsElementOnlyWhenItsTextSaysItChangesTheAttackProperty()
    {
        const string fire = "An arrow with a tip that can be set ablaze.\n<color=#6e8bc8>Equipping Fire Arrow with a bow changes your attack property to Fire.</color>";
        Assert.Equal(4, CombatService.ArrowElement(fire, "4"));
        Assert.Equal(7, CombatService.ArrowElement(fire.Replace("Fire", "Holy"), "7"));
        // Steel Arrow: _kParam1 6, but its text names no property; the common Arrow: 1, neutral.
        Assert.Equal(1, CombatService.ArrowElement("An arrow made of steel with considerable attack power.", "6"));
        Assert.Equal(1, CombatService.ArrowElement("A common Arrow that can be obtained anywhere.", "1"));
        // A parameter that is no element id changes nothing.
        Assert.Equal(1, CombatService.ArrowElement(fire, ""));
        Assert.Equal(1, CombatService.ArrowElement(fire, "11"));
    }

    [Fact]
    public void DamageReductionAttributesCutWhatMonstersDealAndHealingAttributesScaleHeals()
    {
        long Stat(int id) => id switch { 216 => 1000, 212 => 500, 214 => 2000, 232 => 500, 210 => 3000, _ => 0 };
        // Physical: 216 + 232 + 212 = 2000 -> x0.8
        Assert.Equal(800, CombatService.ApplyDamageReductions(1000, magic: false, critical: false, Stat));
        // Magic: 216 + 232 + 214 = 3500 -> x0.65
        Assert.Equal(650, CombatService.ApplyDamageReductions(1000, magic: true, critical: false, Stat));
        // A critical hit is cut again by (15000 - 3000) / 15000 = 0.8
        Assert.Equal(640, CombatService.ApplyDamageReductions(1000, magic: false, critical: true, Stat));
        // Never more than 90% off, and never below 1
        Assert.Equal(100, CombatService.ApplyDamageReductions(1000, false, false, id => id == 216 ? 50000 : 0));
        Assert.Equal(1, CombatService.ApplyDamageReductions(1, false, false, id => id == 216 ? 50000 : 0));
        Assert.Equal(1000, CombatService.ApplyDamageReductions(1000, false, false, _ => 0));
        Assert.Equal(1150, CombatService.ScaleHealing(1000, 1000, 500));
    }

    [Fact]
    public void TheSecondBucketCastAttributesAndHealCritsFollowTheClientFormulas()
    {
        // The "12xx" bucket multiplies the 21x one: (1 + 0.10) * (1 + 0.50) = 1.65
        long Pool(int id) => id switch { 215 => 1000, 1215 => 5000, _ => 0 };
        Assert.Equal(1650, CombatService.ApplyDamageBonuses(1000, magic: true, ranged: false, normalAttack: true, critical: false, Pool));
        // Reductions: melee physical 228 and the 12xx bucket (1 - 0.2) * (1 - 0.25)
        long Cut(int id) => id switch { 212 => 1000, 228 => 1000, 1216 => 2500, _ => 0 };
        Assert.Equal(600, CombatService.ApplyDamageReductions(1000, magic: false, ranged: false, critical: false, Cut));
        Assert.Equal(675, CombatService.ApplyDamageReductions(1000, magic: false, ranged: true, critical: false, Cut)); // 230 is not set: 212 (10%) and 1216 (25%)
        // Healing: (1 + 0.10) * (1 + 0.10)
        Assert.Equal(1210, CombatService.ScaleHealing(1000, 1000, 0, 1000, 0));
        Assert.Equal(1300, CombatService.ScaleHealing(1000, 0, 0, 3000, 0));
        // Cast: variable x max(0, 1 - sqrt(c/600) + pre/10000), fixed x (1 + pre/10000); Arcane Acceleration is -8000 on both
        var row = System.Text.Json.JsonDocument.Parse("{\"_iChantFix\":500,\"_iChantVar\":1500}").RootElement;
        Assert.Equal(2000, CombatService.ChantOf(row, 0, 0, 0));
        Assert.Equal(400, CombatService.ChantOf(row, 0, -8000, -8000)); // 500*0.2 + 1500*0.2
        Assert.Equal(2400, CombatService.ChantOf(row, 0, 2500, 500)); // Phen +25% variable, 5% fixed: 500*1.05 + 1500*1.25
    }

    [Theory]
    [InlineData("""{"_kShowDmg":[5]}""", 5)]
    [InlineData("""{"_kShowDmg":[3,1]}""", 3)]
    [InlineData("""{"_kShowDmg":{}}""", 1)]
    [InlineData("""{}""", 1)]
    [InlineData("""{"_kShowDmg":[99]}""", 20)]
    public void HitSplitFollowsShowDmg(string row, int expected)
    {
        using var doc = JsonDocument.Parse(row);
        Assert.Equal(expected, CombatService.HitSplit(doc.RootElement));
    }

    [Theory]
    [InlineData("""{"m_iBulletControlType":0,"m_fBulletLifeTime":400}""", 50f, 400)]
    [InlineData("""{"m_iBulletControlType":0,"m_fBulletLifeTime":10}""", 1f, 120)]
    [InlineData("""{"m_iBulletControlType":1,"m_fBulletSpeed":24}""", 12f, 500)]
    [InlineData("""{"m_iBulletControlType":1,"m_fBulletSpeed":1}""", 100f, 2500)]
    [InlineData("""{"m_iBulletControlType":1}""", 10f, 120)]
    public void FlightTimeFollowsTheClientsBulletControl(string bullet, float distance, int expected)
    {
        using var doc = JsonDocument.Parse(bullet);
        Assert.Equal(expected, CombatService.FlightMs(doc.RootElement, distance));
    }

    [Fact]
    public void ChantIsTheConfiguredFixedPlusVariableTime()
    {
        // CBT video: Fire Bolt Lv.10 shows "CT 1.0s (0.0s+1.0s)" for _iChantFix 0 + _iChantVar 1000.
        using var doc = JsonDocument.Parse("""{"_iChantFix":0,"_iChantVar":1000}""");
        Assert.Equal(1000, CombatService.ChantOf(doc.RootElement));
        using var both = JsonDocument.Parse("""{"_iChantFix":500,"_iChantVar":1500}""");
        Assert.Equal(2000, CombatService.ChantOf(both.RootElement));
    }

    [Theory]
    [InlineData(0, 2000)]
    [InlineData(52, 1558)]   // 1 - sqrt(52/600) = 0.7056 on the 1500 ms variable part
    [InlineData(150, 1250)]  // 1 - sqrt(150/600) = 0.5
    [InlineData(600, 500)]   // variable part gone, fixed part stays
    [InlineData(5000, 500)]
    public void ChantSpeedShrinksOnlyTheVariablePart(long chantSpeed, int expected)
    {
        using var row = JsonDocument.Parse("""{"_iChantFix":500,"_iChantVar":1500}""");
        Assert.Equal(expected, CombatService.ChantOf(row.RootElement, chantSpeed));
    }
}
