using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Casts every skill of a job family at its top level on the game wire and checks what the client is given to draw
/// (docs/systems/skill-visuals.md). Set RO3EMU_SKILL_AUDIT_OUT to a folder to also get a TSV report per family.
/// </summary>
public sealed partial class SkillCombatTests
{
    // Job tree (JobConfig._iParentId): a family is the first job after Novice 1000 and everything that evolves from it.
    private static readonly Dictionary<string, int[]> JobFamilies = new()
    {
        ["swordsman"] = [1100, 1200, 1210, 1300, 1310, 1400, 1410],
        ["mage"] = [2100, 2200, 2210, 2300, 2400],
        ["archer"] = [3100, 3200, 3210, 3211, 3300, 3400],
        ["acolyte"] = [4100, 4200, 4210, 4300, 4400],
        ["thief"] = [5100, 5200, 5210, 5300, 5400],
        ["merchant"] = [6100, 6200, 6300, 6400],
    };

    private sealed record AuditRow(int Skill, int Level, int Job, int Release, string Cast, string Bullets, string Hits,
        int Effects, string Verdict);

    [Trait("Category", "Slow")]
    [ClientDataFact]
    public Task MageSkillsDrawWhatTheServerSends() => AuditFamily("mage", 2400);

    [Trait("Category", "Slow")]
    [ClientDataFact]
    public Task SwordsmanSkillsDrawWhatTheServerSends() => AuditFamily("swordsman", 1400);

    [Trait("Category", "Slow")]
    [ClientDataFact]
    public Task ArcherSkillsDrawWhatTheServerSends() => AuditFamily("archer", 3400);

    [Trait("Category", "Slow")]
    [ClientDataFact]
    public Task AcolyteSkillsDrawWhatTheServerSends() => AuditFamily("acolyte", 4400);

    [Trait("Category", "Slow")]
    [ClientDataFact]
    public Task ThiefSkillsDrawWhatTheServerSends() => AuditFamily("thief", 5400);

    [Trait("Category", "Slow")]
    [ClientDataFact]
    public Task MerchantSkillsDrawWhatTheServerSends() => AuditFamily("merchant", 6400);

    private async Task AuditFamily(string family, int careerJob)
    {
        var data = app.Services.GetRequiredService<GameData>();
        var combat = app.Services.GetRequiredService<CombatService>();
        var jobs = JobFamilies[family].Append(1000).ToHashSet();
        // The highest level of every active skill group (mode 0 rows), once.
        var rows = data.Table("SkillConfig").Values
            .Where(r => jobs.Contains((int)GameData.Int(r, "_iJob")) && GameData.Int(r, "_iMode") == 0 && GameData.Int(r, "_iBasicType") == 1)
            .GroupBy(r => (int)GameData.Int(r, "_iSkillID"))
            .Select(g => g.OrderByDescending(r => GameData.Int(r, "_iLevel")).First())
            .OrderBy(r => GameData.Int(r, "_iSkillID")).ToList();

        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job " + careerJob); await Gm("alice", "!lvl 99"); await Gm("alice", "!hp 9999999 9999999");
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            bool ready = false;
            await Loop.InvokeAsync(() => { ready = Session("alice").Progress is { Level: 99 } p && p.Career == careerJob; return Task.CompletedTask; });
            if (ready) break;
            Assert.True(DateTime.UtcNow < until, "job/level not applied");
            await Task.Delay(50);
        }
        var monster = await Target("alice", stationary: true);

