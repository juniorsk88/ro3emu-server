using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task TetraVortexHitsPlayersAroundTheChosenPoint()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await using var dave = await InWorld("Dave");
        await MageAsync("alice");
        await Gm("alice", "!job 2400");
        await Gm("bob", "!lvl 99");
        await Gm("carol", "!lvl 99");
        await Gm("dave", "!lvl 99");
        var source = Session("alice");
        var inside = Session("bob");
        var outside = Session("carol");
        var protectedPlayer = Session("dave");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var point = source.Position.Clone();
        var nav = _app.Services.GetRequiredService<WorldData>().Map(source.SceneEntry)!.Nav;
        var (dx, dz) = new[] { (10f, 0f), (-10f, 0f), (0f, 10f), (0f, -10f) }
            .First(o => nav == null || nav.CanWalk(point.X, point.Y, point.Z, point.X + o.Item1, point.Z + o.Item2));
        point.X += dx;
        point.Z += dz;
        long? outsideHp = await Loop.InvokeAsync(() =>
        {
            source.Progress!.Skills[12316] = 1;
            inside.Position = point.Clone();
            outside.Position = source.Position.Clone();
            protectedPlayer.Position = point.Clone();
            combat.PlayerHostile = (a, b) => a == source && (b == inside || b == outside);
            combat.MonsterTargetAllowed = (_, _) => false;
            return Task.FromResult(outside.CurrentHp);
        });
        await alice.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1231601, TargetPos = point });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 1231601 && h.Dsts.Any(d => d.DstID == inside.Role!.Id));
        Assert.True(hit.Dsts.Single(d => d.DstID == inside.Role!.Id).Damage > 0);
        await Task.Delay(300); // Let the cast finish publishing other eligible targets.
        await Loop.InvokeAsync(() =>
        {
            Assert.Equal(outsideHp, outside.CurrentHp);
            Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
                && SkillHitInfo.Parser.ParseFrom(e.Body).Dsts.Any(d => d.DstID == protectedPlayer.Role!.Id));
            return Task.CompletedTask;
        });
    }
}
