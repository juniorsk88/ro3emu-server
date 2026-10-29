using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task MagicDamageCardChangesTheActualMagicHit()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        _app.Services.GetRequiredService<CombatService>().Rolls = new GuardianStableDamageRolls();
        var s = Session("alice");
        var target = await Target("alice", stationary: true);
        long baseline = (await CastMage(alice, 12004, 1, target)).Hit.Dsts.Single(d => d.DstID == target.Uid).Damage;
        await Task.Delay(1500);
        await Loop.InvokeAsync(() =>
        {
            s.SkillCds.Clear(); s.SkillModeCds.Clear();
            s.Items.Add(new InvItem { Guid = 991234, Entry = 1440106, Stack = 1, Pack = 3, Grid = CharacterService.CardGrid(1, 1) });
            Assert.True(_app.Services.GetRequiredService<CharacterService>().Stats(s)[213] >= 240);
            return Task.CompletedTask;
        });
        long boosted = (await CastMage(alice, 12004, 1, target)).Hit.Dsts.Single(d => d.DstID == target.Uid).Damage;
        Assert.InRange(boosted * 100.0 / baseline, 101.5, 103.5);
    }

    [ClientDataTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GuardianDamageBuffsAreCountedOnceForMagicHits(bool damageBonusAttributes)
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        _app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.DamageBonusAttributes = damageBonusAttributes;
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new GuardianStableDamageRolls();
        var s = Session("alice");
        var target = await Target("alice", stationary: true);
        long baseline = (await CastMage(alice, 12004, 1, target)).Hit.Dsts.Single(d => d.DstID == target.Uid).Damage;
        await Task.Delay(1500);
        await Loop.InvokeAsync(async () =>
        {
            s.SkillCds.Clear(); s.SkillModeCds.Clear();
            var buffs = _app.Services.GetRequiredService<BuffService>();
            await buffs.AddGuardianAsync(s, 2303301, 30000, s.Role!.Id); // Moonlight: +5% damage
            await buffs.AddGuardianAsync(s, 2315501, 30000, s.Role.Id); // Esen's Execution: +5% damage
        });
        long boosted = (await CastMage(alice, 12004, 1, target)).Hit.Dsts.Single(d => d.DstID == target.Uid).Damage;
        Assert.InRange(boosted * 100.0 / baseline, 109, 111);
    }

    [ClientDataFact]
    public async Task WildcatChargeDoesNotBoostGuardianHits()
    {
        await using var alice = await InWorld("Alice");
        var s = Session("alice");
        await Loop.InvokeAsync(async () =>
        {
            await _app.Services.GetRequiredService<BuffService>().AddGuardianAsync(s, 2301401, 30000, s.Role!.Id);
            var stats = _app.Services.GetRequiredService<CharacterService>().Stats(s);
            var ownerHit = JsonSerializer.SerializeToElement(new { _iSystemType = 0 });
            var petHit = JsonSerializer.SerializeToElement(new { _iSystemType = 41 });
            Assert.Equal(stats[1215], CombatService.DamageAttribute(s, ownerHit, stats, 1215));
            Assert.Equal(stats[1215] - 1000, CombatService.DamageAttribute(s, petHit, stats, 1215));
        });
    }

    [ClientDataFact]
    public async Task WeaponStrengtheningAndRefiningIncreaseMagicAttackAndDamage()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        _app.Services.GetRequiredService<CombatService>().Rolls = new GuardianStableDamageRolls();
        var s = Session("alice");
        var target = await Target("alice", stationary: true);
        await Gm("alice", "!item 2010121 1");
        long weapon = await Loop.InvokeAsync(() => Task.FromResult(s.Items.First(i => i.Entry == 2010121 && i.Grid == 0).Guid));
        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = weapon, Pos = (EquipGridType)1 });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        long initialMatk = await Loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<CharacterService>().Stats(s)[104]));
        long baseline = (await CastMage(alice, 12004, 1, target)).Hit.Dsts.Single(d => d.DstID == target.Uid).Damage;
        await Task.Delay(1500);
        await Loop.InvokeAsync(() =>
        {
            s.SkillCds.Clear(); s.SkillModeCds.Clear();
            s.EquipLevels.Strength[1] = 5; s.EquipLevels.Refine[1] = 5;
            Assert.Equal(initialMatk + 118, _app.Services.GetRequiredService<CharacterService>().Stats(s)[104]);
            return Task.CompletedTask;
        });
        long boosted = (await CastMage(alice, 12004, 1, target)).Hit.Dsts.Single(d => d.DstID == target.Uid).Damage;
        Assert.True(boosted > baseline);
    }
}
