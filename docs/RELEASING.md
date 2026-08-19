# Releasing Colony Optimiser

Creating a version tag publishes a GitHub release; it is not a dry run. Release from a clean `main` checkout after its verification workflow has passed.

1. Update `Version` in `Directory.Build.props`.
2. Review `README.md`, `CONTRIBUTING.md`, `SECURITY.md`, `THIRD_PARTY_NOTICES.md`, and the changes since the previous release. Confirm that GitHub Private Vulnerability Reporting is enabled.
3. Restore, test, and build locally using the commands in `CONTRIBUTING.md`.
4. Commit the version change and any lock-file changes caused by an intentional dependency update.
5. Create an annotated tag whose version exactly matches `Directory.Build.props`, then push it:

   ```powershell
   git tag -a v1.0.1 -m "Colony Optimiser v1.0.1"
   git push origin v1.0.1
   ```

6. GitHub Actions checks out the required public game data, runs the test suite, publishes a self-contained Windows x64 portable ZIP, a per-machine MSI, and a Setup EXE bootstrapper, then creates SHA-256 checksums and attaches them to the GitHub release.
7. After the workflow succeeds, download the Setup EXE from the release page, verify its checksum using the instructions in `README.md`, and perform a clean installation and first-use check.

Published version tags and their release artefacts are immutable. If a published package is defective, fix the problem and issue a new version; do not move the tag or replace files attached to the existing release. If the workflow fails before publishing a release, correct the problem, delete the unpublished local and remote tag if necessary, then create the tag again from the corrected commit.

Review the automatically generated release notes after publication. Edit them to give users a concise summary of important features, fixes, compatibility changes, known limitations, and upgrade actions; a raw commit list is not sufficient.

The release workflow refuses malformed versions and packages into a new `artifacts` directory. The Setup EXE is the standard end-user download; the MSI supports managed deployment and the ZIP remains portable. Do not upload developer build folders, saves, or source archives as release artefacts.

To create the same package locally without publishing it, restore the solution and both projects in `installer` as described in `CONTRIBUTING.md`, then run:

```powershell
.\scripts\Publish-Release.ps1 -Version 1.0.1
```

Use a clean workspace or remove the packages for that version before running the command. Inspect the ZIP, MSI, Setup EXE, and their checksum files under `artifacts`. Commit lock-file updates only when they result from an intentional dependency update.
