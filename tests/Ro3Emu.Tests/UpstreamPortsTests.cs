using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game.Messaging;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Fixes ported by hand from the original repository (aethai/ro3emu-server), documented in docs/guide/fixes-log.md. They need
/// no client data except where marked, so they also run on the synthetic fixtures.
/// </summary>
public sealed class UpstreamPortsTests
{
    private static GameSession Session(string? account, bool role)
    {
        var s = new GameSession(null!, null!) { Account = account };
        if (role) s.Role = new Role(1, "a", 1, "role", 1, 1000, 1, 1, 0, 0, 10100, 0, 0, 0, 0);
        return s;
    }

    /// <summary>#36: below the stage a message is declared for it is refused; this fork's 77000..77099 hook messages never are.</summary>
    [Fact]
    public void AGameMessageIsAcceptedOnlyFromItsStage()
    {
        var routes = new MessageRoutes();
        routes.AllowFrom(SessionStage.Connected, id => id == MsgId.Heartbeat);
        routes.AllowFrom(SessionStage.RoleScreen, id => id is MsgId.LoginRoleCreate or MsgId.LoginRoleSelect or MsgId.UploadClientSdkInfo);
        routes.AllowFrom(SessionStage.Connected, id => id is >= 77000 and <= 77099);
        var anonymous = Session(null, false);
        var roleScreen = Session("alice", false);
        var world = Session("alice", true);
        Assert.True(routes.Accepts(anonymous, MsgId.Heartbeat));
        Assert.False(routes.Accepts(anonymous, MsgId.LoginRoleCreate));
        Assert.True(routes.Accepts(anonymous, 77017));
        Assert.False(routes.Accepts(anonymous, 31102));
        Assert.True(routes.Accepts(roleScreen, MsgId.LoginRoleCreate));
        Assert.True(routes.Accepts(roleScreen, MsgId.UploadClientSdkInfo));
        Assert.False(routes.Accepts(roleScreen, 31102));
        Assert.False(routes.Accepts(roleScreen, 123456)); // an id nobody claims needs a role
        Assert.True(routes.Accepts(world, 31102));
        Assert.True(routes.Accepts(world, 123456));
    }

    /// <summary>The stage gate as the real dispatcher declares it: the level-up hello and the login messages pass early.</summary>
    [Fact]
    public async Task TheDispatcherDeclaresTheLoginMessagesAndTheHookMessagesAsEarly()
    {
        await using var host = await SyntheticHost.StartAsync();
        var routes = host.Service<GameMessageHandler>().Routes;
        Assert.Equal(SessionStage.Connected, routes.RequiredStage(MsgId.Heartbeat));
        Assert.Equal(SessionStage.Connected, routes.RequiredStage(MonsterService.LevelUpFxHello));
        Assert.Equal(SessionStage.RoleScreen, routes.RequiredStage(MsgId.LoginRoleSelect));
        Assert.Equal(SessionStage.World, routes.RequiredStage(MsgId.Walk));
        Assert.Equal(SessionStage.World, routes.RequiredStage(CombatService.UseSkill));
    }

    /// <summary>#86: a Kafra arrival inside scenery is moved onto walkable ground; a walkable point stays where it is.</summary>
    [ClientDataFact]
    public void EveryKafraArrivalIsWalkable()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") });
        var world = new WorldData(options, new GameData(options, NullLogger<GameData>.Instance), NullLogger<WorldData>.Instance);
        var path = DataFiles.Find(Path.Combine(ClientData.RepoRoot, "data", "sceneeditor"), "MapLinkData.json")!;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var destinations = doc.RootElement.GetProperty("Capra").EnumerateObject()
            .Select(p => long.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var stuck = new List<long>();
        foreach (long scene in destinations)
            if (world.Map(scene) is { Nav: not null, KafraArrival: { } arrival } map && !map.IsWalkable(arrival.X, arrival.Y, arrival.Z))
                stuck.Add(scene);
        Assert.Empty(stuck);
        var born = destinations.Select(world.Map).First(m => m?.Nav != null && m.Born is { } b && m.IsWalkable(b.X, b.Y, b.Z))!;
        Assert.Equal(born.Born!.Value, born.Standable(born.Born.Value));
    }

    /// <summary>#91: filling the missing medicine slots works on a copy and never changes what was saved.</summary>
    [Fact]
    public void FillingMissingMedicineSlotsDoesNotMutateSavedPreferences()
    {
        var saved = new MedicinesAutoUseSet { HpConfig = new MedicinesAutoUse { IsOpen = false, Condition = 42, ItemEntrys = { 1000000 } } };
        var before = saved.ToByteArray();
        var normalized = CharacterService.WithMedicineDefaults(saved);
        Assert.Equal(before, saved.ToByteArray());
        Assert.False(normalized.HpConfig.IsOpen);
        Assert.Equal(42, normalized.HpConfig.Condition);
        Assert.Equal([1000000, 0, 0], normalized.HpConfig.ItemEntrys);
        Assert.Equal(60, normalized.SpConfig.Condition);
        Assert.Equal([0, 0, 0], normalized.SpConfig.ItemEntrys);
        normalized.HpConfig.ItemEntrys[0] = 0;
        Assert.Equal(1000000, saved.HpConfig.ItemEntrys[0]);
    }

    /// <summary>#78: 5009 always carries the six slots of the shortcut bar, in order, 0 for an empty one.</summary>
    [Fact]
    public void TheShortcutBarIsAlwaysSentWithAllSixSlots()
    {
        var bar = CharacterService.FullShortcutBar([new ShortcutBarInfo { SlotIndex = 5, ItemGuid = 77 }, new ShortcutBarInfo { SlotIndex = 9, ItemGuid = 88 }]).ToArray();
        Assert.Equal(Enumerable.Range(0, 6), bar.Select(s => s.SlotIndex));
        Assert.Equal([0L, 0, 0, 0, 0, 77], bar.Select(s => s.ItemGuid));
    }
}
