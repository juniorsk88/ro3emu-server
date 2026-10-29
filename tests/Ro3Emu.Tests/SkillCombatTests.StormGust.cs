using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task StormGustDealsFiveConfiguredWavesAndSplitsOnlyTheDisplay()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        var source = Session("alice");
        var target = await Target("alice", stationary: true);
        long initialHp = 1_000_000;
        long matk = await Loop.InvokeAsync(() =>
        {
            _app.Services.GetRequiredService<CombatService>().Rolls = new GuardianStableDamageRolls();
            _app.Services.GetRequiredService<IOptions<ServerOptions>>().Value.DamageBonusAttributes = false;
            source.Progress!.Skills[12108] = 10;
            target.Template = target.Template with { SpeedCm = 0, AttackRange = 0, Mdef = 0, Element = 1 };
            target.Hp = initialHp;
            return Task.FromResult(Character.Stats(source)[104]);
        });
        CastSkillRsp cast;
        for (int attempt = 0; ; attempt++)
        {
            await Loop.InvokeAsync(() => { source.Progress!.Skills[12108] = 10; return Task.CompletedTask; });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1210810, TargetPos = source.Position.Clone() });
            cast = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            if (cast.Result != ErrorCodes.SkillNotLearned || attempt >= 20) break;
            await Task.Delay(100);
        }
        Assert.Equal(0, cast.Result);
        var hits = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.SessionId == cast.SessionId && h.Dsts.Any(d => d.DstID == target.Uid));
            Assert.Equal(3, hit.Split);
            hits.Add(Assert.Single(hit.Dsts, d => d.DstID == target.Uid).Damage);
        }
        long expected = (long)Math.Round(matk * 0.1 + 130);
        Assert.All(hits, damage => Assert.Equal(expected, damage));
        Assert.Equal(initialHp - 5 * expected, await Loop.InvokeAsync(() => Task.FromResult(target.Hp)));
    }
    [ClientDataFact]
    public async Task StormGustHitsHostilePlayersInTheChosenArea()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        await MageAsync("alice");
        var source = Session("alice");
        var inside = Session("bob");
        var nearCaster = Session("carol");
        var center = await Loop.InvokeAsync(async () =>
        {
            var nav = _app.Services.GetRequiredService<WorldData>().Map(source.SceneEntry)?.Nav;
            var origin = source.Position;
            var offsets = new[] { (8f, 0f), (-8f, 0f), (0f, 8f), (0f, -8f), (6f, 6f), (-6f, 6f), (6f, -6f), (-6f, -6f) };
            var offset = offsets.First(o => nav == null || nav.CanWalk(origin.X, origin.Y, origin.Z, origin.X + o.Item1, origin.Z + o.Item2));
            var spot = new Vec3 { X = origin.X + offset.Item1, Y = origin.Y, Z = origin.Z + offset.Item2 };
            var scene = _app.Services.GetRequiredService<SceneService>();
            await scene.TeleportInMapAsync(inside, new MapPoint(spot.X, spot.Y, spot.Z, inside.Face));
            await scene.TeleportInMapAsync(nearCaster, new MapPoint(origin.X, origin.Y, origin.Z, nearCaster.Face));
            _app.Services.GetRequiredService<CombatService>().PlayerHostile = (a, b) => a == source && (b == inside || b == nearCaster);
            return spot;
        });
        long outsideHp = await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(nearCaster)[2000]));
        CastSkillRsp cast;
        for (int attempt = 0; ; attempt++)
        {
            await Loop.InvokeAsync(() => { source.Progress!.Skills[12108] = 1; return Task.CompletedTask; });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1210801, TargetPos = center });
            cast = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
            if (cast.Result != ErrorCodes.SkillNotLearned || attempt >= 20) break;
            await Task.Delay(100);
        }
        Assert.Equal(0, cast.Result);
        var hit = await Next(bob, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SessionId == cast.SessionId && h.Dsts.Any(d => d.DstID == inside.Role!.Id));
        Assert.True(Assert.Single(hit.Dsts, d => d.DstID == inside.Role!.Id).Damage > 0);
        Assert.Equal(outsideHp, await Loop.InvokeAsync(() => Task.FromResult(Character.Stats(nearCaster)[2000])));
    }

}
