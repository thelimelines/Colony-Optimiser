# Changelog

All notable user-facing changes are recorded here. Each release tag must have a concise, curated entry; that entry is published as the GitHub release notes.

## Unreleased

### Added

- Read every linked save's colony groups and let multiplayer players import one group instead of always combining progress.

### Fixed

- Keep existing plan and export files intact when a save or export cannot be written, and show a clear recovery message.
- Keep Steam library discovery and public game-data cache cleanup from preventing startup or obscuring a completed update.
- Preserve the complete current plan when changing game-data source instead of leaving a partially reset plan that could be saved accidentally.
- Link a world cleanly before game data is loaded, then import its progression automatically after data is available.
- Keep a crop farm's default field geometry stable through plan save and reopen.

### Changed

- Resolve downloaded public game data to one recorded upstream commit before fetching it.
- **New plan** now creates a blank plan, resetting progression, capacity, defence, solver, and timing choices to their defaults.
- Use one 20-second solver budget across all lexicographic objectives, rather than allowing each stage a full timeout.
- Warn non-blockingly when a plan was created against materially different game data.

## [1.0.6] - 2026-08-28

### Fixed

- Fixed the Setup theme/icon payload so the installer can initialise after extraction.
- Added release-package smoke testing for the Setup UI, MSI deployment, and portable ZIP.
- Package the exact .NET runtime packs selected by the completed publish when collecting licence notices, rather than selecting an arbitrary cached version.
- Keep settings loading, saving, and diagnostic logging from obscuring an operation when ordinary filesystem access fails.
- Describe the bundled WebView2 online bootstrapper accurately.

### Changed

- Stop writing an artificial format-version field into new plan documents; unknown fields in existing plans remain ignored.

## [1.0.5] - 2026-08-27

### Added

- Added responsive visualisation rendering, a visible loading indicator, reset-view support, and persisted layout controls.

### Fixed

- Improved Sankey and node-graph rendering for large plans, including safe tooltip text handling and zero-spacing layouts.
- Corrected miner labels and refreshed the application's HTTP user agent.
