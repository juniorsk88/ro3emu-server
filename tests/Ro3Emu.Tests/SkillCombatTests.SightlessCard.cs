using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private sealed class SightlessRolls : CombatRolls
    {
        public override double Hit() => 0;
        public override double Crit() => 1;
        public override int Proc(int max) => max - 1;
    }

    [ClientDataFact]
    public async Task SpecialSightlessCardStunsMonsterWhenCast()
    {
        await using var alice = await InWorld("Alice");
        var player = Session("alice");
        var monster = await Target("alice", stationary: true);
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new SightlessRolls();
        await Loop.InvokeAsync(async () =>
        {
            monster.Hp = 1_000_000;
            player.Items.Add(new InvItem { Guid = 881107, Entry = 1460401, Stack = 1, Pack = 3, Grid = CharacterService.CardGrid(1, 1) });
            await _app.Services.GetRequiredService<CardEffects>().RefreshAsync(player);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2004501 });
        var cast = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, cast.Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == cast.SessionId);
        Assert.True(await Loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<BuffService>().UnitHasGroup(monster, BuffService.StunGroup))));
        Assert.False(await Loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<BuffService>().UnitHasGroup(monster, 9025))));
    }
}