        var report = new List<AuditRow>();
        foreach (var row in rows)
        {
            int group = (int)GameData.Int(row, "_iSkillID"), level = (int)GameData.Int(row, "_iLevel"), id = (int)GameData.Int(row, "_iID");
            await Loop.InvokeAsync(() =>
            {
                var s = Session("alice");
                s.Progress!.Skills[group] = level;
                s.SkillCds.Clear(); s.SkillModeCds.Clear();
                monster.Hp = 1_000_000;
                s.Position.X = monster.X + 1.5f; s.Position.Y = monster.Y; s.Position.Z = monster.Z;
                return Task.CompletedTask;
            });
            int before = 0;
            string cast = "no reply";
            // The previous cast may still be in its action tail (busy) or its cooldown: wait it out.
            for (int attempt = 0; attempt < 30; attempt++)
            {
                before = alice.Inbox.Snapshot().Count;
                var req = new CastSkillReq { SkillId = id, TargetId = monster.Uid, TargetPos = new Vec3 { X = monster.X, Y = monster.Y, Z = monster.Z } };
                await alice.SendGameAsync(CombatService.UseSkill, req);
                try
                {
                    var rsp = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
                    cast = rsp.Result == 0 ? "ok" : "refused " + rsp.Result;
                    if (rsp.Result is not (ErrorCodes.SkillBusy or ErrorCodes.SkillCooldown)) break;
                    await Task.Delay(300);
                    await Loop.InvokeAsync(() => { Session("alice").SkillCds.Clear(); Session("alice").SkillModeCds.Clear(); return Task.CompletedTask; });
                }
                catch (TimeoutException) { cast = "no reply"; break; }
            }
            if (cast == "ok")
            {
                // Chant (up to 3 s), the action, projectile flight and late hits.
                long chant = GameData.Int(row, "_iChantFix") + GameData.Int(row, "_iChantVar"), action = GameData.Int(row, "_iActionTime");
                await Task.Delay((int)Math.Min(12000, chant + action + 1500));
            }
            var seen = alice.Inbox.Snapshot().Skip(before).ToList();
            var bullets = seen.Where(e => e.IsGame(CombatService.SkillBullet)).Select(e => SkillFixTimeBulletNotify.Parser.ParseFrom(e.Body))
                .Where(b => b.SkillConfigId == id).ToList();
            var hits = seen.Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body))
                .Where(h => h.SkillId == id).ToList();
            int effects = seen.Count(e => e.IsGame(CombatService.SkillEffect));
            report.Add(new AuditRow(id, level, (int)GameData.Int(row, "_iJob"), (int)GameData.Int(row, "_iRelease"), cast,
                string.Join(",", bullets.Select(b => $"{b.EffectId}x{b.Split}")),
                string.Join(",", hits.Select(h => $"{h.ShowPoint}x{h.Split}")), effects,
                Verdict(data, combat, row, cast, bullets, hits)));
        }

        string text = string.Join('\n', report.Select(r => string.Join('\t', r.Skill, r.Level, r.Job, r.Release, r.Cast, r.Bullets, r.Hits, r.Effects, r.Verdict)));
        if (Environment.GetEnvironmentVariable("RO3EMU_SKILL_AUDIT_OUT") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"skill-audit-{family}.tsv"),
                "skill\tlevel\tjob\trelease\tcast\tbullets\thits\teffects\tverdict\n" + text + "\n", new UTF8Encoding(false));
        }
        var broken = report.Where(r => r.Verdict.StartsWith("BAD")).ToList();
        Assert.True(broken.Count == 0, "Skills whose visuals break the client's rules:\n" + string.Join('\n', broken.Select(r => $"{r.Skill}: {r.Verdict}")));
    }

    /// <summary>The client's rules for what it can draw (see docs/systems/skill-visuals.md).</summary>
    private static string Verdict(GameData data, CombatService combat, JsonElement row, string cast,
        List<SkillFixTimeBulletNotify> bullets, List<SkillHitInfo> hits)
    {
        if (cast != "ok") return "skipped: " + cast;
        var visual = combat.VisualOf(row);
        var effects = data.SkillTemplateTable("Lua_Config_EffectData");
        var problems = new List<string>();
        foreach (var hit in hits)
        {
            if (hit.ShowPoint != 0 && (!effects.TryGetValue(hit.ShowPoint.ToString(), out var e)
                || !e.TryGetProperty("m_kDamage", out var d) || !d.TryGetProperty("m_kActionDamge", out _)))
                problems.Add($"hit showPoint {hit.ShowPoint} has no m_kActionDamge");
            if (hit.Dsts.Count > 0 && hit.Split != CombatService.HitSplit(row)) problems.Add($"hit split {hit.Split} != _kShowDmg {CombatService.HitSplit(row)}");
        }
        if (visual.BulletEffect != 0 && bullets.Count == 0 && hits.Count > 0) problems.Add($"projectile {visual.BulletEffect} not sent");
        if (visual.BulletEffect == 0 && bullets.Count > 0) problems.Add("projectile sent without a plan");
        foreach (var b in bullets)
            if (!effects.TryGetValue(b.EffectId.ToString(), out var e) || !e.TryGetProperty("m_kDamage", out var d) || !d.TryGetProperty("m_kBulletData", out _))
                problems.Add($"bullet {b.EffectId} has no m_kBulletData");
        return problems.Count == 0 ? (hits.Count == 0 && bullets.Count == 0 ? "quiet (no hit, no projectile)" : "ok") : "BAD " + string.Join("; ", problems);
    }
}
