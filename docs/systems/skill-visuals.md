# Skill visuals: what the client draws and what the server must send

This page records how the client turns a cast into effects, which part the server owns, and the rules `CombatService.Visuals.cs` derives from that. Citations name the decompiled client module and function (`re/clientB/lua-src/...`); anything the client cannot prove is marked *(inferred)*.

## Three sources of visuals

| Visual | Who starts it | Data |
| --- | --- | --- |
| Cast: animation, chant glow, sound | The client, from the skill id in 31102 / 31100 | `Config/Skill/skill_<scriptShow>.lua`: `m_kShows[corKey] = [[effectId, type], ...]`, `m_kPoints`, `m_kAtkPoints` |
| Projectile | The server (31120 `SkillFixTimeBulletNotify`), or the client when the timeline has `m_kClientPoints` | `Lua_Config_EffectData[id].m_kDamage.m_kBulletData`, `m_kDamageType = 2` |
| Impact on the target: flinch, hit gfx, hit sound | The server, through `SkillHit.showPoint` (31101) | `Lua_Config_EffectData[id].m_kDamage.m_kActionDamge`, `m_kDamageType = 3` |

`enum_DamageType` is `Trigger 1, Bullet 2, Action 3, PetShow 6` (`Config/Define/Lua_GameDefine`). `Lua_Config_EffectData` ids of one skill sit in `[_iScriptShow * 100, _iScriptShow * 100 + 99]`; `_kScriptLogic` (`Fire_Bolt`, `FireBall`, ...) is original-server script and the client never reads it.

## Evidence

- **Only two callers create a projectile.** `LC_PerformEffect_Agent.SkillCreateFixTimeBullet` is called by `LC_Scene_Sync._skillFixTimeBullet` (the 31120 handler) and by `LC_Skill_RunTimeContext.PlayClientEffect` (timeline `m_kClientPoints`). `LC_Bullet_Mgr.DisplayBullet` has no other caller. In the exported data 19 of 2219 timelines have `m_kClientPoints` (36 of 292 projectile effects); 198 of 211 projectile skills have none, so the server must send the projectile. `SkillCreateFixTimeBullet` also rejects an effect whose `m_kDamageType` is not `Bullet`.
- **A hit is drawn only for a type-3 `showPoint`.** `LC_DamageHelper.CalcDamageHitPoint` reads `m_kActionDamge` of the `showPoint` effect; a projectile or anim-group effect there draws no hit. Taking the first `m_kDamage` of the range returns the projectile for 125 of 1842 `_iScriptShow` values (Lightning Bolt 12005, Cold Bolt 12006, Fire Ball 12012, ...).
- **An empty `m_kBulletName` draws nothing.** `LC_Bullet.BulletStart` passes the name to `Lua_RD_Utils.AllocGfx`. Fire Ball has `1201208` (empty name, sound only) and `1201210` (`fx_skill_fireball_1.prefab`). 18 projectile effects have an empty name.
- **`fixedTime` is not read.** Flight time comes from `LC_Bullet_Helper`: `m_iBulletControlType` 0 uses `m_fBulletLifeTime` ms, 1 uses distance / `m_fBulletSpeed`; the result is at least 0.12 s and the time already elapsed since `startTimestampMs` is subtracted.
- **One 31120 draws `split` projectiles.** `LC_Bullet_Mgr` stores `split` (>= 1) as `m_iSplit`; `LC_BulletEmit_Mgr.Update` creates one every `m_fBulletHatchInterval` ms until `m_iSplit` is reached.
- **`sourceId` selects the start point.** `LC_Bullet_Helper` (start position): with `sourceId = 0` the start is `sourcePosition` (if non-zero); otherwise it is the source unit's `m_kSourceBindName` bone and `sourcePosition` is unused. The `Sag` track (`m_eBulletType = 2`; three effects, two with `m_kPosOffset.y = 30`) adds the offset to that point, so it drops onto the target when `sourceId = 0` and `sourcePosition` is the target *(inferred: the client allows both, the original server's choice is not visible)*.
- **`SkillHit.split` divides the damage.** `LC_DamageHelper._ActionJumpDamage` reads `split`; the number routine (`ProcessDamageWord`) shows `damage // split` (remainder spread over the first numbers) and, with `m_isPlayShow_Multistage`, repeats the hit effect every `m_fDisplayInterval` seconds. One `SkillHit` with `split = N` shows N numbers and N impacts.
- **`SkillConfig._kShowDmg[0]` is that N.** Fire Bolt levels 1..10 are `[1]..[10]`; Fire Ball is `{}`. The client does not read it *(inferred: it matches the arrow count the client expects per level)*.

