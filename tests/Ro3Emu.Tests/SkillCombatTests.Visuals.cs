using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>What a mage's cast puts on the wire for the client to draw (docs/systems/skill-visuals.md).</summary>
public sealed partial class SkillCombatTests
{
    private async Task MageAsync(string account)
    {
        await Gm(account, "!job 2100"); await Gm(account, "!lvl 99");
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            bool ready = false;
            await Loop.InvokeAsync(() => { ready = Session(account).Progress is { Level: 99 } p && p.Career == 2100; return Task.CompletedTask; });
            if (ready) return;
            Assert.True(DateTime.UtcNow < until, "job/level not applied");
            await Task.Delay(50);
        }
    }

    private async Task<(CastSkillRsp Cast, SkillFixTimeBulletNotify? Bullet, long BulletAt, SkillHitInfo Hit, long HitAt)> CastMage(
        BotClient alice, int group, int level, MonsterUnit target)
    {
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        // Level-up observers may still rewrite the progress right after the GM reply, dropping a just-taught skill.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        CastSkillRsp cast;
        for (int attempt = 0; ; attempt++)
        {
            await Loop.InvokeAsync(() => { Session("alice").Progress!.Skills[group] = level; return Task.CompletedTask; });
            clock.Restart();
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = group * 100 + level, TargetId = target.Uid });
            cast = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            if (cast.Result != ErrorCodes.SkillNotLearned || attempt >= 20) break;
            await Task.Delay(100);
        }
        Assert.Equal(0, cast.Result);
        SkillFixTimeBulletNotify? bullet = null; long bulletAt = 0;
        try
        {
            bullet = await Next(alice, CombatService.SkillBullet, SkillFixTimeBulletNotify.Parser, b => b.SkillConfigId == group * 100 + level);
            bulletAt = clock.ElapsedMilliseconds;
        }
        catch (TimeoutException) { }
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == group * 100 + level);
        return (cast, bullet, bulletAt, hit, clock.ElapsedMilliseconds);
    }

    [ClientDataFact]
    public async Task FireBoltDropsFlamesOnTheTargetAndSplitsTheHit()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        var monster = await Target("alice", stationary: true);
        var r = await CastMage(alice, 12004, 5, monster);
        Assert.NotNull(r.Bullet);
        Assert.Equal(1200411, r.Bullet!.EffectId);
        Assert.Equal(5, r.Bullet.Split);                                   // _kShowDmg[0] of Fire Bolt level 5
        Assert.Equal(0, r.Bullet.SourceId);                                // Sag track: the client starts at SourcePosition
        Assert.Equal(monster.Uid, r.Bullet.TargetId);
        Assert.Equal(monster.X, r.Bullet.SourcePosition.X, 0.01);
        Assert.InRange(r.Bullet.StartTimestampMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(1200404, r.Hit.ShowPoint);                            // the type-3 effect, not the projectile
        Assert.Equal(5, r.Hit.Split);
        Assert.InRange(r.HitAt - r.BulletAt, 350, 900);                    // m_fBulletLifeTime = 400 ms
    }

    [ClientDataFact]
    public async Task LightningBoltHitsWithItsOwnImpactEffect()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        var monster = await Target("alice", stationary: true);
        var r = await CastMage(alice, 12005, 5, monster);
        Assert.Equal(1200503, r.Bullet!.EffectId);
        Assert.Equal(1200504, r.Hit.ShowPoint);                            // the first m_kDamage of the range is the projectile
        Assert.Equal(5, r.Hit.Split);
    }

    [ClientDataFact]
    public async Task FireBallFliesTheVisibleBallAndHitsWithItsImpact()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        var monster = await Target("alice", stationary: true);
        var r = await CastMage(alice, 12012, 1, monster);
        Assert.Equal(1201210, r.Bullet!.EffectId);                         // 1201208 has an empty prefab name
        Assert.Equal(1, r.Bullet.Split);
        Assert.NotEqual(0, r.Bullet.SourceId);                             // speed-controlled ball leaves the caster
        Assert.Equal(1201211, r.Hit.ShowPoint);
        Assert.Equal(1, r.Hit.Split);
        Assert.True(r.Hit.Dsts.Count >= 1);
    }
}
