# Colony Optimiser

Colony Optimiser is a Windows desktop planner for [Colony Survival](https://github.com/pipliz/ColonySurvival). Tell it what you want to produce and it calculates the jobs, ingredients, tools, area jobs, and defence ammunition needed for one production cycle.

It is an independent community project and is not affiliated with Pipliz.

## Download and install

1. Open this repository's [Releases](../../releases) page.
2. Download `ColonyOptimizer-<version>-Setup.exe` for the normal Windows installation. It installs under Program Files and is listed in **Installed apps** for standard uninstallation.
3. Alternatively, download `ColonyOptimizer-<version>-win-x64.msi` for managed or scripted deployment, or `ColonyOptimizer-<version>-win-x64.zip` for a portable copy.
4. Download the matching `.sha256` file for the chosen asset. Do not download the automatically generated `Source code` archives.
5. Run the setup program or MSI. For the portable ZIP, extract it somewhere you can write to and run `ColonyOptimizer.exe`.

The download is self-contained for 64-bit Windows 10 or 11. It does not need .NET, Python, or Steam to be installed before it starts. SmartScreen can be cautious about new unsigned applications; only run a copy obtained from this repository's Releases page and whose checksum matches the accompanying file.

### Check the download

In the folder containing the download, right-click and choose **Open in Terminal**, then run:

```powershell
Get-FileHash .\ColonyOptimizer-<version>-Setup.exe -Algorithm SHA256
```

Compare the displayed hash with the matching `.sha256` file from the same release. They must be identical. Substitute the MSI or ZIP filename if you chose that asset.

## First use

On first start, Colony Optimiser searches the usual Steam library locations for Colony Survival saves and lets you choose a world. You can skip this and use the planner without linking a save.

To obtain recipe data, open the settings cog and either select your installed Colony Survival game-data folder or use the in-app public-data download. The app reads a selected `world.sqlite3` save to preselect completed sciences and available tools. It never changes your save file or your game installation.

To create a plan:

1. In **Planner**, choose an output, enter an amount and unit, then add it to the plan.
2. Set sciences, tools, area-job capacity, defence, and recipe choices as required.
3. Select **Optimise**.
4. Review jobs, inputs, tools, and total outputs in **Results**. The **Visualisation** tab provides an interactive Sankey and node graph; drag empty space to pan, drag nodes to arrange them, and use the mouse wheel to zoom. In **Node visualiser**, choose a rightward or downward layout and tune node and layer spacing. Recipe nodes show their required job-block count; balanced intermediate items are collapsed, while genuine surplus remains visible as an output node.

Plans are saved as `.colonyplan` files. The last opened or saved plan is restored when the application next starts.

## Privacy and safety

- The app works locally and has no telemetry or sign-in.
- Game saves are opened read-only. Do not attach save files to bug reports unless requested directly.
- Settings, cached public game data, and logs are kept under your Windows local app-data folder. Delete the `ColonyOptimizer` folder there to reset the app.
- Download updates only from this repository's Releases page and verify the SHA-256 checksum.

Please report vulnerabilities privately as described in [SECURITY.md](SECURITY.md). For ordinary bugs or requests, use the repository issue tracker and include the app version, a short description, and reproducible steps.

## For contributors

Technical material is kept out of the user guide. See [CONTRIBUTING.md](CONTRIBUTING.md), [release instructions](docs/RELEASING.md), [architecture](docs/ARCHITECTURE.md), [solver model](docs/SOLVER_MODEL.md), and [game-data validation](docs/GAME_DATA_VALIDATION.md). Bundled visualisation-library notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## License

This project is licensed under the [MIT License](LICENSE).