- **Original-game check (video, not client code).** A closed-beta gameplay video (DMC Gaming, `4lVJf3h0BaI`, mage chapter from 0:17) shows the Fire Bolt Lv.10 tooltip "CT: 1.0s (0.0s+1.0s), Cooldown: 0.5s, Multi-hit" (`_iChantVar 1000`, `_iCD 500`, `_kShowDmg [10]`), a flaming arrow falling from the sky and a row of equal small damage numbers about 0.4 s after the cast bubble. The cast time is the configured one; the ChantSpeed term of the client's tooltip formula is not visible there, so the server applies none.

## Server rules

`SkillVisual.Build(data, scriptShow)` is a pure function of the exported data:

1. Collect type-3 effects (`m_kActionDamge`) and type-2 effects (`m_kBulletData`) in the range.
2. Projectile: none when the timeline has `m_kClientPoints`; otherwise the first projectile with a non-empty prefab name, else the first.
3. `ShowPoint`: the first type-3 effect after the chosen projectile id (1200503 -> 1200504), else the first type-3 effect; with none, the generic hit `1000002`.
4. A cast sends one 31120 with `split = HitSplit(row)` and `startTimestampMs = now`, then waits `FlightMs` before applying damage so the numbers land with the projectile. `SkillHit.split` uses the same `HitSplit`.
5. `bullet-timing.json` accepts only `{"sendBullet": true, "extraDelayMs": 0}`.
6. **No-damage skills (buffs, cleanses, debuffs).** Blessing (`fx_buff_angelblessing`), Increase AGI (`fx_buff_increaseagi`), Angelus, Gloria, Kyrie Eleison, Suffragium, Magnificat, Impositio Manus, Assumptio, Auto Guard, Adrenaline Rush, Cart Boost, Crazy Uproar and the other ally skills keep their glow in the type-3 hit effect (`1400102`, `1401004`, ...), the timeline only plays the cast animation and sound, and the client draws a hit effect only for a `SkillHit` dst. With no damage there was no dst, so nothing showed. The server now sends one `SkillHit` (`showPoint` = the skill's hit effect) naming every ally that received the skill (caster included) and, for hostile skills, the affected monsters, with `damageType = DamageShare`, result Normal, damage 0. `LC_DamageHelper.ProcessDamageWord` maps that to hud word type `None` and returns before drawing a number, so only the effect plays. Rules: only when the skill has its own hit effect with a `m_kGfxList` (`SkillVisual.HitHasGfx`; the generic hit is never drawn on allies), never for a field/zone tick, and statistics are not recorded. Checked over the 33 classes of the Novice tree, over the other `SkillConfig` skills that carry a class (`_iJob` set, not in the tree: 103 no-damage skills with a hit gfx, mostly passives, notes and the music/rhythm variants) and over those with no class (208: monster, GVG, event and item skills): no timeline already plays its hit effect, so nothing is drawn twice. Sage, Monk, Bard, Dancer and Rogue are in `JobConfig` but own no skill in `SkillConfig` or the tree. Hostile no-damage skills (Slow, Quagmire) also flinch their targets when the effect has `m_kAni` *(inferred: the original server's choice is not visible)*.

The pairing and variant choices in 2 and 3 are *(inferred)*: the range holds variants whose use the client does not state (Fire Ball: `1201208/09` and `1201210/11`).

## Not covered

Projectiles of monster, boss and pet skills (they only get the corrected `ShowPoint`), ground effects that are server NPCs (Fire Wall), and the effects a buff draws by itself while it lasts (`BuffEffect`, played by the client when the buff is added). 31119 (`SkillEffectDisplay`) stays unused: the timeline already plays those effects.

## Tests

`SkillVisualTests` checks the selection rules on a trimmed copy of the real rows (Fire Bolt, Lightning Bolt, Fire Ball, a timeline with `m_kClientPoints`, a range without a hit effect), `HitSplit` and `FlightMs`. It needs no exported game data.
