using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task PsalmHealsAndShieldsCasterAndAllyWithItsOwnCooldown()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await MageAsync("alice");
        await Gm("alice", "!job 4400");
        var source = Session("alice");
        var ally = Session("bob");
        var buffs = _app.Services.GetRequiredService<BuffService>();
        long expected = await Loop.InvokeAsync(() =>
        {
            source.Progress!.Skills[14301] = 1;
            SetEchoMarks(source, 1002, 6);
            source.CurrentHp = ally.CurrentHp = 1;
            ally.Position = source.Position.Clone();
            return Task.FromResult((long)Math.Floor(Character.Stats(source)[104] * .4));
        });
        Assert.True(expected > 0);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430101, TargetId = ally.Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1430101);
        Assert.Contains(hit.Dsts, d => d.DstID == source.Role!.Id && d.Damage > 0 && d.DamageType == DamageType.Heal);
        Assert.Contains(hit.Dsts, d => d.DstID == ally.Role.Id && d.Damage > 0 && d.DamageType == DamageType.Heal);
        await Loop.InvokeAsync(async () =>
        {
            Assert.Equal(expected, buffs.ShieldAmount(source));
            Assert.Equal(expected, buffs.ShieldAmount(ally));
            await buffs.RemoveAsync(source, source.Buffs.Single(b => b.ConfigId == 11430701));
            await buffs.RemoveAsync(ally, ally.Buffs.Single(b => b.ConfigId == 11430701));
        });
        await Task.Delay(1200);
        await Loop.InvokeAsync(() => { source.SkillCds.Clear(); source.SkillModeCds.Clear(); return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430101, TargetId = ally.Role.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1430101);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(0, buffs.ShieldAmount(source));
            Assert.Equal(0, buffs.ShieldAmount(ally));
            return Task.CompletedTask;
        });
        await Task.Delay(4000);
        await Loop.InvokeAsync(() => { source.SkillCds.Clear(); source.SkillModeCds.Clear(); return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430101, TargetId = ally.Role.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1430101);
        await Loop.InvokeAsync(async () =>
        {
            Assert.Equal(expected, buffs.ShieldAmount(source));
            Assert.Equal(expected, buffs.ShieldAmount(ally));
            await buffs.RemoveAsync(source, source.Buffs.Single(b => b.ConfigId == 11430701));
            await buffs.RemoveAsync(ally, ally.Buffs.Single(b => b.ConfigId == 11430701));
            SetEchoMarks(source, 1002, 5);
            source.SkillProtocol.EchoPsalmReadyMs = 0;
        });
        await Task.Delay(1200);
        await Loop.InvokeAsync(() => { source.SkillCds.Clear(); source.SkillModeCds.Clear(); return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1430101, TargetId = ally.Role.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1430101);
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(0, buffs.ShieldAmount(source));
            Assert.Equal(0, buffs.ShieldAmount(ally));
            return Task.CompletedTask;
        });
    }
}
