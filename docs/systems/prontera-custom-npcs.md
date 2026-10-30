# Prontera custom NPCs

This is an owner-selected server customization. The healer restores HP and SP and applies Blessing and Increase AGI for ten minutes at the configured level (5 or 10, default 10). A stronger existing buff is retained. Player-cast Blessing and Increase AGI use twice the configured attribute values for their learned level.

The hunter offers one repeatable hunt selected by the player's base level when accepted. The objective is 30 kills of the selected monster; the tracker points to a field where it spawns. The NPC pays 30% of the base EXP needed at the current base level and 30% of the job EXP needed at the current job level when the task is delivered. At the job cap, no job EXP is paid. At base level 99, the server's existing overflow rule sends base EXP to Astral progression. The player may accept another hunt after delivery; an unfinished hunt remains the same when the player levels up.

Targets are selected from field spawn groups using the exported monster HP, its HP scale, estimated bonus-time EXP and the level-difference correction. This is an EXP-per-HP estimate, not a guarantee of the fastest target for every character or server configuration. Map access, defense, elements, travel, competing players and other modifiers can affect actual results.

Settings are read from `<DataDir>/custom-npcs.json`: `healer`, `hunter`, `buffLevel` and `job50`. NPC placement is loaded with the map, so changes to presence require a server restart. `job50` is reserved and has no effect until the separate job-level extension is implemented. The client hook announces its synthetic NPC and task rows before the server sends them; unmodified clients do not receive those rows.
