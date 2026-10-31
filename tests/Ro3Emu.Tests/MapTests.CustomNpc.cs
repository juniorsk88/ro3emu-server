using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

public sealed partial class MapTests
{
    [ClientDataFact]
    public void PronteraCustomNpcLocationsAreWalkable()
    {
        var map = _app.Services.GetRequiredService<WorldData>().Map(Prontera)!;
        var data = _app.Services.GetRequiredService<GameData>();
        Assert.Null(data.Row("NPCConfig", CustomNpcSettings.HealerNpc));
        Assert.Null(data.Row("NPCConfig", CustomNpcSettings.HunterNpc));
        Assert.Null(data.Row("NPCInteractConfig", CustomNpcSettings.HealerInteraction));
        Assert.NotNull(map.Nav);
        foreach (int id in new[] { CustomNpcSettings.HealerNpc, CustomNpcSettings.HunterNpc })
        {
            var npc = Assert.Single(map.Npcs, n => n.NpcId == id);
            Assert.True(map.Nav.IsWalkable(npc.X, npc.Y, npc.Z), $"NPC {id} is not on walkable ground");
            var entity = SceneService.NpcEntity(npc);
            Assert.Equal(id == CustomNpcSettings.HealerNpc ? 180f : 90f, entity.Face);
            Assert.Equal(entity.Face, entity.MovementInfo.Face);
        }
    }

    [ClientDataFact]
    public async Task HealerRestoresHpSpAndAppliesTenMinuteBuffs()
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser);
        await Gm("alice", "!warp 10100 156.5 190.5");
        var (_, _, npcs) = await FollowSceneChange(alice);
        Assert.DoesNotContain(npcs.Bases, n => n.EntityTemplateID == CustomNpcSettings.HealerNpc || n.EntityTemplateID == CustomNpcSettings.HunterNpc);
        var hidden = Assert.Single(_app.Services.GetRequiredService<WorldData>().Map(Prontera)!.Npcs,
            n => n.NpcId == CustomNpcSettings.HealerNpc);
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        { NpcUid = hidden.Uid, InteractionId = CustomNpcSettings.HealerInteraction, SceneConfigId = Prontera });
        Assert.NotEqual(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await alice.SendGameAsync(SceneService.CustomNpcHello, new NullArg());
        npcs = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            v => v.Bases.Any(n => n.EntityTemplateID == CustomNpcSettings.HealerNpc));
        var healer = Assert.Single(npcs.Bases, n => n.EntityTemplateID == CustomNpcSettings.HealerNpc);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
        var character = _app.Services.GetRequiredService<CharacterService>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        await loop.InvokeAsync(() => character.SetHpSpAsync(session, 1, 1));
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        { NpcUid = (long)healer.Uuid, InteractionId = CustomNpcSettings.HealerDialogue, SceneConfigId = Prontera });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await loop.InvokeAsync(() =>
        {
            Assert.Equal(1, character.Stats(session)[2000]);
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        { NpcUid = (long)healer.Uuid, InteractionId = CustomNpcSettings.HealerInteraction, SceneConfigId = Prontera });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            p => p.Property.Propertys.Any(v => v.PropertyId == 2000 && v.Data > 1));
        await loop.InvokeAsync(() =>
        {
            var stats = character.Stats(session);
            Assert.Equal(stats[101], stats[2000]);
            Assert.Equal(stats[102], stats[2001]);
            foreach (int id in new[] { 1400110, 1401010 })
                Assert.Equal(600000, Assert.Single(session.Buffs, b => b.ConfigId == id).DurationMs);
            return Task.CompletedTask;
        });
        var buffService = _app.Services.GetRequiredService<BuffService>();
        await loop.InvokeAsync(async () =>
        {
            await buffService.AddPlayerSupportAsync(session, 1400110, 120000, session.Role!.Id, 2);
            await character.SetHpSpAsync(session, 1, 1);
        });
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        { NpcUid = (long)healer.Uuid, InteractionId = CustomNpcSettings.HealerInteraction, SceneConfigId = Prontera });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            p => p.Property.Propertys.Any(v => v.PropertyId == 2000 && v.Data > 1));
        await loop.InvokeAsync(() =>
        {
            var blessing = Assert.Single(session.Buffs, b => b.ConfigId == 1400110);
            Assert.Equal(session.Role!.Id, blessing.SourceId);
            Assert.All(blessing.Changes, c => Assert.Equal(20, c.Value));
            return Task.CompletedTask;
        });
        await loop.InvokeAsync(async () =>
        {
            foreach (var buff in session.Buffs.Where(b => b.ConfigId is 1400110 or 1401010).ToList())
                await buffService.RemoveAsync(session, buff);
            await character.SetHpSpAsync(session, 1, 1);
        });
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "custom-npcs.json"),
            "{\"healer\":true,\"hunter\":true,\"buffLevel\":5,\"job50\":false}");
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        { NpcUid = (long)healer.Uuid, InteractionId = CustomNpcSettings.HealerInteraction, SceneConfigId = Prontera });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            p => p.Property.Propertys.Any(v => v.PropertyId == 2000 && v.Data > 1));
        await loop.InvokeAsync(() =>
        {
            foreach (int id in new[] { 1400105, 1401005 })
                Assert.Equal(600000, Assert.Single(session.Buffs, b => b.ConfigId == id).DurationMs);
            return Task.CompletedTask;
        });
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "custom-npcs.json"),
            "{\"healer\":false,\"hunter\":true,\"buffLevel\":5,\"job50\":false}");
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        { NpcUid = (long)healer.Uuid, InteractionId = CustomNpcSettings.HealerInteraction, SceneConfigId = Prontera });
        Assert.NotEqual(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
    }
}
