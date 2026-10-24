using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Floor skills of the Priest and Mage lines that must stay on the floor and hit over time.</summary>
public sealed partial class SkillCombatTests
{
    /// <summary>
    /// Magnus Exorcismus (14109) is the Exorciser's cross on the floor. Its script was not a ground family, so NPC 22000009
    /// never spawned: it spawns for 4 s (_kLogicData[1]) and hits every 0.5 s (_kDescData[4]).
    /// </summary>
    [ClientDataFact]
    public async Task MagnusExorcismusLeavesItsCrossOnTheFloorForFourSeconds()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 4200"); await Teach("alice", 14109);
        var point = Session("alice").Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1410901, TargetPos = point });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var spawn = await Next(bob, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, x => x.Bases.Any(b => b.EntityTemplateID == 22000009));
        var cross = Assert.Single(spawn.Bases, b => b.EntityTemplateID == 22000009);
        Assert.Equal((long)alice.Self!.Uuid, cross.Owner); Assert.Equal(point, cross.MovementInfo.Pos);
        var unit = Assert.Single(Session("alice").SkillObjects);
        Assert.Equal(500, unit.IntervalMs);
        Assert.InRange(unit.ExpiresMs - unit.NextTickMs, 3000, 4500);
        await Next(bob, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, x => x.LeaveAoi.Contains(cross.Uuid));
        Assert.Empty(Session("alice").SkillObjects);
    }

    /// <summary>
    /// The cross hits every 0.5 s for 4 s whatever the skill level (_kDescData[3] and [4] are the same on all 10 levels; the
    /// level only changes the damage), so one cast lands about 8 hits on a monster that stays inside it.
    /// </summary>
    [ClientDataFact]
    public async Task MagnusExorcismusHitsEveryHalfSecondForFourSeconds()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 4200"); await Teach("alice", 14109);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 10_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1410901, TargetPos = Session("alice").Position.Clone() });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Task.Delay(5500);
        Assert.InRange(HitsOn(alice, accepted.SessionId, target.Uid), 7, 9);
    }

    private static int HitsOn(Ro3Emu.Bot.BotClient bot, long sessionId, long targetId) => bot.Inbox.Snapshot()
        .Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body))
        .Count(h => h.SessionId == sessionId && h.Dsts.Any(d => d.DstID == targetId));
}
