# Architecture

## Project boundaries

- `ColonyOptimizer.Core` contains the normalised domain model and persisted-plan types. It has no project dependencies.
- `ColonyOptimizer.GameData` depends on Core. It resolves a game-data root, follows `baseconfig/modInfo.json`, reads save data and timing, and normalises recognised game JSON.
- `ColonyOptimizer.Optimization` depends on Core. It has no WPF dependency and constructs the CP-SAT production model.
- `ColonyOptimizer.App` depends on the other three projects. It provides the WPF/MVVM interface, plan persistence, exports, settings, bounded JSON-lines logging, and an offline WebView2 visualisation surface.

Keeping game-data import and optimisation independent of WPF allows their behaviour to be exercised directly by `ColonyOptimizer.Tests`. The solution file is `ColonyOptimizer.slnx`; installer projects live separately under `installer`, and `scripts/Publish-Release.ps1` produces the distributable packages.

Dependencies point inwards towards Core. Core must remain independent of game-data acquisition, optimisation, WPF, and application infrastructure; `ColonyOptimizer.GameData` and `ColonyOptimizer.Optimization` must remain usable without App.

At runtime, the app loads game data into the Core model, applies plan and save selections, passes the resulting planning inputs to `ColonyOptimizer.Optimization`, then presents and persists the result. The visualisation receives a compact serialised projection of that result rather than direct access to the solver or game-data loader.

## Visualisation

The optimiser emits `ProductionFlow` records for allocated recipe inputs and outputs. The WPF view model converts them, job-block counts, and layout settings into a compact serialised node/link projection for the offline WebView2 page. Bundled D3 circular-Sankey and ELK layered renderers handle cyclic graphs. Balanced intermediate materials are connected directly, while source deficits and genuine surpluses retain item nodes. Each renderer reports success or failure to WPF using its rendered DOM counts. Contributor-facing smoke and layout-regression checks are documented in `CONTRIBUTING.md`.

## Data ingestion

The loader treats the manifest as authoritative and respects its integer ordering. It combines recipe, item, job, science, toolset, timing, growable, and generated-block data into the Core model. Unknown JSON fields create a diagnostic rather than stopping the import. Exact operations and observed upstream fields are recorded in `GAME_DATA_VALIDATION.md`.

The acquisition service checks common Steam locations, Steam library folders, and the standard Steam path on every ready drive. On first run it enumerates `world.sqlite3` files below each discovered `gamedata\savegames` directory. The selected save folder and last saved/opened plan path are persisted; the saved plan is applied after game data has been loaded on a subsequent launch. Direct folder and file selection remain available. The upstream GitHub source ZIP is cached in `%LOCALAPPDATA%\ColonyOptimizer\GameData\GitHub`.

## Timing and shifts

`GameTiming` derives real cycle seconds as `24 * 60 * 60 / GameTimeScale`. Its interval helper works across midnight, so sleep, night guards, daylight, and hostile-period overlap use loaded values rather than hard-coded vanilla constants. Current validated values belong in `GAME_DATA_VALIDATION.md`.

## Tools and worker capacity

`ColonyOptimizer.Optimization` derives recipe workload, shared worker capacity, and recurring tool replacement from the normalised job and tool data. The mathematical model is documented in `SOLVER_MODEL.md`.

## Persistence and diagnostics

Plans are JSON with a `.colonyplan` extension. Settings and downloaded data locations are distinct from plans. Technical exceptions are written as bounded JSONL files under `%LOCALAPPDATA%\ColonyOptimizer\Logs`; no unrelated filesystem data is collected.
