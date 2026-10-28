using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Diagnostics;

/// <summary>
/// Who is connected, as the game loop last saw it. Session state changes only on the loop, so the HTTP endpoints read
/// this snapshot, taken on the loop once a second, instead of the live sessions: a session's map is several fields
/// (scene, line, instance) that a reader on another thread could see half changed. Ported from upstream #38.
/// </summary>
public sealed class SessionCensus(SessionRegistry sessions) : IGameTicker
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private TimeSpan due;
    private volatile Snapshot current = Snapshot.Empty;

    /// <summary>The latest snapshot; empty until the loop's first tick.</summary>
    public Snapshot Current => current;

    public void Tick(GameTime time)
    {
        if (time.Now < due) return;
        due = time.Now + Interval;
        current = Take(sessions);
    }

    /// <summary>Counts the registered sessions; call it on the loop (or where nothing else changes them).</summary>
    public static Snapshot Take(SessionRegistry sessions)
    {
        int login = 0;
        List<Placement> roles = [];
        foreach (var session in sessions.All())
        {
            if (session.Role == null) login++;
            else roles.Add(new Placement(session.RuntimeMap, session.SceneLoading));
        }
        return new Snapshot(DateTimeOffset.UtcNow, login, roles);
    }

    /// <param name="CapturedUtc">When the loop took it; null before the first tick.</param>
    /// <param name="Login">Sessions without a selected role (role screen).</param>
    /// <param name="Roles">One entry per session with a selected role, loading or in the world.</param>
    public sealed record Snapshot(DateTimeOffset? CapturedUtc, int Login, IReadOnlyList<Placement> Roles)
    {
        public static readonly Snapshot Empty = new(null, 0, []);
    }

    public readonly record struct Placement(RuntimeMapKey Map, bool Loading);
}
