using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public Task AreaSelectionPreservesBoundsOrderAndMovedTargets() => Loop.InvokeAsync(() =>
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        var world = _app.Services.GetRequiredService<World>();
        var template = _app.Services.GetRequiredService<MonsterService>().Template(5000001)!;
        var s = new GameSession(null!, null!) { SceneEntry = 10540, InstanceId = 2992 };
        var map = world.Map(s.WorldScene);
        var group = new SpawnGroup(-1, false, 1, 0, [], []);
        var home = new SpawnPoint(-1, 0, 0, 0, 0, 5000001);
        MonsterUnit Add(long id, float x, float y, float z, long hp = 100)
        {
            var m = new MonsterUnit { Uid = id, SceneEntry = s.SceneEntry, InstanceId = s.InstanceId,
                Template = template, Group = group, Home = home, Hp = hp, X = x, Y = y, Z = z };
            map.Add(m);
            return m;
        }
        Add(20, 101, 2, 100); Add(10, 99, 2, 100); Add(30, 105.05f, 2, 100);
        Add(40, 105.2f, 2, 100); Add(50, 100, 3.1f, 100); Add(60, 100, 2, 100, hp: 0);
        var moved = Add(70, 200, 2, 200);
        Add(80, 100, 2, 100);
        combat.MonsterTargetAllowed = (_, m) => m.Uid != 80;
        var select = typeof(CombatService).GetMethod("SelectEnemies", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<GameSession, ActiveSkillCast, Vec3, float?, List<MonsterUnit>>>(combat);
        using var circle = JsonDocument.Parse("{\"_iRangeType\":2,\"_iRelease\":8,\"_kRange\":[500],\"_iTargetMax\":3,\"_iAreaHeightRange\":100,\"_iDmgType\":1}");
        var center = new Vec3 { X = 100, Y = 2, Z = 100 };
        var cast = new ActiveSkillCast { Id = 1, OwnerId = 1, WorldScene = s.WorldScene, Row = circle.RootElement,
            Request = new CastSkillReq { TargetPos = center }, Origin = center, StartMs = 0, ChantMs = 0, ActionMs = 1 };
        Assert.Equal(new long[] { 10, 20, 30 }, select(s, cast, center, null).Select(m => m.Uid));
        // A skill can move a target before its cell membership is refreshed.
        moved.X = 100; moved.Z = 100;
        Assert.Equal(new long[] { 70, 10, 20 }, select(s, cast, center, null).Select(m => m.Uid));
        return Task.CompletedTask;
    });

    [ClientDataFact]
    public Task RectangularAreaKeepsItsFullLength() => Loop.InvokeAsync(() =>
    {
        var combat = _app.Services.GetRequiredService<CombatService>();
        var world = _app.Services.GetRequiredService<World>();
        var template = _app.Services.GetRequiredService<MonsterService>().Template(5000001)!;
        var s = new GameSession(null!, null!) { SceneEntry = 10540, InstanceId = 2993 };
        world.Map(s.WorldScene).Add(new MonsterUnit { Uid = 1, SceneEntry = s.SceneEntry, InstanceId = s.InstanceId,
            Template = template, Group = new SpawnGroup(-1, false, 1, 0, [], []),
            Home = new SpawnPoint(-1, 0, 0, 0, 0, 5000001), Hp = 100, Z = 8 });
        combat.MonsterTargetAllowed = null;
        using var rect = JsonDocument.Parse("{\"_iRangeType\":2,\"_iRelease\":7,\"_kRange\":[200,2000],\"_iTargetMax\":3,\"_iDmgType\":1}");
        var center = new Vec3();
        var cast = new ActiveSkillCast { Id = 1, OwnerId = 1, WorldScene = s.WorldScene, Row = rect.RootElement,
            Request = new CastSkillReq { TargetPos = center, Face = 0 }, Origin = center, StartMs = 0, ChantMs = 0, ActionMs = 1 };
        var select = typeof(CombatService).GetMethod("SelectEnemies", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<GameSession, ActiveSkillCast, Vec3, float?, List<MonsterUnit>>>(combat);
        Assert.Equal(new long[] { 1 }, select(s, cast, center, null).Select(m => m.Uid));
        using var forward = JsonDocument.Parse("{\"_iRangeType\":2,\"_iRelease\":8,\"_kRange\":[200,2000],\"_iTargetMax\":3,\"_iDmgType\":1,\"_ro3GuardianForwardRectangle\":true}");
        var forwardCast = new ActiveSkillCast { Id = 2, OwnerId = 1, WorldScene = s.WorldScene, Row = forward.RootElement,
            Request = new CastSkillReq { TargetPos = center, Face = 0 }, Origin = center, StartMs = 0, ChantMs = 0, ActionMs = 1 };
        Assert.Equal(new long[] { 1 }, select(s, forwardCast, center, null).Select(m => m.Uid));
        using var circle = JsonDocument.Parse("{\"_iRangeType\":2,\"_iRelease\":8,\"_kRange\":[200],\"_iTargetMax\":3,\"_iDmgType\":1}");
        var circleCast = new ActiveSkillCast { Id = 3, OwnerId = 1, WorldScene = s.WorldScene, Row = circle.RootElement,
            Request = new CastSkillReq { TargetPos = center }, Origin = center, StartMs = 0, ChantMs = 0, ActionMs = 1 };
        Assert.Equal(new long[] { 1 }, select(s, circleCast, center, 10).Select(m => m.Uid));
        using var selfCircle = JsonDocument.Parse("{\"_iRangeType\":2,\"_iRelease\":2,\"_kRange\":[100],\"_iTargetMax\":3,\"_iDmgType\":1}");
        var origin = new Vec3 { Z = 8 };
        var selfCast = new ActiveSkillCast { Id = 4, OwnerId = 1, WorldScene = s.WorldScene, Row = selfCircle.RootElement,
            Request = new CastSkillReq(), Origin = origin, StartMs = 0, ChantMs = 0, ActionMs = 1 };
        Assert.Equal(new long[] { 1 }, select(s, selfCast, new Vec3 { X = 50, Z = 50 }, null).Select(m => m.Uid));
        return Task.CompletedTask;
    });
}
