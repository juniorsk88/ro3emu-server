using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task SoulStrikeUsesItsUndeadElementDamageBranch()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new GuardianStableDamageRolls();
        var data = _app.Services.GetRequiredService<GameData>();
        var row = data.Row("SkillConfig", 1200801)!.Value;
        Assert.Equal(0, CombatService.SoulStrikeBranch(row, 1));
        Assert.Equal(1, CombatService.SoulStrikeBranch(row, 10));
        var source = Session("alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            combat.MonsterTargetAllowed = (_, monster) => monster == target;
            target.Template = target.Template with { Element = 1 };
            return Task.CompletedTask;
        });
        var normal = (await CastMage(alice, 12008, 1, target)).Hit;
        long baseline = Assert.Single(normal.Dsts).Damage;
        Assert.Equal(5, normal.Split);
        await Task.Delay(1500);
        await Loop.InvokeAsync(() =>
        {
            source.SkillCds.Clear(); source.SkillModeCds.Clear();
            target.Template = target.Template with { Element = 10 };
            return Task.CompletedTask;
        });
        var undead = (await CastMage(alice, 12008, 1, target)).Hit;
        long damage = Assert.Single(undead.Dsts).Damage;
        // 1.5x from the configured undead branch and 1.25/0.75 from the two EleConfig rows.
        Assert.InRange(damage, (long)(baseline * 2.5) - 2, (long)(baseline * 2.5) + 2);
    }

    [ClientDataFact]
    public async Task SoulStrikeCardAutocastUsesTheUndeadElementBranch()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new GuardianStableDamageRolls();
        var source = Session("alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 1_000_000;
            target.Template = target.Template with { Element = 1 };
            await combat.AutocastOnMonsterAsync(source, target, 1200801);
        });
        long baseline = Assert.Single((await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1200801)).Dsts).Damage;
        await Loop.InvokeAsync(async () =>
        {
            target.Hp = 1_000_000;
            target.Template = target.Template with { Element = 10 };
            await combat.AutocastOnMonsterAsync(source, target, 1200801);
        });
        long undead = Assert.Single((await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1200801)).Dsts).Damage;
        Assert.InRange(undead, (long)(baseline * 2.5) - 2, (long)(baseline * 2.5) + 2);
    }

    [ClientDataFact]
    public async Task SoulStrikeUsesUndeadArmorElementAgainstAPlayer()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await MageAsync("alice");
        await Gm("bob", "!lvl 99");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new GuardianStableDamageRolls();
        var source = Session("alice");
        var victim = Session("bob");
        await Loop.InvokeAsync(() =>
        {
            source.Progress!.Skills[12008] = 1;
            combat.PlayerHostile = (a, b) => a == source && b == victim;
            combat.ArmorElementOf = _ => 1;
            victim.Position = source.Position.Clone();
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200801, TargetId = victim.Role!.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        long baseline = Assert.Single((await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1200801)).Dsts).Damage;
        await Task.Delay(1500);
        await Loop.InvokeAsync(() =>
        {
            combat.ArmorElementOf = _ => 10;
            source.SkillCds.Clear(); source.SkillModeCds.Clear();
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200801, TargetId = victim.Role.Id });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        long undead = Assert.Single((await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1200801)).Dsts).Damage;
        Assert.InRange(undead, (long)(baseline * 2.5) - 2, (long)(baseline * 2.5) + 2);
    }

    [ClientDataFact]
    public async Task PiercingShadowUsesBothConfiguredDamageComponents()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        await Gm("alice", "!job 5400");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new GuardianStableDamageRolls();
        var data = _app.Services.GetRequiredService<GameData>();
        Assert.Equal(22000, CombatService.DamageRatio(data.Row("SkillConfig", 1532601)!.Value, 0));
        Assert.Equal(30000, CombatService.DamageRatio(data.Row("SkillConfig", 1532605)!.Value, 0));
        var source = Session("alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            source.Progress!.Skills[15326] = 1;
            source.PassiveSkillModifiers = [([], new SkillParameterRules.Modifier(15326, 29, 2, 0, 1))];
            combat.MonsterTargetAllowed = (_, monster) => monster == target;
            target.Hp = 1_000_000;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1532601 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var first = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1532601);
        long half = Assert.Single(first.Dsts).Damage;
        Assert.Equal(8, first.Split);
        await Task.Delay(1500);
        await Loop.InvokeAsync(() =>
        {
            source.PassiveSkillModifiers = [];
            source.SkillCds.Clear(); source.SkillModeCds.Clear();
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1532601 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var second = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1532601);
        long full = Assert.Single(second.Dsts).Damage;
        Assert.InRange(full, half * 2 - 1, half * 2 + 1);
    }
}
