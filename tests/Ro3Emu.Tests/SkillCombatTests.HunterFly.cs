using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task HunterFlyHealsFromAdditionalCardDamageAgainstMonster()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("alice");
        var target = await Target("alice", stationary: true);
        var character = _app.Services.GetRequiredService<CharacterService>();
        var cards = _app.Services.GetRequiredService<CardEffects>();
        cards.Roll = _ => 0;
        try
        {
            await Loop.InvokeAsync(async () =>
            {
                session.Progress!.Stats[2] = 1000;
                session.Items.Add(new InvItem { Guid = 880113, Entry = 1430118, Stack = 1, Pack = 3,
                    Grid = CharacterService.CardGrid(1, 1), Attrs = new() { [502] = 3 } });
                await character.SetHpSpAsync(session, 1, null);
                Assert.True(character.Stats(session)[101] > 101);
                target.Hp = 100_000;
                await _app.Services.GetRequiredService<CombatService>().HitMonsterAsync(session, target, 1000, 0);
                Assert.Equal(101, character.Stats(session)[2000]);
            });
            var heal = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.Dsts.Any(d => d.DstID == session.Role!.Id && d.DamageType == DamageType.Heal));
            Assert.Equal(100, Assert.Single(heal.Dsts).Damage);
        }
        finally { cards.Roll = bound => Random.Shared.Next(bound); }
    }

    [ClientDataFact]
    public async Task HunterFlyHealsFromMagicDamageAgainstMonster()
    {
        await using var alice = await InWorld("Alice");
        await MageAsync("alice");
        await Teach("alice", 12004);
        var session = Session("alice");
        var target = await Target("alice", stationary: true);
        var character = _app.Services.GetRequiredService<CharacterService>();
        var cards = _app.Services.GetRequiredService<CardEffects>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        combat.Rolls = new GuardianStableDamageRolls();
        cards.Roll = _ => 0;
        try
        {
            long before = await Loop.InvokeAsync(async () =>
            {
                session.Items.Add(new InvItem { Guid = 880101, Entry = 1430118, Stack = 1, Pack = 3,
                    Grid = CharacterService.CardGrid(1, 1), Attrs = new() { [502] = 3 } });
                target.Hp = 1_000_000;
                session.LastCombatMs = MonsterService.NowMs;
                session.StatusBits |= 1L << 15; // Keep natural regeneration out of the card-healing assertion.
                var hp = Math.Max(1, character.Stats(session)[101] / 2);
                await character.SetHpSpAsync(session, hp, null);
                return hp;
            });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = target.Uid });
            Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
                h => h.SkillId == 1200401 && h.Dsts.Any(d => d.DstID == target.Uid));
            long damage = hit.Dsts.Single(d => d.DstID == target.Uid).Damage;
            Assert.True(damage >= 10);
            long after = await Loop.InvokeAsync(() => Task.FromResult(character.Stats(session)[2000]));
            Assert.Equal(before + Math.Min(2000, damage / 10), after);
        }
        finally { cards.Roll = bound => Random.Shared.Next(bound); }
    }
}
