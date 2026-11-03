using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private sealed class MeteorProcRolls : CombatRolls
    {
        public override double Hit() => 0;
        public override double Crit() => 1;
        public override double DamageFloat() => 0.5;
        public override int Proc(int max) => 0;
    }

    [ClientDataTheory]
    [InlineData(7)]
    [InlineData(10)]
    public async Task MeteorHasOneWavePerLevelAndTenIndependentHits(int level)
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        var source = Session("alice");
        var target = await Target("alice", stationary: true);
        long initialHp = 10_000_000;
        await Loop.InvokeAsync(() =>
        {
            _app.Services.GetRequiredService<CombatService>().Rolls = new NoProcDamageRolls();
            _app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.DamageBonusAttributes = false;
            source.Progress!.Skills[12106] = level;
            target.Template = target.Template with { SpeedCm = 0, AttackRange = 0, Mdef = 0, Element = 1 };
            target.Hp = initialHp;
            return Task.CompletedTask;
        });
        CastSkillRsp cast;
        for (int attempt = 0; ; attempt++)
        {
            await Loop.InvokeAsync(() => { source.Progress!.Skills[12106] = level; return Task.CompletedTask; });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1210600 + level, TargetPos = source.Position.Clone() });
            cast = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            if (cast.Result != ErrorCodes.SkillNotLearned || attempt >= 20) break;
            await Task.Delay(100);
        }
        Assert.Equal(0, cast.Result);
        var first = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SessionId == cast.SessionId && h.Dsts.Any(d => d.DstID == target.Uid));
        Assert.Equal(1, first.Split);
        long total = Assert.Single(first.Dsts, d => d.DstID == target.Uid).Damage;
        var field = await Loop.InvokeAsync(() => Task.FromResult(Assert.Single(source.SkillObjects, x => x.Cast.Id == cast.SessionId)));
        Assert.Equal(10000 / level, field.IntervalMs);
        Assert.InRange(field.ExpiresMs - field.NextTickMs, 9980 - field.IntervalMs, 10020 - field.IntervalMs);
        for (int i = 1; i < level * 10; i++)
        {
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.SessionId == cast.SessionId && h.Dsts.Any(d => d.DstID == target.Uid));
            Assert.Equal(1, hit.Split);
            total += Assert.Single(hit.Dsts, d => d.DstID == target.Uid).Damage;
        }
        Assert.True(total > 0);
        Assert.Equal(initialHp - total, await Loop.InvokeAsync(() => Task.FromResult(target.Hp)));
        await Task.Delay(2000);
        Assert.Equal(initialHp - total, await Loop.InvokeAsync(() => Task.FromResult(target.Hp)));
    }

    [ClientDataFact]
    public async Task MeteorStunActuallyForbidsMonsterActions()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        var source = Session("alice");
        var target = await Target("alice", stationary: true);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await Loop.InvokeAsync(() =>
        {
            _app.Services.GetRequiredService<CombatService>().Rolls = new MeteorProcRolls();
            source.Progress!.Skills[12106] = 1;
            target.Hp = 1_000_000;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1210601, TargetPos = source.Position.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1210601 && h.Dsts.Any(d => d.DstID == target.Uid));
        await Loop.InvokeAsync(() =>
        {
            Assert.True(buffs.UnitHasGroup(target, BuffService.StunGroup));
            Assert.True(buffs.UnitForbidden(target, ForbiddenState.Walk));
            Assert.True(buffs.UnitForbidden(target, ForbiddenState.NormalAttack));
            return Task.CompletedTask;
        });
    }
}
