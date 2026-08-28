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

The optimiser emits `ProductionFlow` records for allocated recipe inputs and outputs. The WPF view model converts the current result, job-block counts, and layout settings into one compact serialised node/link projection for the offline WebView2 page. Bundled D3 circular-Sankey and ELK layered renderers handle cyclic graphs. Balanced intermediate materials are connected directly, while source deficits and genuine surpluses retain item nodes. The Sankey renderer keeps its full iteration count for smaller graphs and reduces it only as graph size grows; node dragging is frame-throttled and updates only the moved node's incident links.

Layout controls coalesce direction and spacing changes for 180 milliseconds. When the timer expires, WPF persists the complete layout once and sends only updated layout options to the page, which rerenders its retained graph rather than rebuilding the production projection. The page sends render-complete or render-failed messages back to WPF so the interface can show progress; smoke mode additionally waits for the render promise and checks the rendered DOM. Tooltip labels, roles, and job-block names are added as text nodes rather than interpolated HTML.

## Data ingestion

The loader treats the manifest as authoritative and respects its integer ordering. It combines recipe, item, job, science, toolset, timing, growable, and generated-block data into the Core model. Unknown JSON fields create a diagnostic rather than stopping the import. The public-data client identifies itself with the current application assembly version when downloading from GitHub. Exact operations and observed upstream fields are recorded in `GAME_DATA_VALIDATION.md`.

The acquisition service checks common Steam locations, Steam library folders, and the standard Steam path on every ready drive. On first run it enumerates `world.sqlite3` files below each discovered `gamedata\savegames` directory. A linked save is opened read-only and its `colonygroups` records are exposed as import scopes; the combined scope remains the default for compatibility, while multiplayer players can select one current database row. The selected save folder, selected colony-group row identifier, and last saved/opened plan path are persisted. The importer refreshes and validates that identifier against the current save before it changes progression; if the selected row is no longer present or readable, it preserves progression and requires a new choice. Direct folder and file selection remain available. The upstream GitHub source ZIP is cached in `%LOCALAPPDATA%\ColonyOptimizer\GameData\GitHub`.

## Timing and shifts

`GameTiming` derives real cycle seconds as `24 * 60 * 60 / GameTimeScale`. Its interval helper works across midnight, so sleep, night guards, daylight, and hostile-period overlap use loaded values rather than hard-coded vanilla constants. Current validated values belong in `GAME_DATA_VALIDATION.md`.

## Tools and worker capacity

`ColonyOptimizer.Optimization` derives recipe workload, shared worker capacity, and recurring tool replacement from the normalised job and tool data. The mathematical model is documented in `SOLVER_MODEL.md`.

## Persistence and diagnostics

Plans are JSON with a `.colonyplan` extension. Unknown fields are ignored and saved plans contain the supported fields only. Settings and downloaded data locations are distinct from plans. Visualiser settings are loaded together under a startup guard, so loading cannot trigger a partial write; later layout changes are persisted together after the 180-millisecond debounce. Settings and diagnostic logging tolerate ordinary unreadable, locked, and access-denied filesystem failures, so they cannot obscure the operation that raised the original error. Technical exceptions are written as bounded JSONL files under `%LOCALAPPDATA%\ColonyOptimizer\Logs`; no unrelated filesystem data is collected.
