# Contributing

## Prerequisites

- Windows 10 or 11
- .NET SDK 10
- A checkout of the public Colony Survival data in `work\ColonySurvival`, or set `COLONY_SURVIVAL_GAMEDATA` to its `gamedata` directory

## Verify a change

```powershell
dotnet restore ColonyOptimizer.slnx
dotnet restore installer\ColonyOptimizer.Installer\ColonyOptimizer.Installer.wixproj
dotnet restore installer\ColonyOptimizer.Setup\ColonyOptimizer.Setup.wixproj
dotnet test tests\ColonyOptimizer.Tests\ColonyOptimizer.Tests.csproj --no-restore
dotnet build src\ColonyOptimizer.App\ColonyOptimizer.App.csproj -c Release --no-restore
```

Keep game data, saves, plans, packages, and build output out of commits. Pull requests are checked with a clean Colony Survival data checkout in GitHub Actions.
