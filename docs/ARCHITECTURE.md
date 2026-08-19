# Architecture

`ColonyOptimizer.Core` contains the normalised model and persisted plan types. `ColonyOptimizer.GameData` resolves a game-data root, follows `baseconfig/modInfo.json`, reads timing, and normalises recognised game JSON. `ColonyOptimizer.Optimization` has no WPF dependency and constructs the CP-SAT production model. `ColonyOptimizer.App` is the WPF/MVVM UI, file persistence, exports, settings, bounded JSON-line logging, and an offline WebView2 visualisation surface.

## Visualisation

The optimiser emits `ProductionFlow` records for each allocated recipe input and output. The WPF view model serialises those records, the allocation-derived job-block count, and the node-layout settings into a compact node/link payload for the visualisation page. Its bundled D3 circular-Sankey renderer and ELK 0.12 layered node renderer both report DOM node/link counts back through WebView2 after each render. A non-empty optimisation is treated as a rendering failure if either count is zero. The node renderer uses ELK's Sugiyama-style `layered` algorithm with `RIGHT` or `DOWN` direction, polyline edge routing, configurable sibling-node spacing, and configurable layer spacing; ELK handles cycles during the initial layout. It renders recipe nodes in the application teal palette, directly connects balanced intermediate materials, and retains an item node only for a source deficit or genuine surplus. The visualisation owns pointer-capture pan, node drag, and non-passive wheel zoom handling. Edges remain direct when a node is moved, and their labels stay at the geometric centre of each arrow. The smoke check exercises graph navigation. Launch with `--visual-smoke` (or set `COLONY_OPTIMIZER_VISUAL_SMOKE_TEST=1`) to run the ten-wrought-iron-per-minute smoke optimisation with all progression enabled. This mode uses an isolated `%TEMP%` settings profile, validates installed-game icon assets plus both rendered graph DOMs, and exits with a nonzero code on failure. It writes a JSON completion marker to `%TEMP%\ColonyOptimizer\visual-smoke\result.json`, or to `COLONY_OPTIMIZER_SMOKE_RESULT_PATH` when supplied.

The automated test suite also parses `MainWindow.xaml` and locks the Planner, Defence, Sources, and Visualisation row layouts. This catches accidental changes to a tab's available height before release.

## Data ingestion

The loader treats the manifest as authoritative and respects its integer ordering. It currently consumes `addOrReplaceNPCRecipes`, `addOrReplacePlayerRecipes`, `setToolsets`, `addScience`, `addNewTypes`, `addOrOverrideGrowableTypes`, `addOrOverrideAreaJobs`, and `generateBlocks`. Mineable types with `minerIsMineable`, `minerMiningTime`, and `onRemoveType` are normalised as miner recipes. Simple farm area jobs are combined with growable stages and final block drops to create crop source recipes. Generated blocks map crafting blocks, miners, and guards to their job and toolset definitions. Unknown JSON fields create a diagnostic rather than stopping the import.

The acquisition service checks common Steam locations, Steam library folders, and the standard Steam path on every ready drive. On first run it enumerates `world.sqlite3` files below each discovered `gamedata\savegames` directory. The selected save folder and last saved/opened plan path are persisted; the saved plan is applied after game data has been loaded on a subsequent launch. Direct folder and file selection remain available. The upstream GitHub source ZIP is cached in `%LOCALAPPDATA%\ColonyOptimizer\GameData\GitHub`.

## Timing and shifts

`GameTiming` derives real cycle seconds as `24 * 60 * 60 / GameTimeScale`. Its interval helper works across midnight, so sleep, night guards, daylight, and hostile-period overlap use the loaded values rather than fixed vanilla constants. The default data currently yields 720 real seconds per full cycle and 444 worker-active seconds.

## Tools and worker capacity

Jobs inherit their toolset from generated block behaviour. For each toolset the optimiser selects the available usable tool with the largest parsed `craftingspeed`, combines it with the toolset `useMultiplier`, and applies it to each recipe cooldown. A stocked tool's durability is charged against that effective workload and its replacement rate joins the recurring material balance; the one-per-worker starter stock remains separately visible. A job capacity is worker-active milliseconds multiplied by efficiency and reduced by the requested headroom.

## Persistence and diagnostics

Plans are JSON with a `.colonyplan` extension. Settings and downloaded data locations are distinct from plans. Technical exceptions are written as bounded JSONL files under `%LOCALAPPDATA%\ColonyOptimizer\Logs`; no unrelated filesystem data is collected.
