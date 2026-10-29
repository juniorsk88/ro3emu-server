# Soul Echo resonance combat effects

`EmblemMarkConfig` stages unlock at their configured counts of lit marks on the active plan.
`Lua_CFG_EmblemMarkConfig.GetAttrWithMarkAndNum` collects all reached stages, and the special effects select
`SkillConfig` rows for the character's job line. The server reads those rows on each eligible cast; changing
or upgrading an Echo, or switching plans, also refreshes the client's skill parameters.

Implemented multiplier portions are Dragon's Descent (`11130101`, Rift Impact), Pixel Thrust (`11130501`,
Phantom Thrust), Electro Rock (`11130701`, Rain of Spears), Mixed Accompaniment (`11230701`, Tetra Vortex),
Disco Accent (`11230801`, Comet), Moonlit Echo (`11164101`, Crescent Moon), Radiant Bloom (`11164201`,
Twilight), Chorus (`11430801`, Radiant Holy Light healing), Thomas Flare (`11531901`, Rolling Cutter),
Graffiti Mastery (`11531801`, Cross Ripper Slasher), Freestyle Rap (`11533101`, three shadow skills), and
Rhythmic Arrow (`11336001`, Focused Arrow Strike), and Golden Moon Ring (`11164301`, Blood Cross).
Values come from each effect's `_kDescData`; enhanced
`SkillConfig._kDamageParam1` rows corroborate most of them. Graffiti Mastery's component additions come from
those rows. An already enhanced mode is not enhanced again. Focused Arrow's modes have the same original
first ratio; only that ratio receives its bonus. Electro Rock describes all Rain of Spears forms. Dragon's
Descent also uses the enhanced Rift Impact row's Stun tuple, through the normal hostile-buff path.
Moonlit Echo sets the Crescent Moon Over Brand duration to the six seconds in its description and enhanced row.
Golden Moon Ring's second-form multiplier difference comes from its corresponding enhanced row.
Thomas Flare and Freestyle Rap also take the corresponding enhanced row's area radius and publish the
range vector through the client's `SkillRange` parameter (36, `LC_DynamicParamMgr`). Rolling Cutter is
selected as an area skill despite `_iRangeType` 1: its description and `_iTargetMax` specify multiple
enemies within the radius.
Battle Chant Harmony - Psalm (`11430601`) makes Highness Heal shield the caster and targeted ally for the
configured duration and MATK percentage, with the configured five-second shield cooldown. The underlying
heal also restores both, as its own description says. The cooldown is role-session state (inferred reset on relog);
an independently selected mode-1 Highness Heal remains outside this implementation.
The match's replacement build does not receive the player's Echo effects.

These are partial implementations, not the complete seasonal skill system. In particular, Pixel Thrust's
conditional cooldown reset, Mixed Accompaniment's stacks, Golden Moon Ring's conditional slow, and Thomas Flare's movement effect are not implemented
here. The other stage-2 and stage-3 skills remain unsupported. Tetra Vortex's separate hit
execution is limited by the existing combat model; applying its ratio to the configured vector does not by
itself implement all of its hits.

Tetra Vortex's Chinese description (`Localization_zh_CN` 10110300923) describes a total multiplier across
sequential Water, Fire, Earth and Wind attacks; its `SkillConfig` has four equal damage parameters, while
`skills/skill_12316.json` contains six animation attack points. Those sources alone do not establish how the
total is divided among server hits or how the points map to the four elements. Do not sum four parameters or
assign a hit schedule based solely on the animation.

Tetra Vortex and its Reading copy (`SkillConfig` 12316/12326) select hostile players within the configured
circle around the chosen point, like monster targets. Both configured base rows and their mode-1 versions
corroborate the same Mixed Accompaniment multiplier increase at all five levels. The extra cast itself is not yet
implemented, and the generic extra-cast path does not apply these Echo modifiers. Mixed Accompaniment describes three Elemental Attunement
stacks after casting, and `BuffConfig` 1234001 caps them at five. Its enhanced skill rows have a second damage
component and changed status chances, but the available data does not establish stack consumption, the lifetime
of the `-1` buff tuple, or separate-hit execution. Those stack effects remain unsupported.

All four Tetra Vortex variants have four 50% hostile buff tuples and a fifth, 100% Frozen tuple. The generic
monster path currently tries every tuple, including that last one; the original server's use of it is not known.
The player path does not yet apply these Tetra Vortex status tuples.
