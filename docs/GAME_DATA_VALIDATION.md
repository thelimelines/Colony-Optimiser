# Game Data Validation

Validated against public `pipliz/ColonySurvival` commit `7a5121763f93d768599e5d04b5c74f6645670f50`.

This document records behaviour observed at that revision; it is not a declaration of a stable Colony Survival schema or compatibility promise for later revisions. When the validated commit changes, review these observations, the importer, and the automated assertions together.

## Validation basis

- **JSON and manifest inspection** establishes file counts, field names, configured values, and relationships between records.
- **Server-code inspection** establishes runtime semantics that cannot be inferred safely from configuration alone, including tool durability, crop-stage progression, and farm actions.
- **Automated behavioural checks** load the checked-out data and exercise importer and solver expectations described below.

## Observed schemas

- `baseconfig/modInfo.json` is an array containing a module definition. Its `jsonFiles` manifest registered 44 `addOrReplaceNPCRecipes` entries, 21 `generateBlocks` entries, `setToolsets`, `addScience`, `addNewTypes`, and related non-production files.
- The 44 NPC recipe files contain 294 recipe records. Representative recipe fields are `name`, `cooldown`, `requires`, `results`, `requiresScience`, `defaultLimit`, `defaultPriority`, and `sortWeight`.
- Item quantities default to one when `amount` is omitted. Optional returns use `isOptional: true` and chance-based returns use `chance`, with current vanilla examples using `0.95`.
- `toolsets.json` has nine toolsets with `key`, `usable`, and `useMultiplier`. Tool type behaviours use `id: tool`, `craftingspeed`, `durability`, optional `requiresItem`, and optional `scienceHint`. The server code at the validated commit converts `durability` from configured seconds to milliseconds, then decrements it by the effective tooled work time.
- `science.json` contains 97 records with `key` and optional `dependencies`.
- `server.json` stores timing in `Time`. Current values are `GameTimeScale=120`, daylight `4.5-19.5`, day guard `4-19`, night guard `17-8`, and sleep `19.3-4.5`. These derive a 720-second cycle and a 444-second standard work window from 04:30 to 19:18; the earlier end is game data, not a hard-coded planner adjustment.
- `generateblocks_guards.json` currently has 13 guard definitions. Guard behaviours expose `npcType`, `sleepType`, `cooldownShot`, `shootRequirements`, damage, and range.
- Mineable `types.json` entries expose `customData.minerIsMineable`, `customData.minerMiningTime`, and `onRemoveType`. The current data includes infinite iron at 60 seconds per ore and other infinite mineral sources at their declared times; these become miner recipes and independent miner job-capacity groups for each output resource.
- `areajobs` contains nine `simpleFarm` definitions: wheat, cabbage, barley, cotton, hemp, alkanet, wolfsbane, hollyhock, and flax. Their configured minimum fertility values are retained by the planner. The server's `FirstNightRandom` growable logic at the validated commit advances each crop stage on successive nights; wheat has three stages and therefore grows for two 720-second game cycles (24 real minutes at the current time scale), while the remaining vanilla simple crops have two stages and grow for one cycle. Final crop stages expose their normal `onRemove` drops, including wheat and its 25% straw return. `FarmAreaJob` harvests and replants the mature stage with a 1.2-1.8 second action delay; the planner uses the midpoint for work capacity.

## Automated checks

`ColonyOptimizer.Tests` loads the checked-out vanilla `gamedata` through the manifest and asserts parsed recipes, tools, guards, science, cycle timing, worker timing, crop stages and drops, fletcher crossbow bolts, and crossbow ammunition references. Required CI checks out the exact commit recorded above. A separate weekly and manually dispatchable compatibility workflow tests the latest upstream default branch without blocking ordinary pull requests or releases. The solver suite covers shared worker capacity, alternate recipes, forbidden recipes, tool durability replacement, science restrictions, chance output, external material, crop farms, and guard demand.

## Known unresolved mechanics

Simple crop farms, mineable sources, and temperate/taiga forestry are modelled from game rules verified at the validated commit. Gathering and any area jobs without a verified growth or action model remain explicit external requirements. Crop walking, storage distance, and traffic are represented only through the planner efficiency setting, rather than being fabricated as fixed production rates.
