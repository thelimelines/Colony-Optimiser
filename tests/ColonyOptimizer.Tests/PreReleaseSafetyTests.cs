using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using ColonyOptimizer.App;
using ColonyOptimizer.Core;
using ColonyOptimizer.GameData;
using Microsoft.Data.Sqlite;

namespace ColonyOptimizer.Tests;

public sealed class PreReleaseSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ColonyOptimizer", "pre-release-safety-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task atomic_write_keeps_the_existing_plan_when_the_destination_is_locked()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "existing.colonyplan");
        File.WriteAllText(path, "original plan");

        using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var exception = await Record.ExceptionAsync(() => AtomicTextFile.WriteAsync(path, "replacement plan"));
        Assert.True(exception is IOException or UnauthorizedAccessException);

        lockHandle.Dispose();
        Assert.Equal("original plan", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task atomic_write_reports_an_unusable_destination_directory()
    {
        Directory.CreateDirectory(_root);
        var unusableDirectory = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(unusableDirectory, "file");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => AtomicTextFile.WriteAsync(Path.Combine(unusableDirectory, "plan.colonyplan"), "content"));
    }

    [Fact]
    public void new_plan_resets_progression_and_solver_settings_to_blank_defaults()
    {
        var viewModel = new MainWindowViewModel
        {
            EfficiencyPercent = 73m,
            HeadroomPercent = 15m,
            SelectedObjective = ColonyOptimizer.Core.OptimizationObjective.LowestRawResourceConsumption,
            SelectedStochasticPolicy = ColonyOptimizer.Core.StochasticOutputPolicy.Conservative,
            SelectedPlanName = "Configured plan"
        };
        viewModel.Targets.Add(new DemandRow("wheat", "Wheat", 15m, ColonyOptimizer.Core.DemandUnit.PerMinute));
        viewModel.ExternalItems.Add(new ExternalItemRow("logs", "Logs"));
        viewModel.ScienceRows.Add(new SelectableEntry("science", "Science", true));
        viewModel.ToolRows.Add(new SelectableEntry("tools", "Tools", true));

        viewModel.NewPlanCommand.Execute(null);

        Assert.Empty(viewModel.Targets);
        Assert.Empty(viewModel.ExternalItems);
        Assert.All(viewModel.ScienceRows, row => Assert.False(row.IsSelected));
        Assert.All(viewModel.ToolRows, row => Assert.False(row.IsSelected));
        Assert.Equal(100m, viewModel.EfficiencyPercent);
        Assert.Equal(0m, viewModel.HeadroomPercent);
        Assert.Equal(ColonyOptimizer.Core.OptimizationObjective.FewestWorkers, viewModel.SelectedObjective);
        Assert.Equal(ColonyOptimizer.Core.StochasticOutputPolicy.ExpectedValue, viewModel.SelectedStochasticPolicy);
        Assert.Equal("Untitled plan", viewModel.SelectedPlanName);
    }

    [Fact]
    public void changing_game_data_reapplies_the_complete_current_plan()
    {
        var viewModel = new MainWindowViewModel();
        Invoke(viewModel, "ApplyDatabase", CreateSwitchDatabase("first"), null);
        viewModel.SelectedPlanName = "Shared colony";
        viewModel.Targets.Add(new DemandRow("output", "Output", 12m, DemandUnit.PerMinute));
        viewModel.ExternalItems.Add(new ExternalItemRow("ammo", "Ammo"));
        viewModel.ScienceRows.Single().IsSelected = true;
        viewModel.ToolRows.Single().IsSelected = true;
        viewModel.CropSourceRows.Single().FieldWidth = 7;
        viewModel.CropSourceRows.Single().FieldLength = 8;
        viewModel.RecipeRows.Single().Policy = RecipePolicy.Forbidden;
        viewModel.GuardRows.Single().Count = 3;
        viewModel.GuardRows.Single().AmmoMode = GuardAmmoMode.CustomRoundsPerCycle;
        viewModel.GuardRows.Single().CustomRoundsPerCycle = 4;
        viewModel.TrapRows.Single().Count = 2;
        viewModel.EfficiencyPercent = 82m;
        viewModel.HeadroomPercent = 9m;
        viewModel.SelectedObjective = OptimizationObjective.LowestRawResourceConsumption;
        viewModel.SelectedStochasticPolicy = StochasticOutputPolicy.Conservative;
        viewModel.UseTimingOverride = true;
        viewModel.GameTimeScale = 90m;

        var snapshot = Assert.IsType<SavedPlanDocument>(Invoke(viewModel, "CaptureCurrentPlanState"));
        Invoke(viewModel, "ApplyDatabase", CreateSwitchDatabase("second"), snapshot);
        var restored = Assert.IsType<ProductionPlan>(Invoke(viewModel, "BuildPlan"));
        var settings = Assert.IsType<OptimizationSettings>(Invoke(viewModel, "BuildSettings"));

        Assert.Equal("Shared colony", restored.Name);
        Assert.Equal(12m, Assert.Single(restored.Targets).Amount);
        Assert.Contains("ammo", restored.ExternalItems);
        Assert.Contains("science", restored.UnlockedSciences);
        Assert.Contains("tools", restored.AvailableTools);
        var crop = Assert.Single(restored.CropFarmLayouts).Value;
        Assert.Equal(7, crop.Width);
        Assert.Equal(8, crop.Length);
        Assert.Equal(RecipePolicy.Forbidden, restored.RecipePolicies["recipe-one"]);
        var guard = Assert.Single(restored.Guards);
        Assert.Equal(3, guard.Count);
        Assert.Equal(GuardAmmoMode.CustomRoundsPerCycle, guard.AmmoMode);
        Assert.Equal(4, guard.CustomRoundsPerCycle);
        Assert.Equal(2, Assert.Single(restored.Traps).Count);
        Assert.Equal(82m, settings.EfficiencyPercent);
        Assert.Equal(9m, settings.HeadroomPercent);
        Assert.Equal(OptimizationObjective.LowestRawResourceConsumption, settings.Objective);
        Assert.Equal(StochasticOutputPolicy.Conservative, settings.StochasticOutputPolicy);
        Assert.True(settings.TimingOverride.IsEnabled);
        Assert.Equal(90m, settings.TimingOverride.GameTimeScale);
    }

    [Fact]
    public void changing_game_data_with_a_linked_save_preserves_manual_progression()
    {
        var currentPlan = new SavedPlanDocument { Plan = new ProductionPlan(), Settings = new OptimizationSettings() };
        var shouldImport = (bool)typeof(MainWindowViewModel)
            .GetMethod("ShouldAutomaticallyImportLinkedSave", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [currentPlan, false])!;
        var shouldImportWithoutPlan = (bool)typeof(MainWindowViewModel)
            .GetMethod("ShouldAutomaticallyImportLinkedSave", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [null, false])!;
        var shouldImportAfterRestore = (bool)typeof(MainWindowViewModel)
            .GetMethod("ShouldAutomaticallyImportLinkedSave", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [null, true])!;

        Assert.False(shouldImport);
        Assert.True(shouldImportWithoutPlan);
        Assert.False(shouldImportAfterRestore);
    }

    [Fact]
    public void a_malformed_selected_colony_group_preserves_current_progression()
    {
        Directory.CreateDirectory(_root);
        var worldPath = Path.Combine(_root, "malformed-world.sqlite3");
        typeof(SaveGameImportService).GetMethod("EnsureSqliteProvider", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null);
        using (var connection = new SqliteConnection($"Data Source={worldPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE science_mapping (name TEXT NOT NULL, [index] INTEGER NOT NULL);
                CREATE TABLE colonygroups (json TEXT);
                INSERT INTO science_mapping (name, [index]) VALUES ('science', 1);
                INSERT INTO colonygroups (json) VALUES ('{"science":');
                """;
            command.ExecuteNonQuery();
        }

        var viewModel = new MainWindowViewModel { LinkedSaveGamePath = worldPath };
        Invoke(viewModel, "ApplyDatabase", CreateSwitchDatabase("first"), null);
        viewModel.ScienceRows.Single().IsSelected = true;
        viewModel.ToolRows.Single().IsSelected = true;
        typeof(MainWindowViewModel).GetField("selectedColonyGroup", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, new ColonyGroupImportOption(1, "Malformed group"));

        Invoke(viewModel, "TryApplyLinkedSave", false);

        Assert.True(viewModel.ScienceRows.Single().IsSelected);
        Assert.True(viewModel.ToolRows.Single().IsSelected);
        Assert.Equal("The selected colony group could not be read; existing progression was not changed.", viewModel.SaveImportStatus);
    }

    [Fact]
    public void linking_a_world_before_game_data_is_loaded_is_not_an_error()
    {
        Directory.CreateDirectory(_root);
        var worldPath = Path.Combine(_root, "world.sqlite3");
        File.WriteAllBytes(worldPath, []);
        var viewModel = new MainWindowViewModel { LinkedSaveGamePath = worldPath };

        Invoke(viewModel, "TryApplyLinkedSave", true);

        Assert.Equal("World linked — progression will import after game data is loaded.", viewModel.SaveImportStatus);
    }

    [Fact]
    public void plan_provenance_warns_only_for_materially_different_data()
    {
        var github = new GameDataSourceInfo("GitHub cache", "cache", Version: "1.2", Commit: "abc");

        Assert.Null(GameDataSourceComparison.GetDifferenceWarning(github, github));
        Assert.Contains("Results may differ", GameDataSourceComparison.GetDifferenceWarning(github, github with { Commit = "def" }));
        Assert.Contains("Results may differ", GameDataSourceComparison.GetDifferenceWarning(github with { Commit = null }, github with { Commit = null, Version = "1.3" }));
        Assert.Null(GameDataSourceComparison.GetDifferenceWarning(null, github));
    }

    [Fact]
    public void default_crop_layout_has_one_canonical_geometry()
    {
        var layout = CropFarmLayout.CreateDefault(100);

        Assert.Equal(10, layout.Width);
        Assert.Equal(10, layout.Length);
        Assert.True(CropFarmLayout.IsDefault(100, layout.Width, layout.Length));
        Assert.False(CropFarmLayout.IsDefault(100, 1, 100));
    }

    [Fact]
    public void game_data_discovery_skips_a_locked_library_file_and_keeps_other_candidates()
    {
        var steamRoot = Path.Combine(_root, "Steam");
        var gameData = Path.Combine(steamRoot, "steamapps", "common", "Colony Survival", "gamedata");
        Directory.CreateDirectory(Path.Combine(gameData, "baseconfig"));
        File.WriteAllText(Path.Combine(gameData, "baseconfig", "modInfo.json"), "[]");
        var libraryFolders = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        File.WriteAllText(libraryFolders, "\"libraryfolders\"\n{");

        using var lockHandle = new FileStream(libraryFolders, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var discovered = new GameDataAcquisition().FindGameDataDirectories([steamRoot]);

        Assert.Equal([gameData], discovered);
    }

    [Fact]
    public void locked_temporary_game_data_is_left_for_later_cleanup()
    {
        Directory.CreateDirectory(_root);
        var temporaryFile = Path.Combine(_root, "source.zip");
        File.WriteAllText(temporaryFile, "temporary content");

        using var lockHandle = new FileStream(temporaryFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(GameDataAcquisition.TryDeleteDirectory(_root, "test-clean-up"));

        lockHandle.Dispose();
        Assert.True(GameDataAcquisition.TryDeleteDirectory(_root, "test-clean-up"));
    }

    [Fact]
    public async Task downloaded_data_uses_the_resolved_commit_and_replaces_a_validated_cache()
    {
        var cacheRoot = Path.Combine(_root, "cache");
        var existingCache = Path.Combine(cacheRoot, "GitHub");
        Directory.CreateDirectory(existingCache);
        File.WriteAllText(Path.Combine(existingCache, "previous.txt"), "keep until replacement is ready");
        using var handler = new CommitPinnedDownloadHandler();
        var acquisition = new GameDataAcquisition(cacheRoot, handler);

        var downloaded = await acquisition.DownloadLatestAsync();

        Assert.Equal(CommitPinnedDownloadHandler.Commit, downloaded.Commit);
        Assert.Equal(Path.Combine(cacheRoot, "GitHub"), downloaded.GameDataPath);
        Assert.True(File.Exists(Path.Combine(downloaded.GameDataPath, "baseconfig", "modInfo.json")));
        Assert.False(File.Exists(Path.Combine(downloaded.GameDataPath, "previous.txt")));
        Assert.Equal(
            [
                "https://api.github.com/repos/pipliz/ColonySurvival/commits/master",
                $"https://github.com/pipliz/ColonySurvival/archive/{CommitPinnedDownloadHandler.Commit}.zip"
            ],
            handler.RequestUris);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            GameDataAcquisition.TryDeleteDirectory(_root, "clean-up-test-directory");
        }
    }

    private sealed class CommitPinnedDownloadHandler : HttpMessageHandler
    {
        public const string Commit = "0123456789abcdef0123456789abcdef01234567";
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsoluteUri.EndsWith("/commits/master", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"sha\":\"{Commit}\"}}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(CreateRepositoryZip())
            });
        }

        private static byte[] CreateRepositoryZip()
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry($"ColonySurvival-{Commit}/gamedata/baseconfig/modInfo.json");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("[]");
            }

            return stream.ToArray();
        }
    }

    private static object? Invoke(MainWindowViewModel viewModel, string methodName, params object?[] arguments) =>
        typeof(MainWindowViewModel).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, arguments);

    private static GameDatabase CreateSwitchDatabase(string sourcePath)
    {
        var database = new GameDatabase
        {
            Source = new GameDataSourceInfo("Test data", sourcePath, Version: "1.0")
        };
        database.Items.AddRange(
        [
            new ItemDefinition { Id = "output", DisplayName = "Output" },
            new ItemDefinition { Id = "ammo", DisplayName = "Ammo" },
            new ItemDefinition { Id = "tools", DisplayName = "Tools" }
        ]);
        database.Sciences.Add(new ScienceDefinition { Id = "science", DisplayName = "Science" });
        database.Tools.Add(new ToolDefinition { Id = "tools", DisplayName = "Tools", RequiredScience = "science" });
        database.Recipes.AddRange(
        [
            Recipe("recipe-one", "First recipe"),
            Recipe("recipe-two", "Second recipe")
        ]);
        var crop = new CropFarmSourceDefinition
        {
            Id = "crop",
            DisplayName = "Crop",
            JobTypeId = "worker",
            DefaultFieldTiles = 100,
            GrowthCyclesPerHarvest = 1m
        };
        crop.Outputs.Add(new ItemAmount("output", 1m));
        database.CropFarmSources.Add(crop);
        var guard = new GuardTypeDefinition
        {
            Id = "guard",
            DisplayName = "Guard",
            NpcTypeId = "guard",
            CooldownShotSeconds = 1m
        };
        guard.Ammunition.Add(new ItemAmount("ammo", 1m));
        database.Guards.Add(guard);
        database.Traps.Add(new TrapDefinition
        {
            Id = "trap",
            DisplayName = "Trap",
            AmmunitionItemId = "ammo",
            AmmunitionCapacity = 1,
            ReloadSecondsPerAmmunition = 1m
        });
        return database;
    }

    private static RecipeDefinition Recipe(string id, string displayName)
    {
        var recipe = new RecipeDefinition { Id = id, DisplayName = displayName, JobTypeId = "worker", CooldownSeconds = 1m };
        recipe.Outputs.Add(new ItemAmount("output", 1m));
        return recipe;
    }
}
