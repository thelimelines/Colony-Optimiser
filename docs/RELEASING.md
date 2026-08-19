# Releasing Colony Optimiser

1. Update `Version` in `Directory.Build.props`.
2. Restore, test, and build locally using the commands in `CONTRIBUTING.md`.
3. Commit the version change and dependency lock files.
4. Create and push an annotated tag matching the version, for example `v1.0.0`.
5. GitHub Actions checks out the required public game data, runs the test suite, publishes a self-contained Windows x64 portable ZIP, a per-machine MSI, and a Setup EXE bootstrapper, then creates SHA-256 checksums and attaches them to the GitHub release.

The release workflow refuses malformed versions and packages into a new `artifacts` directory. The Setup EXE is the standard end-user download; the MSI supports managed deployment and the ZIP remains portable. Do not upload developer build folders, saves, or source archives as release assets.

To create the same package locally, restore the solution and both projects in `installer`, then run `./scripts/Publish-Release.ps1 -Version 1.0.0`. Commit any lock-file updates that result from an intentional package update.
