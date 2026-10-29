using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task WornSoulEchoIncreasesMagicAttackAndActualDamage()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        _app.Services.GetRequiredService<CombatService>().Rolls = new GuardianStableDamageRolls();
        await Gm("alice", "!quest done 10103040");
        await Gm("alice", "!item 1890112"); // fixed +15 attack conversion, no random subattributes
        var s = Session("alice");
        var target = await Target("alice", stationary: true);
        long guid = await Loop.InvokeAsync(() => Task.FromResult(s.Items.Single(i => i.Entry == 1890112).Guid));
        long before = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(s)[104]));
        long baseline = (await CastMage(alice, 12004, 1, target)).Hit.Dsts.Single(d => d.DstID == target.Uid).Damage;

        await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = 5, EmblemGuid = guid });
        Assert.Equal(0, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
        long equipped = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(s)[104]));
        Assert.Equal(before + 15, equipped);
        await Task.Delay(1500);
        await Loop.InvokeAsync(() => { s.SkillCds.Clear(); s.SkillModeCds.Clear(); return Task.CompletedTask; });
        long boosted = (await CastMage(alice, 12004, 1, target)).Hit.Dsts.Single(d => d.DstID == target.Uid).Damage;
        Assert.True(boosted > baseline);

        await alice.SendGameAsync(EmblemService.SetDown, new SetDownEmblemReq { EmblemPos = 5 });
        Assert.Equal(0, (await Next(alice, EmblemService.SetDown, SetDownEmblemResp.Parser)).Result);
        Assert.Equal(before, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(s)[104])));
    }
}
