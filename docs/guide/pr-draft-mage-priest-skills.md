# PR draft: Mage and Priest floor skills (NOT opened)

Status: prepared 2026-10-24, **not opened** on `aethai/ro3emu-server`. Branch `fix/mage-priest-floor-skills` is pushed to the
fork `juniorsk88/ro3emu-server` only (https://github.com/juniorsk88/ro3emu-server/tree/fix/mage-priest-floor-skills). The PR is opened only after the user says yes.

Base: `aethai/ro3emu-server` `main` at `69b2c2d` (merge of #96). Commits (one per skill, in this order):

1. `a5211c8` Spawn Magnus Exorcismus's cross and hit every 0.5 s for 4 s
2. `0f9df85` Make Sanctuary a 4 s blessing area that ticks every 0.6 s
3. `503c513` Make Gravitation Field last 3.9 s and strike every 0.5 s
4. `97d9388` Document the timed areas of Magnus Exorcismus, Sanctuary and Gravitation Field

---

## Title

Mage/Priest floor skills: Magnus Exorcismus, Sanctuary and Gravitation Field now last and tick as described

## Body

### What changes

Three skills whose description says "for the next N seconds ... every X seconds" were cast as a single instant hit, because
`CombatService.CreateSkillObjectAsync` only knew the scripts of Fire Wall, Quagmire, Fire Pillar, Meteor Storm, Lord of
Vermilion, Storm Gust, Safety Wall, Pneuma and the Hunter traps. For any other script `npc` stayed 0, the method returned
`false`, and the cast fell through to `ApplyEffectsAsync` once. Each commit adds the missing case and a test that fails on
`main` and passes with the change.

How I found them: I listed every skill of the Mage (2100), Wizard (2200), Sage (2210), High Wizard (2300), Warlock (2400),
Acolyte (4100), Priest (4200), Monk (4210), High Priest (4300) and Archbishop (4400) rows of `SkillConfig`, took the release
types that leave something on the floor (`_iRelease` 7 and 8), and compared each one's description (`Localization_en`),
`_kDescData` and `_kLogicData` with what the server did. Then I wrote the test the description implies and checked that it
fails on `main`. Cases I could not prove are listed under "Not changed".

### Case 1 - Magnus Exorcismus (14109, Priest line) - `a5211c8`

- **Symptom:** the cross never appeared on the floor and the skill hit each enemy once. Counted on a stationary target:
  1 hit in 5.5 s on `main`.
- **Cause:** `_kScriptLogic` is `Magnus_Exorcismus`, which has no case. Its `_kLogicData` is `[22000009, 4, 8, 10]`; NPC
  22000009 exists in `NPCConfig` (`_iNPCType` 4, unit 40010) and nothing referenced it.
- **Fix:** a `Magnus_Exorcismus` case: NPC = `_kLogicData[0]`, lifetime = `_kLogicData[1]` seconds ("For the next
  ${4} sec."), interval = `_kDescData[4]` seconds ("every ${5} sec."; 0.5). Targets per hit stay `_iTargetMax` (4). The cross
  uses the same owner/teardown rules as Fire Wall (despawns on expiry, death, map change, logout).
- **Test:** `MagnusExorcismusLeavesItsCrossOnTheFloorForFourSeconds` (the NPC enters another player's view at the cast point,
  interval 500 ms, about 4 s left, then leaves) and `MagnusExorcismusHitsEveryHalfSecondForFourSeconds` (7 to 9 hits on a
  monster inside). Level does not change the count: `_kDescData[3]` and `[4]` are the same on all 10 levels.
- **Inferred:** nothing in this case. The bonus of ${9}% (30) against Undead/Demon race and Shadow/Undead property is not
  implemented by this PR (see below).

### Case 2 - Sanctuary (14115, Priest line) - `0f9df85`

- **Symptom:** one heal instead of a heal every 0.6 s for 4 s; and any ally within 7 m of the target point (a circle, 14 m
  wide) was healed although the area is a 7 m x 7 m square. On `main`: 1 heal in 5.5 s; an ally 6 m from the centre was healed.
- **Cause:** script `Sanctuary`, no case, and `_kLogicData` is only `[4]` (no NPC exists for it). In the ally selection of
  `ApplyEffectsAsync` the radius was `_kRange[0] / 100` (7 m) around the point, and the caster was always counted.
- **Fix:** a `Sanctuary` case with duration `_kDescData[3]` (4 s) and interval `_kDescData[4]` (0.6 s). Without an NPC it is a
  timed area owned by the caster: `SkillObject.HasEntity` is false, so it is not added to the AOI, and its `SkillHit` names
  the caster as `SrcID`. The allies are taken with the same `SkillAreaContains` rectangle the enemies already use (release 7,
  `_kRange [700, 700]`), and the caster only counts when inside it. Up to `_iTargetMax` (4), nearest first. Damage to Undead and
  Demon monsters goes through the same enemy selection (race mask of the skill's `m_kSelectInfo`), now on every tick; I did
  not write a test for that part.
- **Test:** `SanctuaryHealsEverySixTenthsOfASecondForFourSeconds` (6 to 8 heals on the caster in 5.5 s; no field left) and
  `SanctuaryDoesNotHealAnAllyOutsideItsSquare`.
- **Inferred:** that the client draws the area from the skill itself and needs no NPC. The data has no NPC for Sanctuary and
  `_kLogicData` holds no NPC id, so the server sends no entity; I could not check the sprite in a client. Whether the first
  tick fires at cast or after one interval is not in the data (the other fields in the file fire at cast, so does this one).

### Case 3 - Gravitation Field (12212, High Wizard) - `503c513`

- **Symptom:** one hit instead of eight. On `main`: 1 hit in 5.5 s on a stationary target.
- **Cause:** script `ZhongLiYuanYe`, no case, and its `_kLogicData` `[500, 3900, 550]` has no NPC. The description: "The gravity
  field lasts ${6} seconds and strikes a total of ${7} times" (`_kDescData` 4 and 8).
- **Fix:** a `ZhongLiYuanYe` case that reads `_kLogicData[0]` (ms between waves) and `[1]` (ms it lasts) and uses the same
  timed area as Sanctuary: ticks at 0, 0.5, ... 3.5 s are the 8 strikes, the area ends at 3.9 s. The Immobilize of
  `_kBuff2` rides on each wave as for the other fields; the self buff of `_kBuff1` is applied once at cast.
- **Test:** `GravitationFieldStrikesEightTimes` (7 to 9 hits in 5.5 s, no field left).
- **Inferred:** same as Sanctuary, no NPC. `_kLogicData[2]` (550) is read as the damage multiplier of the description
  (5.5 x), not a radius, because it matches `${4}` and the other fields' tuples carry the multiplier there too.

### Docs - the fourth commit

`docs/systems/skills-rest.md` lists Magnus, and Sanctuary / Gravitation Field as timed areas without an AOI entity.
`docs/systems/skill-protocol.md` no longer says that Gravitation Field "resolves at once".

### Not changed (found but not proven, so only noted)

- **Magnus Exorcismus bonus:** the description raises the damage by `_kDescData[8]`% (30) on Undead/Demon race and Shadow/
  Undead property. I did not find where the server applies it and did not write a test; it may be missing *(inferred)*.
- **Heaven's Drive (12109, Wizard):** "dealing a total of ..." could mean several hits in the original; `_kLogicData` is empty
  and the description gives no count, so it stays one hit *(inferred)*.
- **Ember Zone (1244301):** the described 4 s / 0.5 s zone is a talent (`CreateZoneAsync` + NPC 1200021 in `TalentProcs`),
  already handled there.
- **Offertorium (1431501):** a periodic heal buff (BuffConfig 1432501), outside the floor-skill path; not audited.
- Elements: for every Mage/Priest skill whose description names an element, `_iEleType` matches (the only differences are
  Tetra Vortex, which has four, and passives).

### Evidence and limits

- All numbers above come from `data/config/SkillConfig.json`, `NPCConfig.json` and `Localization_en.json`; no client Lua
  was needed. The three tests are `[ClientDataFact]`, so CI skips them (no client export there); they ran on my machine with
  the export.
- I could not run a real client: how the client shows these areas is **(inferred)**.

### Validation

Run on a clone of `upstream/main` + these 4 commits, with the client export in `data/` (Windows, .NET SDK 10.0.402):

- `dotnet build src/Ro3Emu.Server/Ro3Emu.Server.csproj -c Release`: 0 errors (warnings are errors in this repo).
- `dotnet format whitespace --folder --verify-no-changes`: clean. `dotnet run --project tools/AnalyzerBaseline -c Release`:
  "matches the code". `LuaDecode` and `ConfigExport` build; `Ro3Emu.Patcher.Tests`: 19 passed.
- The 5 new tests are `[ClientDataFact]`: each one fails on `main` (Magnus 1 hit, Sanctuary 1 heal and an ally at 6 m healed,
  Gravitation 1 hit) and passes on the branch.
- Skills/combat/config group (`SkillCombatTests`, `SkillProtocolTests`, `SkillBuffTests`, `ConfigViewTests`, `TitleTests`,
  `CombatTableRulesTests`, `BuffRulesTests`, `SkillGrowthTests`, `CareerSpecialTests`): 198 passed, 1 failed on the branch;
  193 passed, the same 1 failed on `main` (`TitleTests.RanksAreClaimedInOrderAgainstLevelPowerAndCrests`, "expected 82, got 0":
  it passes when run alone and fails in the group, with or without this PR).
- `RO3EMU_NO_CLIENT_DATA=1 dotnet test tests/Ro3Emu.Tests --filter "Category!=Slow"` (what CI runs) was run before the last
  refactor: 852 passed, 1058 skipped, 1 failed, and that one was `ConfigViewTests.ClientFieldNamesLiveInTheConfigViews`
  (the field-name count rose by 3 because my first version wrote `_kDescData`/`_iRelease` as strings). I changed the code to
  read them through `SkillConfig` and `ConfigViewTests` now passes (it ran again with the final code, in the group above).
  **I did not repeat the whole CI-like run on the final commit.**
- Full run with client data (1981 tests) before the refactor: 1973 passed, 3 failed, then the test host aborted
  (`EconomyProbe: Output artifacts already exist; use a new prefix`, probably two runs of mine and another session's sharing
  the temp folder). The 3 failures: `ConfigViewTests` (fixed, above), `TitleTests` (same on `main`, above) and
  `FirstHeartTests.SyncStartsAtItsOpenLevelWithDayOneOpenAndAnInitializedRewardMap` (fails the same way on `main` with my
  export, probably a difference between my export and the one the test was written for).
- Not run: a real client or a server on a live machine.
