# Monster chase AI (`monster-ai.json`)

How monsters give up a chase, whether they walk around obstacles, and what happens when they kill a player. Two profiles:
**field** (every monster without special skills) and **boss** (bosses and MVPs, the monsters with special skills).

## Giving up a chase

A monster that has a target (the player who hit it, or the nearest one for an aggressive monster) chases it until the
target is lost: dead, loading, in another scene, **too far**, or **idle for too long**.

| Key | Values | Meaning |
|---|---|---|
| `giveUp` | `"player"` / `"home"` | `"player"`: the player is farther from the **monster** than the give-up distance. `"home"`: the player is farther from the monster's **spawn point** than that distance (the old leash). |
| `leash` | 0.1 to 20 | Multiplies the give-up distance of the monster's AIConfig (`_iChaseDistance`, mirrored by `_iUnFightParam[0]`; 18 m for most monsters). |
| `giveUpSeconds` | 0 to 3600 | Gives up when nothing happened between the monster and its target (no swing, no hit taken) for this long; 0 = never. The game's data carries 15 to 30 s (`_iUnFightType` 2, `_iUnFightParam[1]`). |
| `returnHome` | `true` / `false` | After giving up, walk back to the spawn point. `false`: stay where it gave up and roam around that spot (`MonsterUnit.RoamAnchor`). |
| `heal` | `true` / `false` | Recover all HP when it gives up (or when it reaches home, if it walks back). `false`: keep the HP it has. |
| `pathfinding` | `true` / `false` | Walk around obstacles (below). `false`: straight segments only; a monster behind a wall stands still. |

Defaults: **field** `player`, 1.0, 30 s, no return, no heal, pathfinding on; **boss** `home`, 1.0, 0 (never), return, heal, pathfinding on.

The defaults let a pack be pulled across the map and handed to another player at the HP it has, which is how monsters are
gathered for an area attack. Bosses keep their post so nobody can lead an MVP away and reset it.

## Walking around obstacles

When the straight segment toward the target (or home) is blocked on the navigation grid, `MonsterService.PathStep` asks
`NavGrid.FindPath` for a route: weighted A* (weight 2) over the 1 m cells, 8 neighbours, no corner cutting, steps of at most
2 m of height, at most 800 expanded cells, then smoothed so each waypoint is one straight `CanWalk` leg. The route is kept for
2.5 s (and re-planned when the target moved 2.5 m), a failed search is not repeated for 1 s, and the whole realm may start only
2 searches per game tick (`PathSearchesPerTick`), so a crowd of blocked monsters cannot stall a tick. A search that hits the cap
returns the route to the explored cell nearest to the goal.

## Kill progression ("evolves when it kills a player")

With `killGrowth` on, each player the monster kills (a normal attack or a special skill) makes it:

- level up (+1 level, with an effect on the monster). The character's own level-up sprite (`fx_levelup.prefab`) has no effect id
  in the data (the client plays it by name from the level-up panel). `Ro3Emu.ClientHooks.LevelUpEffect` adds an effect row of
  our own (id 990000001, a new id: no original row changes) that plays that prefab on the unit's DRoot for 3 s, and tells the
  server with game message 77017 (`GameSession.LevelUpFx`). A client without it (the original data, or a player who has not
  updated) is sent the stand-in effects instead (angel-blessing light 214390017 and the heal sparkle 1000301). Checked: the
  client log shows "effect 990000001 plays fx_levelup.prefab" after the real launcher flow; the sprite itself on a monster
  is not yet seen in a game session;
- stronger by `killStepPct` per kill (HP, attack, magic attack, defence, magic defence; the original template stays in
  `MonsterUnit.BaseTemplate`), up to `killMaxPct`;
- recover `killHealPct` of its (new) max HP;
- give more EXP and more items when it is killed, on the same scale: the bonus is `kills x step / cap` of `killExpMaxPct` and
  `killDropMaxPct` (half way to the cap, half of each);
- stay where it is: after a kill it neither walks home nor heals fully (`MonsterUnit.LastKillMs`).

The scale is `min(killMaxPct, kills x killStepPct)`. With the defaults (step 50, cap 500, EXP and drop 500) it takes ten kills:
50 %, 100 %, ... 500 % (six times the original), and from the cap on the monster only keeps recovering HP.

| Key | Default field / boss | Meaning |
|---|---|---|
| `killGrowth` | on / off | The feature. |
| `killStepPct` | 50 | Stat bonus each kill adds (0 to 500). |
| `killMaxPct` | 500 | Cap of the stat bonus (0 to 5000). |
| `killHealPct` | 20 | Percent of max HP recovered per kill (0 to 100). |
| `killExpMaxPct` | 500 | EXP bonus at the cap (0 to 5000). |
| `killDropMaxPct` | 500 | Item bonus at the cap (0 to 5000): the kill's drops are repeated like a map drop rate (`MapRates.ScaleItems`). |

## Where it is set

`<data>/monster-ai.json` (the `Ro3Emu__DataDir`), read again once per second: no restart. The admin panel's **Ajustes**
tab has a section "IA dos monstros e MVPs" that writes it and shows the progression table for the current values. A missing
or invalid file, key or value keeps the default of that key.

```json
{ "field": { "giveUp": "player", "leash": 1.0, "returnHome": false, "heal": false, "pathfinding": true, "giveUpSeconds": 30,
             "killGrowth": true, "killStepPct": 50, "killMaxPct": 500, "killHealPct": 20, "killExpMaxPct": 500, "killDropMaxPct": 500 },
  "boss":  { "giveUp": "home",   "leash": 1.0, "returnHome": true,  "heal": true,  "pathfinding": true, "giveUpSeconds": 0,
             "killGrowth": false } }
```

## Code and tests

`Game/MonsterAiSettings.cs` (profiles, file reading), `MonsterService` (`AiOf`, `TooFar`, `IdleTooLong`, `Disengage`, `ChaseStep`,
`PathStep`, `OnKilledPlayerAsync`), `Maps/NavGrid.FindPath`, `RewardService.ResolveKill` (EXP and drop bonus),
`CombatService` (calls `OnKilledPlayerAsync` after a monster kills a player).
Tests: `MonsterAiSettingsTests`, `NavPathTests` (synthetic grid), and in `SkillCombatTests`
`FieldMonsterKeepsChasingFarFromHomeAndStaysWhereItGaveUp`, `FieldMonsterGivesUpAfterTheConfiguredTimeWithoutCombat`,
`AMonsterThatKillsPlayersGrowsOnTheScaleUpToTheCap`.

## Known limits

- Aggro goes to the last player who hit the monster (no hate table).
- The evolved level is only shown through property 2002 (and the text notice).
