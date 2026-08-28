# Contributing

Thank you for helping to improve Colony Optimiser. Bug reports, documentation corrections, tests, and focused code changes are all welcome.

## Prerequisites

- Windows 10 or 11
- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git
- A checked-out copy of the public [Colony Survival data](https://github.com/pipliz/ColonySurvival)

Clone the game data into the location used by the test suite:

```powershell
git clone https://github.com/pipliz/ColonySurvival.git work\ColonySurvival
git -C work\ColonySurvival checkout --detach 7a5121763f93d768599e5d04b5c74f6645670f50
```

The detached commit is the revision recorded in `docs/GAME_DATA_VALIDATION.md` and used by required CI. A separate weekly and manually dispatchable workflow checks the latest upstream default branch without blocking ordinary pull requests.

Alternatively, set `COLONY_SURVIVAL_GAMEDATA` to the `gamedata` directory in an existing checkout for the current terminal session:

```powershell
$env:COLONY_SURVIVAL_GAMEDATA = 'C:\path\to\ColonySurvival\gamedata'
```

The test suite reads this public data but does not modify it. Do not commit game data, save files, plans, packages, or build output.

## Repository guide

- `src/ColonyOptimizer.Core` contains shared domain and persisted-plan types.
- `src/ColonyOptimizer.GameData` imports public game data and reads saves.
- `src/ColonyOptimizer.Optimization` builds and solves the production model.
- `src/ColonyOptimizer.App` contains the WPF application and bundled visualisation.
- `tests/ColonyOptimizer.Tests` contains unit, integration, and layout-regression tests.
- `installer` and `scripts/Publish-Release.ps1` build release packages.
- `docs` explains the [architecture](docs/ARCHITECTURE.md), [solver](docs/SOLVER_MODEL.md), [validated game-data assumptions](docs/GAME_DATA_VALIDATION.md), and [release process](docs/RELEASING.md). [CHANGELOG.md](CHANGELOG.md) contains the curated notes published for each release.

The project uses nullable reference types and implicit global usings. Follow the style of the surrounding C# and XAML; keep changes focused and add or update tests when behaviour changes. Use British English in user-facing text and documentation. Keep code identifiers unchanged when they belong to an API, file format, upstream schema, or existing `ColonyOptimizer` namespace.

## Make a change

Create a branch from the latest `main` and make a focused change. Restoring packages requires an internet connection the first time:

```powershell
dotnet restore ColonyOptimizer.slnx --runtime win-x64 --locked-mode
dotnet restore installer\ColonyOptimizer.Installer\ColonyOptimizer.Installer.wixproj --locked-mode
dotnet restore installer\ColonyOptimizer.Setup\ColonyOptimizer.Setup.wixproj --locked-mode
```

These commands verify the committed NuGet dependency graph. Use `--force-evaluate` instead of `--locked-mode` only when intentionally updating dependencies, then review and commit the resulting lock-file changes with that update.

## Build and run locally

You do not need a separate WPF or game-development environment to run the app from source: Windows and the .NET 10 SDK are enough. From the repository root, restore once, then launch the Debug build:

```powershell
dotnet restore ColonyOptimizer.slnx --runtime win-x64 --locked-mode
dotnet run --project src\ColonyOptimizer.App\ColonyOptimizer.App.csproj
```

The first command requires internet access to obtain the locked NuGet packages if they are not already cached. The second command builds the WPF app when needed and opens Colony Optimiser. To launch the locally built Release configuration instead, run:

```powershell
dotnet run --project src\ColonyOptimizer.App\ColonyOptimizer.App.csproj -c Release --no-restore
```

On first launch, use the settings cog to select an installed Colony Survival `gamedata` folder or choose **Download latest public data**. A separate public game-data checkout is required for the automated tests below, but not to open and use the application. A Visual Studio user can open `ColonyOptimizer.slnx`, set `ColonyOptimizer.App` as the startup project, and use the same Debug/Release configurations.

## Testing

Run the automated tests and Release build from the repository root:

```powershell
dotnet test tests\ColonyOptimizer.Tests\ColonyOptimizer.Tests.csproj -c Release --no-restore
dotnet build src\ColonyOptimizer.App\ColonyOptimizer.App.csproj -c Release --no-restore
```

The test suite needs compatible Colony Survival data at one of the locations described under [Prerequisites](#prerequisites). It reports an error if the data cannot be found. In addition to solver and game-data tests, the suite parses `MainWindow.xaml` and protects the Planner, Defence, Sources, and Visualisation row layouts against accidental height regressions.

For a user-interface change, start the application and exercise the affected workflow manually. Include screenshots in the pull request for a visible interface change.

For a visualisation change, run the built application in smoke mode:

```powershell
dotnet run --project src\ColonyOptimizer.App\ColonyOptimizer.App.csproj -c Release --no-build -- --visual-smoke
```

Setting `COLONY_OPTIMIZER_VISUAL_SMOKE_TEST=1` before an ordinary launch enables the same mode. The smoke mode:

- uses an isolated settings profile under `%TEMP%`;
- runs a ten-wrought-iron-per-minute optimisation with all progression enabled;
- checks installed-game icon assets and verifies that both graph renderers produce DOM nodes and links;
- exercises graph navigation and zero-spacing node layout, then exits with a non-zero code if a check fails; and
- writes its JSON completion marker to `%TEMP%\ColonyOptimizer\visual-smoke\result.json` unless `COLONY_OPTIMIZER_SMOKE_RESULT_PATH` specifies another location.

When changing the smoke workflow or layout-regression assertions, update this section and the corresponding tests together.

For a release-packaging change, build the release assets and smoke-test every distributable before opening a pull request:

```powershell
.\scripts\Publish-Release.ps1 -Version <version>
.\scripts\Test-ReleasePackages.ps1 -ArtifactRoot artifacts -Version <version>
```

The package smoke test verifies the Setup UI can start without installing, checks the bundle's embedded MSI chain through layout mode, performs an administrative MSI deployment into a temporary folder, and checks required portable-ZIP files. It does not replace the manual clean Setup installation required before release.

## Submit a pull request

Before opening a pull request:

1. Check that generated files and private data are not included in the diff.
2. Describe the user-visible effect and the reason for the change.
3. Link any related issue and include reproducible steps for a bug fix.
4. Include screenshots for visible interface changes.
5. Confirm which checks you ran and explain any that could not be run.

GitHub Actions repeats the checks with a clean Colony Survival data checkout. Keep dependency lock-file changes only when they result from an intentional dependency update.

## Reports and security

Use the repository's [issue tracker](../../issues) for reproducible bugs and focused feature requests. Include the Colony Optimiser version, Windows version, expected result, actual result, and the shortest steps that demonstrate the problem. Do not attach Colony Survival saves or logs containing private paths unless a maintainer asks for them.

Report suspected vulnerabilities privately by following [SECURITY.md](SECURITY.md), not through a public issue.
