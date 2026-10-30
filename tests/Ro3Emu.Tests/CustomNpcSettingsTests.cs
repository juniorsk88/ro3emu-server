using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class CustomNpcSettingsTests
{
    [Fact]
    public void ReadsOwnerChoicesAndFailsClosedForDamagedSettings()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ro3emu-npc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new CustomNpcSettings(Options.Create(new ServerOptions { DataDir = dir }));
            Assert.Equal(new CustomNpcSettings.Snapshot(true, true, 10, false), settings.Read());
            File.WriteAllText(Path.Combine(dir, "custom-npcs.json"),
                "{\"healer\":false,\"hunter\":true,\"buffLevel\":5,\"job50\":true}");
            Assert.Equal(new CustomNpcSettings.Snapshot(false, true, 5, true), settings.Read());
            File.WriteAllText(Path.Combine(dir, "custom-npcs.json"), "{");
            Assert.Equal(new CustomNpcSettings.Snapshot(false, false, 10, false), settings.Read());
        }
        finally { Directory.Delete(dir, true); }
    }
}
