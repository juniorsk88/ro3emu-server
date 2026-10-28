using System.Text.Json;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Buffs that outlive a logout. BuffConfig._iOffline 2 marks them (food, honing stones, glyphs and armor plates last two hours, the dungeon
/// matchmaking penalties minutes): they keep counting down while the role is away, so the role comes back with what is left. (inferred: the
/// data has the flag but no text; 1 is not applied, it belongs to boss and event buffs.) The state is a small file per role next to the
/// database (DataDir/buffs/{role}.json), written when the role leaves the world and removed when it is read back.
/// </summary>
public sealed partial class BuffService
{
    private const int KeepsCountingOffline = 2;

    private sealed record SavedBuff(int Id, long LeftMs, long Source, int Pile);

    private sealed record SavedBuffs(long SavedAtMs, List<SavedBuff> Buffs);

    private string? BuffFile(long role) => _options.Value.DataDir is { Length: > 0 } dir ? Path.Combine(dir, "buffs", role + ".json") : null;

    /// <summary>Called as the role leaves the world, while its buffs are still on the session.</summary>
    public Task SaveOfflineAsync(GameSession p)
    {
        if (p.Role == null || BuffFile(p.Role.Id) is not { } file) return Task.CompletedTask;
        long now = MonsterService.NowMs;
        var keep = p.Buffs.Where(b => b.ExpireMs > now + 1000 && !b.IsPassive && !b.IsTotemOwned && !b.IsGuardianOwned
                && _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iOffline") == KeepsCountingOffline)
            .Select(b => new SavedBuff(b.ConfigId, b.ExpireMs - now, b.SourceId, b.PileCount)).ToList();
        try
        {
            if (keep.Count == 0) { File.Delete(file); return Task.CompletedTask; }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new SavedBuffs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), keep)));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Could not save the offline buffs of role {Role}", p.Role.Id);
        }
        return Task.CompletedTask;
    }

    /// <summary>Called at login: the saved buffs return with the time they had left minus the time the role was away.</summary>
    public async Task RestoreOfflineAsync(GameSession p)
    {
        if (p.Role == null || BuffFile(p.Role.Id) is not { } file || !File.Exists(file)) return;
        SavedBuffs? saved;
        try { saved = JsonSerializer.Deserialize<SavedBuffs>(File.ReadAllText(file)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(e, "Could not read the offline buffs of role {Role}", p.Role.Id);
            return;
        }
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.LogDebug(e, "Offline buff file of role {Role} stays", p.Role.Id); }
        if (saved == null) return;
        long away = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - saved.SavedAtMs);
        foreach (var buff in saved.Buffs)
        {
            long left = buff.LeftMs - away;
            if (left <= 1000 || left > int.MaxValue) continue;
            long source = buff.Source == p.Role.Id ? buff.Source : p.Role.Id;
            await AddCoreAsync(p, buff.Id, (int)left, source, Math.Max(1, buff.Pile));
        }
    }
}
