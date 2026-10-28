using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// A Kafra is used by tapping it within NPCConfig _iInteractDistance (2 m, 3D) of the player, and the client puts it at the height the
/// server sends: every Kafra of every field map must stand on a walkable nav cell, at a height that cell really has.
/// </summary>
public sealed class KafraProbeTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ro3emu-kafraprobe-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        app = ServerHost.Create([], o =>
        {
            o.DataDir = directory; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await app.StopAsync(); await app.DisposeAsync(); SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    [ClientDataFact]
    public void EveryKafraStandsOnWalkableGround()
    {
        var world = app.Services.GetRequiredService<WorldData>();
        var bad = new StringBuilder();
        int checkedKafras = 0;
        for (long scene = 10000; scene < 10999; scene++)
        {
            if (world.Map(scene) is not { Nav: { } nav } map) continue;
            foreach (var n in map.Npcs.Where(n => n.Kind == WorldNpcKind.Kafra))
            {
                checkedKafras++;
                if (!nav.IsWalkable(n.X, n.Y, n.Z)) bad.AppendLine($"{scene} Kafra {n.NpcId} at ({n.X:0.0},{n.Y:0.00},{n.Z:0.0}) is not on walkable ground");
            }
        }
        Assert.True(checkedKafras > 50, "no Kafras were checked");
        Assert.True(bad.Length == 0, bad.ToString());
    }
}
