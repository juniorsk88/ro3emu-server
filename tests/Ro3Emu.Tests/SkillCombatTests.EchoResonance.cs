using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private static void SetEchoMarks(GameSession session, int mark, int count)
    {
        var slots = session.Emblems.Plans[session.Emblems.CurrentPlan].Slots;
        slots.Clear();
        session.Items.RemoveAll(i => i.Guid is >= 980001 and <= 980003);
        for (int slot = 1; count > 0; slot++)
        {
            var item = new InvItem
            {
                Entry = 1850101, Stack = 1, Pack = 5, Guid = 980000 + slot,
                Attrs = new() { [1001] = 20, [10001] = 2, [10002] = slot }
            };
            for (int i = 0; i < Math.Min(count, 4); i++) item.Attrs[1021 + i] = mark;
            count -= 4;
            session.Items.Add(item);
            slots[slot] = item.Guid;
        }
    }

    [ClientDataFact]
    public async Task EchoSkillRatiosFollowMarksCareerAndExistingModes()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        await Loop.InvokeAsync(() =>
        {
            long[] Ratio(int id) => combat.EffectiveSkillRow(session, id)!.Value.GetProperty("_kDamageParam1")
                .EnumerateArray().Select(v => v.GetInt64()).ToArray();

            session.Progress!.Career = 2400;
            SetEchoMarks(session, 1004, 5);
            Assert.Equal(new long[] { 8000, 8000, 8000, 8000 }, Ratio(1231601));
            SetEchoMarks(session, 1004, 6);
            Assert.Equal(new long[] { 13000, 13000, 13000, 13000 }, Ratio(1231601));
            Assert.Equal(new long[] { 22000 }, Ratio(1231501));
            SetEchoMarks(session, 1004, 10);
            Assert.Equal(new long[] { 42000 }, Ratio(1231501));
            Assert.Equal(new long[] { 13000, 13000, 13000, 13000 }, Ratio(1231601));

            session.PassiveSkillModifiers = [([], new SkillParameterRules.Modifier(12315, 15, 1, 1, 0))];
            Assert.Equal(new long[] { 42000 }, Ratio(1231501)); // mode 1 already contains the +200
            session.PassiveSkillModifiers = [];
            var matchAttributes = Character.MatchAttributes;
            Character.MatchAttributes = _ => [];
            Assert.Equal(new long[] { 22000 }, Ratio(1231501));
            Character.MatchAttributes = matchAttributes;
            session.Progress.Career = 1400;
            Assert.Equal(new long[] { 22000 }, Ratio(1231501));
            SetEchoMarks(session, 1001, 6);
            Assert.Equal(new long[] { 20000 }, Ratio(1130101));
            var rift = combat.EffectiveSkillRow(session, 1130101)!.Value.GetProperty("_kBuff2");
            Assert.Contains(rift.EnumerateArray(), b => b[0].GetInt64() == 901601 && b[1].GetInt64() == 10000 && b[2].GetInt64() == 1500);
            SetEchoMarks(session, 1003, 6);
            Assert.Equal(new long[] { 35000 }, Ratio(1130801));
            SetEchoMarks(session, 1004, 6);
            Assert.Equal(new long[] { 13500 }, Ratio(1131001));
            session.PassiveSkillModifiers = [([], new SkillParameterRules.Modifier(11310, 15, 1, 1, 0))];
            Assert.Equal(new long[] { 20500 }, Ratio(1131001));
            session.PassiveSkillModifiers = [];
            session.Progress.Career = 1410;
            SetEchoMarks(session, 1001, 6);
            Assert.Equal(new long[] { 4000 }, Ratio(1162801));
            Assert.Equal(6000, combat.EffectiveSkillRow(session, 1162801)!.Value.GetProperty("_kBuff1")[0][2].GetInt64());
            SetEchoMarks(session, 1001, 10);
            Assert.Equal(new long[] { 18000 }, Ratio(1160701));
            SetEchoMarks(session, 1002, 6);
            Assert.Equal(new long[] { 3900 }, Ratio(1160401));
            session.PassiveSkillModifiers = [([], new SkillParameterRules.Modifier(11604, 15, 1, 4, 0))];
            Assert.Equal(new long[] { 5850 }, Ratio(1160401));
            session.PassiveSkillModifiers = [];
            SetEchoMarks(session, 1002, 10);
            session.Progress.Career = 4400;
            Assert.Equal(new long[] { 115000 }, Ratio(1430301));
            SetEchoMarks(session, 1001, 6);
            session.Progress.Career = 3400;
            Assert.Equal(new long[] { 5900 }, Ratio(1330501));
            session.PassiveSkillModifiers = [([], new SkillParameterRules.Modifier(13305, 15, 1, 4, 0))];
            Assert.Equal(new long[] { 5900, 1500 }, Ratio(1330501));
            session.PassiveSkillModifiers = [];
            SetEchoMarks(session, 1002, 6);
            session.Progress.Career = 5400;
            Assert.Contains(11531901, _app.Services.GetRequiredService<EmblemService>().ResonanceSkills(session));
            Assert.Equal(new long[] { 3300 }, Ratio(1530301));
            Assert.Equal(600, combat.EffectiveSkillRow(session, 1530301)!.Value.GetProperty("_kRange")[0].GetInt64());
            Assert.Equal(new long[] { 3200, 3520 }, Ratio(1530201));
            SetEchoMarks(session, 1002, 10);
            Assert.Equal(new long[] { 4200, 4620 }, Ratio(1530201));
            Assert.Equal(new long[] { 5000, 5500 }, Ratio(1530205));
            session.PassiveSkillModifiers = [([], new SkillParameterRules.Modifier(15302, 15, 1, 1, 0))];
            Assert.Equal(new long[] { 4200, 4620 }, Ratio(1530201));
            session.PassiveSkillModifiers = [];
            SetEchoMarks(session, 1003, 6);
            Assert.Equal(new long[] { 12000 }, Ratio(1532001));
            Assert.Equal(new long[] { 9000 }, Ratio(1532201));
            Assert.Equal(new long[] { 7000 }, Ratio(1532301));
            Assert.Equal(700, combat.EffectiveSkillRow(session, 1532301)!.Value.GetProperty("_kRange")[0].GetInt64());
            SetEchoMarks(session, 1001, 0);
            Assert.Equal(new long[] { 5600 }, Ratio(1330501));
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task EquippingAndRemovingEchoesRefreshesSkillParameters()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        await Gm("alice", "!job 2400");
        await Gm("alice", "!quest done 10103040");
        await Gm("alice", "!item 1850101 3");
        var session = Session("alice");
        long[] ids = await Loop.InvokeAsync(() =>
        {
            session.Progress!.Skills[12315] = 1;
            var items = session.Items.Where(i => i.Entry == 1850101).Take(3).ToArray();
            Assert.Equal(3, items.Length);
            foreach (var item in items)
            {
                item.Attrs![1001] = 20;
                for (int i = 0; i < 4; i++) item.Attrs[1021 + i] = 1004;
            }
            return Task.FromResult(items.Select(i => i.Guid).ToArray());
        });
        for (int slot = 1; slot <= 3; slot++)
        {
            await alice.SendGameAsync(EmblemService.SetUp, new SetUpEmblemReq { EmblemPos = slot, EmblemGuid = ids[slot - 1] });
            Assert.Equal(0, (await Next(alice, EmblemService.SetUp, SetUpEmblemResp.Parser)).Result);
        }
        var update = await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            m => m.AffectParamDict.TryGetValue(12315, out var info) && info.AffectParam.TryGetValue(29, out var ratio)
                && ratio.AffectValueList.Contains(42000));
        Assert.Equal(42000, update.AffectParamDict[12315].AffectParam[29].AffectValueList[0]);
        using (var cleared = alice.Inbox.Expect(e => e.IsGame(CombatService.SkillAffectedEx)
            && !SkillBeAffectedParamExNotify.Parser.ParseFrom(e.Body).AffectParamDict.ContainsKey(12315)))
        {
            await Gm("alice", "!job 1400");
            await cleared.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await Gm("alice", "!job 2400");
        await alice.SendGameAsync(EmblemService.SetDown, new SetDownEmblemReq { EmblemPos = 3 });
        Assert.Equal(0, (await Next(alice, EmblemService.SetDown, SetDownEmblemResp.Parser)).Result);
        long after = await Loop.InvokeAsync(() => Task.FromResult(_app.Services.GetRequiredService<CombatService>()
            .EffectiveSkillRow(session, 1231501)!.Value.GetProperty("_kDamageParam1")[0].GetInt64()));
        Assert.Equal(22000, after);
    }

    [ClientDataFact]
    public async Task RhythmicArrowChangesARealHitAndStopsAfterRemoval()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        await Gm("alice", "!job 3400");
        _app.Services.GetRequiredService<CombatService>().Rolls = new GuardianStableDamageRolls();
        var session = Session("alice");
        var target = await Target("alice", stationary: true);
        long baseline = (await CastMage(alice, 13305, 1, target)).Hit.Dsts.Single(d => d.DstID == target.Uid).Damage;
        await Task.Delay(1500);
        await Loop.InvokeAsync(() =>
        {
            SetEchoMarks(session, 1001, 6);
            session.SkillCds.Clear(); session.SkillModeCds.Clear();
            return Task.CompletedTask;
        });
        long boosted = (await CastMage(alice, 13305, 1, target)).Hit.Dsts.Single(d => d.DstID == target.Uid).Damage;
        Assert.True(boosted > baseline);
        await Loop.InvokeAsync(() =>
        {
            SetEchoMarks(session, 1001, 0);
            Assert.Equal(5600, _app.Services.GetRequiredService<CombatService>().EffectiveSkillRow(session, 1330501)!
                .Value.GetProperty("_kDamageParam1")[0].GetInt64());
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task DragonsDescentStunsTheMonsterHitByRiftImpact()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        await Gm("alice", "!job 1400");
        _app.Services.GetRequiredService<CombatService>().Rolls = new GuardianStableDamageRolls();
        var session = Session("alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            SetEchoMarks(session, 1001, 6);
            session.Progress!.Skills[11301] = 1;
            target.Hp = 1_000_000;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1130101, TargetId = target.Uid, TargetPos = target.Pos });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            b => b.UnitId == target.Uid && b.Buffs.Any(x => x.ConfigId == 901601));
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 1130101 && h.Dsts.Any(d => d.DstID == target.Uid));
    }

    [ClientDataFact]
    public async Task DragonsDescentAlsoStunsAHostilePlayerHitByRiftImpact()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await MageAsync("alice");
        await Gm("alice", "!job 1400");
        await Gm("bob", "!lvl 99");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new GuardianStableDamageRolls();
        var source = Session("alice");
        var victim = Session("bob");
        await Loop.InvokeAsync(() =>
        {
            SetEchoMarks(source, 1001, 6);
            source.Progress!.Skills[11301] = 1;
            victim.Position = source.Position.Clone();
            combat.PlayerHostile = (a, b) => a == source && b == victim;
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1130101, TargetId = victim.Role!.Id, TargetPos = victim.Position.Clone() });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bob, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            b => b.UnitId == victim.Role.Id && b.Buffs.Any(x => x.ConfigId == 901601));
    }

    [ClientDataFact]
    public async Task ThomasFlareExpandsRollingCutterAreaInCombatAndOnTheClient()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        await Gm("alice", "!job 5400");
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new GuardianStableDamageRolls();
        var source = Session("alice");
        var near = await Target("alice", stationary: true);
        var far = await Target("alice", stationary: true);
        await Loop.InvokeAsync(async () =>
        {
            source.Progress!.Skills[15303] = 1;
            combat.MonsterTargetAllowed = (_, target) => target == near || target == far;
            near.Hp = far.Hp = 1_000_000;
            far.X = source.Position.X + 5.5f;
            Assert.Equal(500, combat.EffectiveSkillRow(source, 1530301)!.Value.GetProperty("_kRange")[0].GetInt64());
            SetEchoMarks(source, 1002, 6);
            await combat.RefreshSkillParametersAsync(source);
        });
        var update = await Next(alice, CombatService.SkillAffectedEx, SkillBeAffectedParamExNotify.Parser,
            m => m.AffectParamDict.TryGetValue(15303, out var info) && info.AffectParam.ContainsKey(36));
        Assert.Equal(600, update.AffectParamDict[15303].AffectParam[36].AffectValueList[0]);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1530301 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var after = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SkillId == 1530301 && h.Dsts.Any(d => d.DstID == far.Uid));
        Assert.Contains(after.Dsts, d => d.DstID == near.Uid);
        Assert.Contains(after.Dsts, d => d.DstID == far.Uid);
    }
}
