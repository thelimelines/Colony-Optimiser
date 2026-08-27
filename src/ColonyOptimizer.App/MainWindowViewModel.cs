using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ColonyOptimizer.Core;
using ColonyOptimizer.GameData;
using ColonyOptimizer.Optimization;
using Microsoft.Win32;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ColonyOptimizer.App;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly GameDataLoader _loader = new();
    private readonly GameDataAcquisition _acquisition = new();
    private readonly ProductionOptimizer _optimizer = new();
    private readonly SaveGameImportService _saveImporter = new();
    private readonly UserSettingsStore _settingsStore = new();
    private UserSettings _userSettings = new();
    private GameDatabase? _database;
    private OptimizationResult? _lastResult;
    private string? _currentPlanPath;
    private HashSet<string> _plannerItemIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _lastPlanRestored;

    public ObservableCollection<ItemOption> FilteredItems { get; } = [];
    public ObservableCollection<DemandRow> Targets { get; } = [];
    public ObservableCollection<ExternalItemRow> ExternalItems { get; } = [];
    public ObservableCollection<SelectableEntry> ScienceRows { get; } = [];
    public ObservableCollection<SelectableEntry> ToolRows { get; } = [];
    public ObservableCollection<CropSourceRow> CropSourceRows { get; } = [];
    public ObservableCollection<ForestrySourceRow> ForestrySourceRows { get; } = [];
    public ObservableCollection<object> AreaJobRows { get; } = [];
    public ObservableCollection<RecipeRow> RecipeRows { get; } = [];
    public ObservableCollection<GuardRow> GuardRows { get; } = [];
    public ObservableCollection<TrapRow> TrapRows { get; } = [];
    public ObservableCollection<JobRequirement> JobResults { get; } = [];
    public ObservableCollection<RecipeAllocation> AllocationResults { get; } = [];
    public ObservableCollection<ToolRequirement> ToolResults { get; } = [];
    public ObservableCollection<ExternalRequirement> ExternalResults { get; } = [];
    public ObservableCollection<ProductionOutput> OutputResults { get; } = [];
    public ObservableCollection<VisualGraphNode> VisualGraphNodes { get; } = [];
    public ObservableCollection<VisualGraphLink> VisualGraphLinks { get; } = [];
    public ObservableCollection<GraphRootOption> GraphRoots { get; } = [];
    public ObservableCollection<string> RecentPlans { get; } = [];
    public ObservableCollection<WorldSaveOption> WorldSaveOptions { get; } = [];

    public Array DemandUnits { get; } = Enum.GetValues(typeof(DemandUnit));
    public Array RecipePolicies { get; } = Enum.GetValues(typeof(RecipePolicy));
    public IReadOnlyList<GuardAmmoModeOption> GuardAmmoModes { get; } =
    [
        new(GuardAmmoMode.EntireShiftWorstCase, "Entire shift - worst case"),
        new(GuardAmmoMode.HostilePeriodOnly, "Hostile/dark period only"),
        new(GuardAmmoMode.CustomUtilisation, "Custom utilisation"),
        new(GuardAmmoMode.CustomRoundsPerCycle, "Custom shots per cycle")
    ];
    public Array OptimizationObjectives { get; } = Enum.GetValues(typeof(OptimizationObjective));
    public Array StochasticPolicies { get; } = Enum.GetValues(typeof(StochasticOutputPolicy));
    public Array NodeLayoutDirections { get; } = Enum.GetValues(typeof(NodeLayoutDirection));

    [ObservableProperty] private ItemOption? selectedItem;
    [ObservableProperty] private string itemSearch = string.Empty;
    [ObservableProperty] private bool isItemSearchDropDownOpen;
    [ObservableProperty] private decimal newTargetAmount = 10m;
    [ObservableProperty] private DemandUnit newTargetUnit = DemandUnit.PerMinute;
    [ObservableProperty] private string dataDirectory = string.Empty;
    [ObservableProperty] private string dataSourceDisplay = "No game data loaded";
    [ObservableProperty] private string timingDisplay = "Load game data to derive cycle and shift timing.";
    [ObservableProperty] private string diagnosticsText = "No diagnostics available.";
    [ObservableProperty] private string statusText = "Ready";
    [ObservableProperty] private string recipeSearch = string.Empty;
    [ObservableProperty] private string sankeyGraphJson = "{\"mode\":0,\"nodes\":[],\"links\":[]}";
    [ObservableProperty] private int selectedVisualisationIndex;
    [ObservableProperty] private NodeLayoutDirection nodeLayoutDirection = NodeLayoutDirection.Right;
    [ObservableProperty] private int nodeSpacing = 44;
    [ObservableProperty] private int layerSpacing = 96;
    [ObservableProperty] private GraphRootOption? selectedGraphRoot;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private decimal efficiencyPercent = 100m;
    [ObservableProperty] private decimal headroomPercent;
    [ObservableProperty] private OptimizationObjective selectedObjective = OptimizationObjective.FewestWorkers;
    [ObservableProperty] private StochasticOutputPolicy selectedStochasticPolicy = StochasticOutputPolicy.ExpectedValue;
    [ObservableProperty] private string resultHeadline = "No calculation yet";
    [ObservableProperty] private string resultDetail = "Add production targets, configure progression, then optimise.";
    [ObservableProperty] private string selectedPlanName = "Untitled plan";
    [ObservableProperty] private bool isSettingsOpen;
    [ObservableProperty] private bool isWorldSelectionOpen;
    [ObservableProperty] private WorldSaveOption? selectedWorldSave;
    [ObservableProperty] private string linkedSaveGamePath = string.Empty;
    [ObservableProperty] private string saveImportStatus = "No save linked.";
    [ObservableProperty] private bool useTimingOverride;
    [ObservableProperty] private decimal gameTimeScale = GameTiming.Default.GameTimeScale;
    [ObservableProperty] private decimal dayTimeStart = GameTiming.Default.DayTimeStart;
    [ObservableProperty] private decimal dayTimeEnd = GameTiming.Default.DayTimeEnd;
    [ObservableProperty] private decimal guardShiftDayStart = GameTiming.Default.GuardShiftDayStart;
    [ObservableProperty] private decimal guardShiftDayEnd = GameTiming.Default.GuardShiftDayEnd;
    [ObservableProperty] private decimal guardShiftNightStart = GameTiming.Default.GuardShiftNightStart;
    [ObservableProperty] private decimal guardShiftNightEnd = GameTiming.Default.GuardShiftNightEnd;
    [ObservableProperty] private decimal sleepTimeStart = GameTiming.Default.SleepTimeStart;
    [ObservableProperty] private decimal sleepTimeEnd = GameTiming.Default.SleepTimeEnd;

    public string LinkedSaveGameDisplay => string.IsNullOrWhiteSpace(LinkedSaveGamePath)
        ? "No save selected. Select a world's world.sqlite3 file."
        : LinkedSaveGamePath;

    public bool HasVisualisationGraph => _lastResult is { IsFeasible: true }
        && _lastResult.ProductionFlows.Any(flow => flow.Amount > 0m);

    public bool IsNodeVisualiserSelected => SelectedVisualisationIndex == 1;

    public bool HasCoreIconAssets => _database is not null
        && new[] { "coppertools", "wheat", "alkanet" }.All(id =>
        {
            var path = _database.Items.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.IconPath;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        })
        && _database.Traps.All(trap =>
        {
            var ammunitionIcon = _database.Items.FirstOrDefault(item => item.Id.Equals(trap.AmmunitionItemId, StringComparison.OrdinalIgnoreCase))?.IconPath;
            return !string.IsNullOrWhiteSpace(trap.IconPath)
                && File.Exists(trap.IconPath)
                && !string.IsNullOrWhiteSpace(ammunitionIcon)
                && File.Exists(ammunitionIcon);
        });

    public async Task InitializeAsync()
    {
        _userSettings = _settingsStore.Load();
        LinkedSaveGamePath = _userSettings.LinkedSaveGamePath ?? string.Empty;
        if (Enum.TryParse<NodeLayoutDirection>(_userSettings.NodeLayoutDirection, ignoreCase: true, out var savedLayoutDirection))
        {
            NodeLayoutDirection = savedLayoutDirection;
        }
        NodeSpacing = Math.Clamp(_userSettings.NodeSpacing ?? NodeSpacing, 16, 160);
        LayerSpacing = Math.Clamp(_userSettings.LayerSpacing ?? LayerSpacing, 48, 240);
        foreach (var recentPlanPath in _userSettings.RecentPlans.Where(File.Exists))
        {
            RecentPlans.Add(recentPlanPath);
        }

        var path = _userSettings.LastGameDataDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            path = _acquisition.FindInstalledGameDataDirectories().FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            DataDirectory = path;
            await LoadDataAsync();
        }

        if (!AppRuntime.IsVisualSmokeTest && !_userSettings.HasCompletedInitialWorldDiscovery)
        {
            await DiscoverWorldSavesAsync();
            _userSettings.HasCompletedInitialWorldDiscovery = true;
            _settingsStore.Save(_userSettings);
            if (WorldSaveOptions.Count > 0 && !File.Exists(LinkedSaveGamePath))
            {
                IsSettingsOpen = true;
                IsWorldSelectionOpen = true;
                SaveImportStatus = "Select a discovered world to import progression.";
            }
        }
    }

    partial void OnItemSearchChanged(string value)
    {
        RefreshItemFilter();
        IsItemSearchDropDownOpen = !string.IsNullOrWhiteSpace(value) && FilteredItems.Count > 0;
    }
    partial void OnRecipeSearchChanged(string value) => RefreshRecipeFilter();
    partial void OnSelectedVisualisationIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsNodeVisualiserSelected));
        RefreshVisualisation();
    }
    partial void OnSelectedGraphRootChanged(GraphRootOption? value) => RefreshVisualisation();
    partial void OnNodeLayoutDirectionChanged(NodeLayoutDirection value)
    {
        SaveVisualisationSettings();
        RefreshVisualisation();
    }
    partial void OnNodeSpacingChanged(int value)
    {
        if (value is < 16 or > 160)
        {
            NodeSpacing = Math.Clamp(value, 16, 160);
            return;
        }

        SaveVisualisationSettings();
        RefreshVisualisation();
    }
    partial void OnLayerSpacingChanged(int value)
    {
        if (value is < 48 or > 240)
        {
            LayerSpacing = Math.Clamp(value, 48, 240);
            return;
        }

        SaveVisualisationSettings();
        RefreshVisualisation();
    }
    partial void OnUseTimingOverrideChanged(bool value) => RefreshTimingPresentation();
    partial void OnGameTimeScaleChanged(decimal value) => RefreshTimingPresentation();
    partial void OnDayTimeStartChanged(decimal value) => RefreshTimingPresentation();
    partial void OnDayTimeEndChanged(decimal value) => RefreshTimingPresentation();
    partial void OnGuardShiftDayStartChanged(decimal value) => RefreshTimingPresentation();
    partial void OnGuardShiftDayEndChanged(decimal value) => RefreshTimingPresentation();
    partial void OnGuardShiftNightStartChanged(decimal value) => RefreshTimingPresentation();
    partial void OnGuardShiftNightEndChanged(decimal value) => RefreshTimingPresentation();
    partial void OnSleepTimeStartChanged(decimal value) => RefreshTimingPresentation();
    partial void OnSleepTimeEndChanged(decimal value) => RefreshTimingPresentation();
    partial void OnLinkedSaveGamePathChanged(string value) => OnPropertyChanged(nameof(LinkedSaveGameDisplay));

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private void ChooseSaveGame()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Colony Survival world save (world.sqlite3)|world.sqlite3|SQLite database (*.sqlite3;*.db)|*.sqlite3;*.db|All files (*.*)|*.*",
            Title = "Link a Colony Survival world save",
            InitialDirectory = Directory.Exists(_userSettings.LastWorldSaveDirectory) ? _userSettings.LastWorldSaveDirectory : string.Empty,
            FileName = File.Exists(LinkedSaveGamePath) ? Path.GetFileName(LinkedSaveGamePath) : "world.sqlite3"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        LinkSaveGame(dialog.FileName, importProgression: true);
    }

    [RelayCommand]
    private void ImportLinkedSave() => TryApplyLinkedSave(showError: true);

    [RelayCommand]
    private async Task DiscoverWorldSavesAsync()
    {
        var worldPaths = await Task.Run(() => _acquisition.FindWorldSaveDatabases());
        WorldSaveOptions.Clear();
        foreach (var worldPath in worldPaths)
        {
            WorldSaveOptions.Add(new WorldSaveOption(worldPath));
        }

        SelectedWorldSave = WorldSaveOptions.FirstOrDefault(option => option.Path.Equals(LinkedSaveGamePath, StringComparison.OrdinalIgnoreCase));
        IsWorldSelectionOpen = WorldSaveOptions.Count > 0;
        if (WorldSaveOptions.Count == 0)
        {
            SaveImportStatus = "No Colony Survival worlds were found. Select world.sqlite3 manually.";
        }
    }

    [RelayCommand]
    private void UseSelectedWorldSave()
    {
        if (SelectedWorldSave is not null)
        {
            LinkSaveGame(SelectedWorldSave.Path, importProgression: true);
            IsWorldSelectionOpen = false;
        }
    }

    [RelayCommand]
    private void AddTarget()
    {
        if (SelectedItem is null || NewTargetAmount <= 0m)
        {
            return;
        }

        Targets.Add(new DemandRow(SelectedItem.Id, SelectedItem.DisplayName, NewTargetAmount, NewTargetUnit, SelectedItem.IconPath));
        NewTargetAmount = 10m;
    }

    [RelayCommand]
    private void RemoveTarget(DemandRow? target)
    {
        if (target is not null)
        {
            Targets.Remove(target);
        }
    }

    [RelayCommand]
    private void ClearTargets() => Targets.Clear();

    [RelayCommand]
    private void AddExternalItem()
    {
        if (SelectedItem is not null && ExternalItems.All(item => !item.ItemId.Equals(SelectedItem.Id, StringComparison.OrdinalIgnoreCase)))
        {
            ExternalItems.Add(new ExternalItemRow(SelectedItem.Id, SelectedItem.DisplayName, SelectedItem.IconPath));
        }
    }

    [RelayCommand]
    private void RemoveExternalItem(ExternalItemRow? item)
    {
        if (item is not null)
        {
            ExternalItems.Remove(item);
        }
    }

    [RelayCommand]
    private async Task LoadDataAsync()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory))
        {
            ShowError("Select a Colony Survival installation, gamedata folder, or compatible extracted game-data directory.");
            return;
        }

        IsBusy = true;
        StatusText = "Loading game data...";
        try
        {
            var selectedPath = DataDirectory;
            var database = await Task.Run(() => _loader.Load(selectedPath));
            ApplyDatabase(database);
            TryApplyLinkedSave();
            await RestoreLastPlanIfAvailableAsync();
            _userSettings.LastGameDataDirectory = database.Source.SourcePath;
            _settingsStore.Save(_userSettings);
            StatusText = $"Loaded {database.Recipes.Count:N0} recipes";
        }
        catch (Exception exception)
        {
            FileLogger.Write(exception, "load-game-data");
            StatusText = "Game data could not be loaded";
            ShowError(exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ChooseDataDirectoryAsync()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the Colony Survival install folder, gamedata folder, or extracted game-data directory.",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(DataDirectory) ? DataDirectory : string.Empty
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        DataDirectory = dialog.SelectedPath;
        await LoadDataAsync();
    }

    [RelayCommand]
    private async Task DownloadLatestDataAsync()
    {
        IsBusy = true;
        StatusText = "Downloading public game data...";
        try
        {
            var downloaded = await _acquisition.DownloadLatestAsync();
            DataDirectory = downloaded.GameDataPath;
            var database = await Task.Run(() => _loader.Load(downloaded.GameDataPath));
            database.Source = new GameDataSourceInfo("GitHub cache", downloaded.GameDataPath, database.Source.Version, downloaded.Commit, downloaded.DownloadedAt);
            ApplyDatabase(database);
            TryApplyLinkedSave();
            await RestoreLastPlanIfAvailableAsync();
            _userSettings.LastGameDataDirectory = downloaded.GameDataPath;
            _settingsStore.Save(_userSettings);
            StatusText = "Downloaded and loaded latest public data";
        }
        catch (Exception exception)
        {
            FileLogger.Write(exception, "download-game-data");
            StatusText = "Download failed";
            ShowError($"The public game-data download failed: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OptimiseAsync()
    {
        if (_database is null)
        {
            ShowError("Load game data before running an optimisation.");
            return;
        }

        if (Targets.Count == 0 && GuardRows.All(row => row.Count == 0) && TrapRows.All(row => row.Count == 0))
        {
            ShowError("Add at least one production target, guard requirement, or trap requirement.");
            return;
        }

        IsBusy = true;
        StatusText = "Optimising production network...";
        try
        {
            var plan = BuildPlan();
            var settings = BuildSettings();
            var result = await Task.Run(() => _optimizer.Optimize(_database, plan, settings));
            ApplyResult(result);
            StatusText = !result.IsFeasible
                ? "No feasible plan"
                : result.IsOptimal
                    ? "Optimisation complete"
                    : "Optimisation complete — approximate result";
        }
        catch (Exception exception)
        {
            FileLogger.Write(exception, "optimise");
            StatusText = "Optimisation failed";
            ShowError($"The optimiser stopped unexpectedly: {exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> RunVisualisationSmokeOptimisationAsync()
    {
        if (_database is null)
        {
            return false;
        }

        var wroughtIron = _database.Items.FirstOrDefault(item => item.Id.Equals("ironwrought", StringComparison.OrdinalIgnoreCase));
        if (wroughtIron is null)
        {
            return false;
        }

        Targets.Clear();
        ExternalItems.Clear();
        foreach (var science in ScienceRows) science.IsSelected = true;
        foreach (var tool in ToolRows) tool.IsSelected = true;
        Targets.Add(new DemandRow(wroughtIron.Id, wroughtIron.DisplayName, 10m, DemandUnit.PerMinute, wroughtIron.IconPath));

        var result = await Task.Run(() => _optimizer.Optimize(_database, BuildPlan(), BuildSettings()));
        ApplyResult(result);
        StatusText = result.IsFeasible && HasVisualisationGraph && HasCoreIconAssets
            ? "Visualisation smoke optimisation complete"
            : "Visualisation smoke optimisation did not resolve graph or icon data";
        return result.IsFeasible && HasVisualisationGraph && HasCoreIconAssets;
    }

    public void ReportVisualisationFailure()
    {
        if (HasVisualisationGraph)
        {
            StatusText = "Optimisation complete, but visualisation failed to render";
        }
    }

    public void ReportVisualisationRuntimeInstallationStarted() =>
        StatusText = "Downloading and installing the Microsoft Edge WebView2 Runtime for visualisation...";

    public void ReportVisualisationRuntimeInstalled() =>
        StatusText = "Microsoft Edge WebView2 Runtime installed; visualisation is ready";

    public void ReportVisualisationRuntimeUnavailable() =>
        StatusText = "Visualisation requires the Microsoft Edge WebView2 Runtime. Keep an internet connection and restart the app, or install it from Microsoft.";

    [RelayCommand]
    private async Task SavePlanAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentPlanPath))
        {
            await SavePlanAsAsync();
            return;
        }

        await SavePlanToAsync(_currentPlanPath);
    }

    [RelayCommand]
    private async Task SavePlanAsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Colony Optimiser plan (*.colonyplan)|*.colonyplan|JSON files (*.json)|*.json",
            DefaultExt = ".colonyplan",
            FileName = string.IsNullOrWhiteSpace(SelectedPlanName) ? "Untitled plan" : SelectedPlanName,
            InitialDirectory = Directory.Exists(_userSettings.LastPlanDirectory) ? _userSettings.LastPlanDirectory : string.Empty
        };
        if (dialog.ShowDialog() == true)
        {
            await SavePlanToAsync(dialog.FileName);
        }
    }

    [RelayCommand]
    private async Task OpenPlanAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Colony Optimiser plan (*.colonyplan;*.json)|*.colonyplan;*.json",
            InitialDirectory = Directory.Exists(_userSettings.LastPlanDirectory) ? _userSettings.LastPlanDirectory : string.Empty
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await OpenPlanFromAsync(dialog.FileName);
    }

    [RelayCommand]
    private async Task OpenRecentPlanAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            await OpenPlanFromAsync(path);
        }
    }

    [RelayCommand]
    private void NewPlan()
    {
        Targets.Clear();
        ExternalItems.Clear();
        GuardRows.ToList().ForEach(row => row.Count = 0);
        TrapRows.ToList().ForEach(row => row.Count = 0);
        RecipeRows.ToList().ForEach(row => row.Policy = RecipePolicy.Allowed);
        SelectedPlanName = "Untitled plan";
        _currentPlanPath = null;
        ClearResults();
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        Clipboard.SetText(DiagnosticsText);
        StatusText = "Diagnostics copied";
    }

    [RelayCommand]
    private void CopySummary()
    {
        Clipboard.SetText(BuildClipboardSummary());
        StatusText = "Production summary copied";
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", DefaultExt = ".csv", FileName = "colony-production-plan.csv" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var content = new StringBuilder("Job,Workers,Machine blocks,Workload seconds,Capacity seconds,Utilisation percent,Selected tool\r\n");
        foreach (var job in JobResults)
        {
            content.AppendLine(string.Join(',', Csv(job.JobDisplayName), job.Workers, job.MachineBlocks, job.WorkloadSeconds.ToString(CultureInfo.InvariantCulture), job.CapacitySeconds.ToString(CultureInfo.InvariantCulture), job.UtilisationPercent.ToString(CultureInfo.InvariantCulture), Csv(job.SelectedToolId ?? string.Empty)));
        }

        content.AppendLine();
        content.AppendLine("Tool job,Tool,Starter stock,Speed multiplier,Durability");
        foreach (var tool in ToolResults)
        {
            content.AppendLine(string.Join(',', Csv(tool.JobDisplayName), Csv(tool.ToolDisplayName), tool.Quantity, tool.CraftingSpeed.ToString(CultureInfo.InvariantCulture), tool.Durability.ToString(CultureInfo.InvariantCulture)));
        }

        await File.WriteAllTextAsync(dialog.FileName, content.ToString());
        StatusText = "CSV export created";
    }

    [RelayCommand]
    private async Task ExportResultJsonAsync()
    {
        var dialog = new SaveFileDialog { Filter = "JSON files (*.json)|*.json", DefaultExt = ".json", FileName = "colony-production-result.json" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var result = new { Jobs = JobResults, Recipes = AllocationResults, Outputs = OutputResults, Flows = _lastResult?.ProductionFlows, Tools = ToolResults, ExternalRequirements = ExternalResults, Headline = ResultHeadline, Detail = ResultDetail };
        await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(result, JsonDefaults.Options));
        StatusText = "JSON export created";
    }

    private void ApplyDatabase(GameDatabase database)
    {
        _database = database;
        DataDirectory = database.Source.SourcePath;
        DataSourceDisplay = $"{database.Source.SourceType}: {database.Source.SourcePath}" +
            (string.IsNullOrWhiteSpace(database.Source.Commit) ? string.Empty : $"  Commit {database.Source.Commit[..Math.Min(8, database.Source.Commit.Length)]}");
        LoadTimingEditor(database.Timing);
        DiagnosticsText = BuildDiagnostics(database);

        _plannerItemIds = GetPlannerItemIds(database);
        RefreshItemFilter();

        ScienceRows.Clear();
        foreach (var science in database.Sciences.OrderBy(science => science.DisplayName))
        {
            ScienceRows.Add(new SelectableEntry(science.Id, science.DisplayName, true));
        }

        ToolRows.Clear();
        foreach (var tool in database.Tools.OrderBy(tool => GetToolSortOrder(tool.Id)).ThenBy(tool => tool.DisplayName))
        {
            ToolRows.Add(new SelectableEntry(tool.Id, tool.DisplayName, true, database.Items.FirstOrDefault(item => item.Id.Equals(tool.Id, StringComparison.OrdinalIgnoreCase))?.IconPath, IsToolSectionStart(tool.Id)));
        }

        CropSourceRows.Clear();
        AreaJobRows.Clear();
        foreach (var source in database.CropFarmSources.OrderBy(source => source.DisplayName))
        {
            var row = new CropSourceRow(source, EffectiveTiming, database);
            CropSourceRows.Add(row);
            AreaJobRows.Add(row);
        }

        ForestrySourceRows.Clear();
        foreach (var source in database.ForestrySources.OrderBy(source => source.DisplayName))
        {
            var row = new ForestrySourceRow(source, database);
            ForestrySourceRows.Add(row);
            AreaJobRows.Add(row);
        }

        var automatedJobs = database.Jobs.Where(job => job.IsAutomatedQueue).Select(job => job.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var productionRecipes = database.Recipes.Where(recipe => !recipe.JobTypeId.Equals("player", StringComparison.OrdinalIgnoreCase)).ToArray();
        var alternateOutputIds = productionRecipes.SelectMany(recipe => recipe.Outputs.Select(output => new { recipe.Id, output.ItemId }))
            .GroupBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        RecipeRows.Clear();
        foreach (var group in productionRecipes
            .Where(recipe => recipe.Outputs.Any(output => alternateOutputIds.Contains(output.ItemId)))
            .GroupBy(GetMaterialRecipeSignature, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.First().Outputs[0].ItemId)
            .ThenBy(group => group.First().DisplayName))
        {
            var representative = group.OrderBy(recipe => recipe.RequiredScience is null ? 0 : 1).ThenBy(recipe => recipe.Id).First();
            RecipeRows.Add(new RecipeRow(representative, automatedJobs.Contains(representative.JobTypeId), database, group.Select(recipe => recipe.Id)));
        }

        GuardRows.Clear();
        foreach (var guard in database.Guards.OrderBy(guard => GetGuardSortOrder(guard.Id)).ThenBy(guard => guard.Shift))
        {
            GuardRows.Add(new GuardRow(guard, EffectiveTiming, database));
        }

        TrapRows.Clear();
        foreach (var trap in database.Traps.OrderBy(trap => trap.DisplayName))
        {
            TrapRows.Add(new TrapRow(trap, database));
        }

        ExternalItems.Clear();
        ClearResults();
    }

    private void RefreshItemFilter()
    {
        if (_database is null)
        {
            return;
        }

        var selectedId = SelectedItem?.Id;
        var query = ItemSearch.Trim();
        FilteredItems.Clear();
        foreach (var item in _database.Items.Where(item => _plannerItemIds.Contains(item.Id) && (string.IsNullOrWhiteSpace(query) || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))).OrderBy(item => item.DisplayName).Take(250))
        {
            FilteredItems.Add(new ItemOption(item.Id, item.DisplayName, item.IconPath));
        }

        SelectedItem = !string.IsNullOrWhiteSpace(selectedId)
            ? FilteredItems.FirstOrDefault(item => item.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            : string.IsNullOrWhiteSpace(query) ? FilteredItems.FirstOrDefault() : null;
    }

    private void RefreshRecipeFilter()
    {
        var query = RecipeSearch.Trim();
        foreach (var row in RecipeRows)
        {
            row.IsVisible = string.IsNullOrWhiteSpace(query) || row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || row.JobTypeId.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
    }

    private ProductionPlan BuildPlan()
    {
        var plan = new ProductionPlan { Name = SelectedPlanName };
        plan.Targets = Targets.Select(row => new DemandTarget { ItemId = row.ItemId, Amount = row.Amount, Unit = row.Unit }).ToList();
        plan.UnlockedSciences = ScienceRows.Where(row => row.IsSelected).Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        plan.AvailableTools = ToolRows.Where(row => row.IsSelected).Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        plan.RecipePolicies = RecipeRows
            .SelectMany(row => row.RelatedRecipeIds.Select(id => new KeyValuePair<string, RecipePolicy>(id, row.Policy)))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        plan.CropFarmLayouts = CropSourceRows.ToDictionary(row => row.Id, row => new CropFarmLayout
        {
            Width = Math.Max(1, row.FieldWidth),
            Length = Math.Max(1, row.FieldLength)
        }, StringComparer.OrdinalIgnoreCase);
        plan.ForestryLayouts = ForestrySourceRows.ToDictionary(row => row.Id, row => new ForestryLayout
        {
            ForesterCount = Math.Max(1, row.ForesterCount),
            PlotWidth = Math.Max(1, row.PlotWidth),
            PlotLength = Math.Max(1, row.PlotLength)
        }, StringComparer.OrdinalIgnoreCase);
        plan.ExternalItems = ExternalItems.Select(item => item.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        plan.Guards = GuardRows.Where(row => row.Count > 0).Select(row => new GuardAssignment { GuardTypeId = row.Id, Count = row.Count, AmmoMode = row.AmmoMode, UtilisationPercent = row.UtilisationPercent, CustomRoundsPerCycle = row.CustomRoundsPerCycle }).ToList();
        plan.Traps = TrapRows.Where(row => row.Count > 0).Select(row => new TrapAssignment { TrapTypeId = row.Id, Count = row.Count }).ToList();
        return plan;
    }

    private OptimizationSettings BuildSettings() => new()
    {
        EfficiencyPercent = EfficiencyPercent,
        HeadroomPercent = HeadroomPercent,
        Objective = SelectedObjective,
        StochasticOutputPolicy = SelectedStochasticPolicy,
        TimingOverride = BuildTimingOverride()
    };

    private void ApplyResult(OptimizationResult result)
    {
        JobResults.Clear();
        AllocationResults.Clear();
        ToolResults.Clear();
        ExternalResults.Clear();
        OutputResults.Clear();
        foreach (var job in result.JobRequirements.OrderByDescending(job => job.BlockCount)) JobResults.Add(job);
        foreach (var allocation in result.RecipeAllocations.OrderBy(allocation => allocation.JobTypeId).ThenBy(allocation => allocation.RecipeId))
        {
            allocation.IconPath = _database?.Recipes.FirstOrDefault(recipe => recipe.Id.Equals(allocation.RecipeId, StringComparison.OrdinalIgnoreCase))?.Outputs
                .Select(output => _database.Items.FirstOrDefault(item => item.Id.Equals(output.ItemId, StringComparison.OrdinalIgnoreCase))?.IconPath)
                .FirstOrDefault(icon => !string.IsNullOrWhiteSpace(icon));
            AllocationResults.Add(allocation);
        }
        foreach (var tool in result.ToolRequirements.OrderBy(tool => tool.ToolDisplayName).ThenBy(tool => tool.JobDisplayName)) ToolResults.Add(tool);
        foreach (var requirement in result.ExternalRequirements.OrderBy(requirement => requirement.ItemId))
        {
            requirement.IconPath = _database?.Items.FirstOrDefault(item => item.Id.Equals(requirement.ItemId, StringComparison.OrdinalIgnoreCase))?.IconPath;
            ExternalResults.Add(requirement);
        }
        foreach (var output in result.TotalOutputs.OrderBy(output => output.ItemDisplayName))
        {
            output.IconPath = _database?.Items.FirstOrDefault(item => item.Id.Equals(output.ItemId, StringComparison.OrdinalIgnoreCase))?.IconPath;
            OutputResults.Add(output);
        }
        _lastResult = result;
        RefreshVisualisation();

        if (result.IsFeasible)
        {
            var exactness = result.IsOptimal ? string.Empty : " (approximate)";
            ResultHeadline = $"{result.TotalWorkers:N0} production workers + {result.TotalMachineBlocks:N0} machine blocks{exactness}";
            ResultDetail = $"{result.JobRequirements.Count} job types | {result.RecipeAllocations.Count(allocation => allocation.IsAutomatedQueue)} queued machine outputs | {result.TotalOutputs.Count} planned outputs | {result.ExternalRequirements.Count} external inputs | solver {result.SolverStatus} | cycle {EffectiveTiming.CycleSeconds / 60m:0.##} minutes";
        }
        else
        {
            ResultHeadline = "No feasible production plan";
            ResultDetail = string.Join(Environment.NewLine, result.Messages.Select(message => message.Text));
        }
    }

    private void ClearResults()
    {
        JobResults.Clear();
        AllocationResults.Clear();
        ToolResults.Clear();
        ExternalResults.Clear();
        OutputResults.Clear();
        GraphRoots.Clear();
        VisualGraphNodes.Clear();
        VisualGraphLinks.Clear();
        _lastResult = null;
        ResultHeadline = "No calculation yet";
        ResultDetail = "Add production targets, configure progression, then optimise.";
    }

    private async Task SavePlanToAsync(string path)
    {
        var plan = BuildPlan();
        plan.Name = Path.GetFileNameWithoutExtension(path);
        var document = new SavedPlanDocument { Plan = plan, Settings = BuildSettings(), DataSource = _database?.Source };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, JsonDefaults.Options));
        _currentPlanPath = path;
        SelectedPlanName = Path.GetFileNameWithoutExtension(path);
        _userSettings.LastPlanPath = path;
        _userSettings.LastPlanDirectory = Path.GetDirectoryName(path);
        _userSettings.AddRecentPlan(path);
        _settingsStore.Save(_userSettings);
        RefreshRecentPlans();
        StatusText = "Plan saved";
    }

    private async Task OpenPlanFromAsync(string path)
    {
        try
        {
            var document = JsonSerializer.Deserialize<SavedPlanDocument>(await File.ReadAllTextAsync(path), JsonDefaults.Options)
                ?? throw new InvalidDataException("The plan file contains no plan document.");
            ApplyPlan(document.Plan, document.Settings);
            _currentPlanPath = path;
            SelectedPlanName = Path.GetFileNameWithoutExtension(path);
            _userSettings.LastPlanPath = path;
            _userSettings.LastPlanDirectory = Path.GetDirectoryName(path);
            _userSettings.AddRecentPlan(path);
            _settingsStore.Save(_userSettings);
            RefreshRecentPlans();
            StatusText = "Plan loaded";
        }
        catch (Exception exception)
        {
            FileLogger.Write(exception, "open-plan");
            ShowError($"The selected plan could not be opened: {exception.Message}");
        }
    }

    private async Task RestoreLastPlanIfAvailableAsync()
    {
        if (_lastPlanRestored || string.IsNullOrWhiteSpace(_userSettings.LastPlanPath) || !File.Exists(_userSettings.LastPlanPath))
        {
            return;
        }

        _lastPlanRestored = true;
        await OpenPlanFromAsync(_userSettings.LastPlanPath);
    }

    private void ApplyPlan(ProductionPlan plan, OptimizationSettings settings)
    {
        Targets.Clear();
        ExternalItems.Clear();
        foreach (var target in plan.Targets)
        {
            var item = _database?.Items.FirstOrDefault(candidate => candidate.Id.Equals(target.ItemId, StringComparison.OrdinalIgnoreCase));
            Targets.Add(new DemandRow(target.ItemId, item?.DisplayName ?? DisplayName.FromIdentifier(target.ItemId), target.Amount, target.Unit, item?.IconPath));
        }
        foreach (var itemId in plan.ExternalItems)
        {
            var item = _database?.Items.FirstOrDefault(candidate => candidate.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase));
            ExternalItems.Add(new ExternalItemRow(itemId, item?.DisplayName ?? DisplayName.FromIdentifier(itemId), item?.IconPath));
        }
        foreach (var science in ScienceRows) science.IsSelected = plan.UnlockedSciences.Contains(science.Id);
        foreach (var tool in ToolRows) tool.IsSelected = plan.AvailableTools.Contains(tool.Id);
        foreach (var cropSource in CropSourceRows)
        {
            var layout = plan.CropFarmLayouts.GetValueOrDefault(cropSource.Id);
            if (layout is not null && layout.Width > 0 && layout.Length > 0)
            {
                cropSource.FieldWidth = layout.Width;
                cropSource.FieldLength = layout.Length;
                continue;
            }

            var tiles = Math.Max(1, cropSource.DefaultFieldTiles);
            cropSource.FieldWidth = 1;
            cropSource.FieldLength = tiles;
        }
        foreach (var forestrySource in ForestrySourceRows)
        {
            var layout = plan.ForestryLayouts.GetValueOrDefault(forestrySource.Id);
            forestrySource.ForesterCount = layout is { ForesterCount: > 0 } ? layout.ForesterCount : forestrySource.DefaultForesterCount;
            forestrySource.PlotWidth = layout is { PlotWidth: > 0 } ? layout.PlotWidth : forestrySource.DefaultPlotWidth;
            forestrySource.PlotLength = layout is { PlotLength: > 0 } ? layout.PlotLength : forestrySource.DefaultPlotLength;
        }
        foreach (var recipe in RecipeRows)
        {
            recipe.Policy = recipe.RelatedRecipeIds.Select(id => plan.RecipePolicies.GetValueOrDefault(id, RecipePolicy.Allowed)).FirstOrDefault(policy => policy != RecipePolicy.Allowed);
        }
        foreach (var guard in GuardRows)
        {
            var assignment = plan.Guards.FirstOrDefault(item => item.GuardTypeId.Equals(guard.Id, StringComparison.OrdinalIgnoreCase));
            guard.Count = assignment?.Count ?? 0;
            guard.AmmoMode = assignment?.AmmoMode ?? GuardAmmoMode.EntireShiftWorstCase;
            guard.UtilisationPercent = assignment?.UtilisationPercent ?? 100m;
            guard.CustomRoundsPerCycle = assignment?.CustomRoundsPerCycle;
        }
        foreach (var trap in TrapRows)
        {
            trap.Count = plan.Traps.FirstOrDefault(item => item.TrapTypeId.Equals(trap.Id, StringComparison.OrdinalIgnoreCase))?.Count ?? 0;
        }

        SelectedPlanName = plan.Name;
        EfficiencyPercent = settings.EfficiencyPercent;
        HeadroomPercent = settings.HeadroomPercent;
        SelectedObjective = settings.Objective;
        SelectedStochasticPolicy = settings.StochasticOutputPolicy;
        ApplyTimingOverride(settings.TimingOverride);
        ClearResults();
    }

    private string BuildDiagnostics(GameDatabase database)
    {
        var summary = new[]
        {
            $"Items: {database.Items.Count:N0}", $"Recipes: {database.Recipes.Count:N0}", $"Crop farms: {database.CropFarmSources.Count:N0}", $"Forests: {database.ForestrySources.Count:N0}", $"Jobs: {database.Jobs.Count:N0}", $"Toolsets: {database.Toolsets.Count:N0}", $"Tools: {database.Tools.Count:N0}", $"Sciences: {database.Sciences.Count:N0}", $"Guards: {database.Guards.Count:N0}", $"Traps: {database.Traps.Count:N0}", $"Diagnostics: {database.Diagnostics.Entries.Count:N0}"
        };
        var actionable = database.Diagnostics.Entries.Where(entry => entry.Level is DiagnosticLevel.Warning or DiagnosticLevel.Error);
        return string.Join(Environment.NewLine, summary) + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, actionable.Select(entry => $"[{entry.Level}] {entry.Message}" + (string.IsNullOrWhiteSpace(entry.SourceFile) ? string.Empty : $" ({entry.SourceFile})")));
    }

    private string BuildClipboardSummary()
    {
        var summary = new StringBuilder("Colony production plan\r\n\r\n");
        foreach (var job in JobResults) summary.AppendLine($"{job.BlockCount} x {job.JobDisplayName}{(job.IsAutomatedQueue ? " (machine)" : string.Empty)}");
        if (OutputResults.Count > 0)
        {
            summary.AppendLine("\r\nTotal outputs:");
            foreach (var output in OutputResults) summary.AppendLine($"{output.ItemDisplayName}: {output.PerCycle:0.##}/cycle ({output.PerMinute:0.##}/min)");
        }
        if (ExternalResults.Count > 0)
        {
            summary.AppendLine("\r\nExternal inputs:");
            foreach (var external in ExternalResults) summary.AppendLine($"{external.ItemDisplayName}: {external.PerMinute:0.##}/min ({external.Source})");
        }
        return summary.ToString();
    }

    private GameTiming EffectiveTiming => BuildTimingOverride().Apply(_database?.Timing ?? GameTiming.Default);

    private TimingOverride BuildTimingOverride() => new()
    {
        IsEnabled = UseTimingOverride,
        GameTimeScale = GameTimeScale,
        DayTimeStart = DayTimeStart,
        DayTimeEnd = DayTimeEnd,
        GuardShiftDayStart = GuardShiftDayStart,
        GuardShiftDayEnd = GuardShiftDayEnd,
        GuardShiftNightStart = GuardShiftNightStart,
        GuardShiftNightEnd = GuardShiftNightEnd,
        SleepTimeStart = SleepTimeStart,
        SleepTimeEnd = SleepTimeEnd
    };

    private void LoadTimingEditor(GameTiming timing)
    {
        UseTimingOverride = false;
        GameTimeScale = timing.GameTimeScale;
        DayTimeStart = timing.DayTimeStart;
        DayTimeEnd = timing.DayTimeEnd;
        GuardShiftDayStart = timing.GuardShiftDayStart;
        GuardShiftDayEnd = timing.GuardShiftDayEnd;
        GuardShiftNightStart = timing.GuardShiftNightStart;
        GuardShiftNightEnd = timing.GuardShiftNightEnd;
        SleepTimeStart = timing.SleepTimeStart;
        SleepTimeEnd = timing.SleepTimeEnd;
        RefreshTimingPresentation();
    }

    private void ApplyTimingOverride(TimingOverride? timing)
    {
        timing ??= new TimingOverride();
        GameTimeScale = timing.GameTimeScale;
        DayTimeStart = timing.DayTimeStart;
        DayTimeEnd = timing.DayTimeEnd;
        GuardShiftDayStart = timing.GuardShiftDayStart;
        GuardShiftDayEnd = timing.GuardShiftDayEnd;
        GuardShiftNightStart = timing.GuardShiftNightStart;
        GuardShiftNightEnd = timing.GuardShiftNightEnd;
        SleepTimeStart = timing.SleepTimeStart;
        SleepTimeEnd = timing.SleepTimeEnd;
        UseTimingOverride = timing.IsEnabled;
        RefreshTimingPresentation();
    }

    private void RefreshTimingPresentation()
    {
        var timing = EffectiveTiming;
        TimingDisplay = $"Cycle {timing.CycleSeconds / 60m:0.##} min | Day {FormatHour(timing.DayTimeStart)}-{FormatHour(timing.DayTimeEnd)} | Night {FormatHour(timing.DayTimeEnd)}-{FormatHour(timing.DayTimeStart)} | Work {FormatHour(timing.SleepTimeEnd)}-{FormatHour(timing.SleepTimeStart)} ({timing.WorkerActiveSeconds:0.#} s) | Guard D {timing.DayGuardSeconds:0.#} s / N {timing.NightGuardSeconds:0.#} s";
        foreach (var cropSource in CropSourceRows)
        {
            cropSource.UpdateTiming(timing);
        }
        foreach (var guard in GuardRows)
        {
            guard.UpdateTiming(timing);
        }
    }

    private void TryApplyLinkedSave(bool showError = false)
    {
        if (_database is null)
        {
            if (showError)
            {
                ShowError("Load game data before importing a save.");
            }
            return;
        }

        if (!File.Exists(LinkedSaveGamePath))
        {
            var detectedSave = _saveImporter.FindLastWorldDatabase(_database.Source.SourcePath);
            if (!string.IsNullOrWhiteSpace(detectedSave))
            {
                LinkedSaveGamePath = detectedSave;
                _userSettings.LinkedSaveGamePath = detectedSave;
                _userSettings.LastWorldSaveDirectory = Path.GetDirectoryName(detectedSave);
                _settingsStore.Save(_userSettings);
            }
        }

        if (!File.Exists(LinkedSaveGamePath))
        {
            SaveImportStatus = "No readable world.sqlite3 is linked.";
            if (showError)
            {
                ShowError("Choose a Colony Survival world.sqlite3 file to import its completed sciences.");
            }
            return;
        }

        try
        {
            var imported = _saveImporter.Import(LinkedSaveGamePath);
            foreach (var science in ScienceRows)
            {
                science.IsSelected = imported.UnlockedScienceIds.Contains(science.Id);
            }
            foreach (var tool in ToolRows)
            {
                var definition = _database.Tools.FirstOrDefault(candidate => candidate.Id.Equals(tool.Id, StringComparison.OrdinalIgnoreCase));
                tool.IsSelected = definition?.RequiredScience is null || imported.UnlockedScienceIds.Contains(definition.RequiredScience);
            }

            SaveImportStatus = $"Imported {imported.UnlockedScienceIds.Count:N0} completed sciences; tool limits now match their unlocks.";
            StatusText = "Save progress imported";
        }
        catch (Exception exception)
        {
            FileLogger.Write(exception, "import-save");
            SaveImportStatus = "Save import failed.";
            if (showError)
            {
                ShowError($"The save could not be read: {exception.Message}");
            }
        }
    }

    private void LinkSaveGame(string path, bool importProgression)
    {
        LinkedSaveGamePath = path;
        _userSettings.LinkedSaveGamePath = path;
        _userSettings.LastWorldSaveDirectory = Path.GetDirectoryName(path);
        _settingsStore.Save(_userSettings);
        if (importProgression)
        {
            TryApplyLinkedSave(showError: true);
        }
    }

    private static string FormatHour(decimal value)
    {
        var wholeHours = (int)Math.Floor(value) % 24;
        if (wholeHours < 0)
        {
            wholeHours += 24;
        }
        var minutes = (int)Math.Round((value - Math.Floor(value)) * 60m, MidpointRounding.AwayFromZero);
        if (minutes == 60)
        {
            wholeHours = (wholeHours + 1) % 24;
            minutes = 0;
        }
        return $"{wholeHours:00}:{minutes:00}";
    }

    private static HashSet<string> GetPlannerItemIds(GameDatabase database)
    {
        var ids = database.Recipes
            .Where(recipe => !recipe.JobTypeId.Equals("player", StringComparison.OrdinalIgnoreCase))
            .SelectMany(recipe => recipe.Outputs)
            .Select(output => output.ItemId)
            .Concat(database.MiningSources.Select(source => source.OutputItemId))
            .Concat(database.CropFarmSources.SelectMany(source => source.Outputs).Select(output => output.ItemId))
            .Concat(database.ForestrySources.SelectMany(source => new[] { source.LogItemId, source.LeavesItemId }))
            .Concat(database.Guards.SelectMany(guard => guard.Ammunition).Select(ammunition => ammunition.ItemId))
            .Concat(database.Traps.Select(trap => trap.AmmunitionItemId))
            .Concat(database.Tools.Select(tool => tool.Id));

        return ids.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static int GetToolSortOrder(string toolId) => toolId.ToLowerInvariant() switch
    {
        "notools" => 0,
        "stonetools" => 10,
        "coppertools" => 20,
        "irontools" => 30,
        "bronzetools" => 40,
        "steeltools" => 50,
        "eyeglasses" => 100,
        "machinetools" => 200,
        _ => 300
    };

    private static bool IsToolSectionStart(string toolId) => toolId.Equals("eyeglasses", StringComparison.OrdinalIgnoreCase)
        || toolId.Equals("machinetools", StringComparison.OrdinalIgnoreCase);

    private static int GetGuardSortOrder(string guardId)
    {
        var id = guardId.ToLowerInvariant();
        return id.Contains("slinger") ? 0
            : id.Contains("crossbow") ? 20
            : id.Contains("bow") ? 10
            : id.Contains("poison") ? 30
            : id.Contains("handcannon") ? 40
            : id.Contains("musket") ? 50
            : id.Contains("grenade") ? 60
            : 100;
    }

    private static string GetMaterialRecipeSignature(RecipeDefinition recipe) => string.Join('|',
        recipe.Outputs.OrderBy(amount => amount.ItemId, StringComparer.OrdinalIgnoreCase).Select(amount => $"out:{amount.ItemId}:{amount.Amount}:{amount.Chance}:{amount.IsOptional}")
            .Concat(recipe.Inputs.OrderBy(amount => amount.ItemId, StringComparer.OrdinalIgnoreCase).Select(amount => $"in:{amount.ItemId}:{amount.Amount}:{amount.Chance}:{amount.IsOptional}")));

    private void RefreshVisualisation()
    {
        VisualGraphNodes.Clear();
        VisualGraphLinks.Clear();
        GraphRoots.Clear();
        if (_lastResult is null || !_lastResult.IsFeasible)
        {
            SankeyGraphJson = "{\"mode\":0,\"nodes\":[],\"links\":[]}";
            return;
        }

        var flows = _lastResult.ProductionFlows.Where(flow => flow.Amount > 0m).ToList();
        var allocationsByNodeId = _lastResult.RecipeAllocations.ToDictionary(allocation => $"recipe:{allocation.RecipeId}", StringComparer.OrdinalIgnoreCase);
        var jobRequirementsById = _lastResult.JobRequirements.ToDictionary(requirement => requirement.JobTypeId, StringComparer.OrdinalIgnoreCase);

        var sankeyNodes = flows
            .SelectMany(flow => new[]
            {
                new { Id = flow.SourceId, Label = flow.SourceLabel, Kind = flow.SourceKind, JobBlock = flow.SourceJobBlock },
                new { Id = flow.TargetId, Label = flow.TargetLabel, Kind = flow.TargetKind, JobBlock = flow.TargetJobBlock }
            })
            .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var node = group.First();
                var jobBlocks = GetJobBlockCount(group.Key, allocationsByNodeId, jobRequirementsById);
                return new { id = group.Key, label = node.Label, kind = node.Kind, jobBlock = node.JobBlock, jobBlocks };
            })
            .ToArray();
        SankeyGraphJson = JsonSerializer.Serialize(new
        {
            mode = SelectedVisualisationIndex,
            layout = new
            {
                direction = NodeLayoutDirection == NodeLayoutDirection.Down ? "DOWN" : "RIGHT",
                nodeSpacing = NodeSpacing,
                layerSpacing = LayerSpacing
            },
            nodes = sankeyNodes,
            links = flows.Select(flow => new { source = flow.SourceId, target = flow.TargetId, value = flow.Amount, item = flow.ItemId }).ToArray()
        }, JsonDefaults.Options);

        var nodeMetadata = new Dictionary<string, (string Label, string Kind)>(StringComparer.OrdinalIgnoreCase);
        foreach (var flow in flows)
        {
            nodeMetadata.TryAdd(flow.SourceId, (flow.SourceLabel, flow.SourceKind));
            nodeMetadata.TryAdd(flow.TargetId, (flow.TargetLabel, flow.TargetKind));
        }
        var layers = nodeMetadata.Keys.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        for (var pass = 0; pass < nodeMetadata.Count; pass++)
        {
            foreach (var flow in flows)
            {
                layers[flow.TargetId] = Math.Min(nodeMetadata.Count, Math.Max(layers[flow.TargetId], layers[flow.SourceId] + 1));
            }
        }

        var nodes = nodeMetadata.Select(entry => new VisualGraphNode(entry.Key, entry.Value.Label, entry.Value.Kind)).OrderBy(node => node.Label).ToList();
        if (SelectedVisualisationIndex == 1)
        {
            var radiusX = Math.Max(220, nodes.Count * 23);
            var radiusY = Math.Max(170, nodes.Count * 16);
            for (var index = 0; index < nodes.Count; index++)
            {
                var angle = 2d * Math.PI * index / Math.Max(1, nodes.Count);
                nodes[index].X = 480d + radiusX * Math.Cos(angle);
                nodes[index].Y = 320d + radiusY * Math.Sin(angle);
            }
        }
        else
        {
            foreach (var group in nodes.GroupBy(node => layers[node.Id]).OrderBy(group => group.Key))
            {
                var ordered = group.OrderBy(node => node.Label).ToArray();
                for (var index = 0; index < ordered.Length; index++)
                {
                    ordered[index].X = 30d + group.Key * 220d;
                    ordered[index].Y = 30d + index * 92d;
                }
            }
        }

        foreach (var node in nodes) VisualGraphNodes.Add(node);
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.OrdinalIgnoreCase);
        var largestFlow = flows.Max(flow => flow.Amount);
        foreach (var flow in flows)
        {
            var source = byId[flow.SourceId];
            var target = byId[flow.TargetId];
            VisualGraphLinks.Add(new VisualGraphLink(
                source.X + VisualGraphNode.Width,
                source.Y + VisualGraphNode.Height / 2d,
                target.X,
                target.Y + VisualGraphNode.Height / 2d,
                SelectedVisualisationIndex == 0 ? Math.Clamp(2d + 16d * (double)(flow.Amount / largestFlow), 2d, 18d) : 2d,
                $"{flow.ItemId}: {flow.Amount:0.##}"));
        }
    }

    private void RefreshRecentPlans()
    {
        RecentPlans.Clear();
        foreach (var path in _userSettings.RecentPlans.Where(File.Exists)) RecentPlans.Add(path);
    }

    private void SaveVisualisationSettings()
    {
        _userSettings.NodeLayoutDirection = NodeLayoutDirection.ToString();
        _userSettings.NodeSpacing = NodeSpacing;
        _userSettings.LayerSpacing = LayerSpacing;
        _settingsStore.Save(_userSettings);
    }

    private static long? GetJobBlockCount(
        string nodeId,
        IReadOnlyDictionary<string, RecipeAllocation> allocationsByNodeId,
        IReadOnlyDictionary<string, JobRequirement> jobRequirementsById)
    {
        if (!allocationsByNodeId.TryGetValue(nodeId, out var allocation))
        {
            return null;
        }

        if (!jobRequirementsById.TryGetValue(allocation.JobTypeId, out var requirement) || requirement.BlockCount <= 0)
        {
            return null;
        }

        var capacityPerBlock = requirement.CapacitySeconds / requirement.BlockCount;
        return capacityPerBlock <= 0m
            ? requirement.BlockCount
            : Math.Max(1L, (long)Math.Ceiling(allocation.WorkloadSeconds / capacityPerBlock));
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static void ShowError(string message) => MessageBox.Show(message, "Colony Optimiser", MessageBoxButton.OK, MessageBoxImage.Warning);
}

public sealed record ItemOption(string Id, string DisplayName, string? IconPath)
{
    public override string ToString() => DisplayName;
}

public partial class DemandRow : ObservableObject
{
    public DemandRow(string itemId, string displayName, decimal amount, DemandUnit unit, string? iconPath = null)
    {
        ItemId = itemId;
        DisplayName = displayName;
        IconPath = iconPath;
        Amount = amount;
        Unit = unit;
    }

    public string ItemId { get; }
    public string DisplayName { get; }
    public string? IconPath { get; }
    [ObservableProperty] private decimal amount;
    [ObservableProperty] private DemandUnit unit;
}

public sealed record ExternalItemRow(string ItemId, string DisplayName, string? IconPath = null);

public sealed record GuardAmmoModeOption(GuardAmmoMode Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public partial class CropSourceRow : ObservableObject
{
    private readonly CropFarmSourceDefinition _source;

    public CropSourceRow(CropFarmSourceDefinition source, GameTiming timing, GameDatabase database)
    {
        _source = source;
        Id = source.Id;
        DisplayName = source.DisplayName;
        JobDisplayName = ColonyOptimizer.Core.DisplayName.FromIdentifier(source.JobTypeId);
        DefaultFieldTiles = source.DefaultFieldTiles;
        FieldWidth = 10;
        FieldLength = Math.Max(1, (int)Math.Ceiling(source.DefaultFieldTiles / 10m));
        IconPath = database.Items.FirstOrDefault(item => item.Id.Equals(source.Outputs[0].ItemId, StringComparison.OrdinalIgnoreCase))?.IconPath;
        UpdateTiming(timing);
        FertilityRequirement = source.FertilityRequirement;
        RequiredScience = source.RequiredScience is null ? "None" : ColonyOptimizer.Core.DisplayName.FromIdentifier(source.RequiredScience);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string JobDisplayName { get; }
    public string? IconPath { get; }
    public string AreaJobType => "Crop farm";
    public int WorkerCount => 1;
    public int AreaWidth
    {
        get => FieldWidth;
        set => FieldWidth = value;
    }
    public int AreaLength
    {
        get => FieldLength;
        set => FieldLength = value;
    }
    public string AreaCapacity => $"{FieldTiles} tiles";
    public string Cadence => Growth;
    public int DefaultFieldTiles { get; }
    [ObservableProperty] private string growth = string.Empty;
    public int FertilityRequirement { get; }
    public string RequiredScience { get; }
    public int FieldTiles => Math.Max(1, FieldWidth) * Math.Max(1, FieldLength);
    public string OutputPerCycle => string.Join(", ", _source.Outputs.Select(output => $"{output.ExpectedAmount * FieldTiles / _source.GrowthCyclesPerHarvest:0.##} {ColonyOptimizer.Core.DisplayName.FromIdentifier(output.ItemId)}"));
    [ObservableProperty] private int fieldWidth;
    [ObservableProperty] private int fieldLength;

    partial void OnFieldWidthChanged(int value)
    {
        OnPropertyChanged(nameof(FieldTiles));
        OnPropertyChanged(nameof(OutputPerCycle));
        OnPropertyChanged(nameof(AreaWidth));
        OnPropertyChanged(nameof(AreaCapacity));
    }

    partial void OnFieldLengthChanged(int value)
    {
        OnPropertyChanged(nameof(FieldTiles));
        OnPropertyChanged(nameof(OutputPerCycle));
        OnPropertyChanged(nameof(AreaLength));
        OnPropertyChanged(nameof(AreaCapacity));
    }

    public void UpdateTiming(GameTiming timing)
    {
        Growth = $"{_source.StageCount - 1} night{(_source.StageCount == 2 ? string.Empty : "s")} / {_source.GrowthCyclesPerHarvest * timing.CycleSeconds / 60m:0.##} real min";
        OnPropertyChanged(nameof(Cadence));
    }
}

public partial class ForestrySourceRow : ObservableObject
{
    private readonly ForestrySourceDefinition _source;

    public ForestrySourceRow(ForestrySourceDefinition source, GameDatabase database)
    {
        _source = source;
        Id = source.Id;
        DisplayName = source.DisplayName;
        LogIconPath = database.Items.FirstOrDefault(item => item.Id.Equals(source.LogItemId, StringComparison.OrdinalIgnoreCase))?.IconPath;
        LeavesIconPath = database.Items.FirstOrDefault(item => item.Id.Equals(source.LeavesItemId, StringComparison.OrdinalIgnoreCase))?.IconPath;
        DefaultForesterCount = source.DefaultForesterCount;
        DefaultPlotWidth = source.DefaultPlotWidth;
        DefaultPlotLength = source.DefaultPlotLength;
        ForesterCount = source.DefaultForesterCount;
        PlotWidth = source.DefaultPlotWidth;
        PlotLength = source.DefaultPlotLength;
        RequiredScience = source.RequiredScience is null ? "None" : DisplayNameFromId(source.RequiredScience);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string? LogIconPath { get; }
    public string? LeavesIconPath { get; }
    public string? IconPath => LogIconPath;
    public string AreaJobType => "Forestry";
    public int WorkerCount
    {
        get => ForesterCount;
        set => ForesterCount = value;
    }
    public int AreaWidth
    {
        get => PlotWidth;
        set => PlotWidth = value;
    }
    public int AreaLength
    {
        get => PlotLength;
        set => PlotLength = value;
    }
    public string AreaCapacity => TreeCount;
    public string Cadence => HarvestRate;
    public int DefaultForesterCount { get; }
    public int DefaultPlotWidth { get; }
    public int DefaultPlotLength { get; }
    public string RequiredScience { get; }
    public string OutputPerCycle => $"{HarvestedTrees * _source.LogsPerTree} {DisplayNameFromId(_source.LogItemId)}, {HarvestedTrees * _source.LeavesPerTree} {DisplayNameFromId(_source.LeavesItemId)}";
    public string TreeCount => $"{TotalTrees} trees";
    public string HarvestRate => $"{HarvestedTrees}/{TotalTrees} trees/cycle";
    [ObservableProperty] private int foresterCount;
    [ObservableProperty] private int plotWidth;
    [ObservableProperty] private int plotLength;

    private int TotalTrees => ForestryLayout.GetTreeSlotCount(PlotWidth, PlotLength);
    private int HarvestedTrees => Math.Min(TotalTrees, Math.Max(1, ForesterCount) * _source.TreesPerForesterPerCycle);
    private static string DisplayNameFromId(string id) => ColonyOptimizer.Core.DisplayName.FromIdentifier(id);

    partial void OnForesterCountChanged(int value)
    {
        OnPropertyChanged(nameof(OutputPerCycle));
        OnPropertyChanged(nameof(HarvestRate));
        OnPropertyChanged(nameof(WorkerCount));
        OnPropertyChanged(nameof(Cadence));
    }

    partial void OnPlotWidthChanged(int value)
    {
        OnPropertyChanged(nameof(TreeCount));
        OnPropertyChanged(nameof(OutputPerCycle));
        OnPropertyChanged(nameof(HarvestRate));
        OnPropertyChanged(nameof(AreaWidth));
        OnPropertyChanged(nameof(AreaCapacity));
        OnPropertyChanged(nameof(Cadence));
    }

    partial void OnPlotLengthChanged(int value)
    {
        OnPropertyChanged(nameof(TreeCount));
        OnPropertyChanged(nameof(OutputPerCycle));
        OnPropertyChanged(nameof(HarvestRate));
        OnPropertyChanged(nameof(AreaLength));
        OnPropertyChanged(nameof(AreaCapacity));
        OnPropertyChanged(nameof(Cadence));
    }
}

public partial class SelectableEntry : ObservableObject
{
    public SelectableEntry(string id, string displayName, bool isSelected, string? iconPath = null, bool isSectionStart = false)
    {
        Id = id;
        DisplayName = displayName;
        IconPath = iconPath;
        IsSectionStart = isSectionStart;
        IsSelected = isSelected;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string? IconPath { get; }
    public bool IsSectionStart { get; }
    [ObservableProperty] private bool isSelected;
}

public partial class RecipeRow : ObservableObject
{
    public RecipeRow(RecipeDefinition recipe, bool isAutomatedQueue, GameDatabase database, IEnumerable<string>? relatedRecipeIds = null)
    {
        Id = recipe.Id;
        DisplayName = recipe.DisplayName;
        JobTypeId = recipe.JobTypeId;
        CooldownSeconds = recipe.CooldownSeconds;
        RequiredScience = recipe.RequiredScience ?? "None";
        SourceFile = recipe.SourceFile;
        IsAutomatedQueue = isAutomatedQueue;
        RelatedRecipeIds = (relatedRecipeIds ?? [recipe.Id]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Ingredients = string.Join(", ", recipe.Inputs.Select(amount => $"{amount.Amount:0.##} {DisplayItem(database, amount.ItemId)}"));
        OutputName = string.Join(", ", recipe.Outputs.Select(amount => $"{amount.Amount:0.##} {DisplayItem(database, amount.ItemId)}"));
        OutputIconPath = recipe.Outputs.Select(output => database.Items.FirstOrDefault(item => item.Id.Equals(output.ItemId, StringComparison.OrdinalIgnoreCase))?.IconPath).FirstOrDefault(icon => !string.IsNullOrWhiteSpace(icon));
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string JobTypeId { get; }
    public decimal CooldownSeconds { get; }
    public string RequiredScience { get; }
    public string SourceFile { get; }
    public IReadOnlyList<string> RelatedRecipeIds { get; }
    public string Ingredients { get; }
    public string OutputName { get; }
    public string? OutputIconPath { get; }
    public bool IsAutomatedQueue { get; }
    public string Mode => IsAutomatedQueue ? "Queued machine" : "Worker job";
    [ObservableProperty] private RecipePolicy policy = RecipePolicy.Allowed;
    [ObservableProperty] private bool isVisible = true;

    private static string DisplayItem(GameDatabase database, string itemId) => database.Items.FirstOrDefault(item => item.Id.Equals(itemId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? ColonyOptimizer.Core.DisplayName.FromIdentifier(itemId);
}

public partial class GuardRow : ObservableObject
{
    private GameTiming _timing;

    public GuardRow(GuardTypeDefinition guard, GameTiming timing, GameDatabase database)
    {
        _timing = timing;
        Id = guard.Id;
        DisplayName = guard.DisplayName;
        GuardShift = guard.Shift;
        Ammo = string.Join(", ", guard.Ammunition.Select(ammo => $"{ammo.Amount:0.##} {database.Items.FirstOrDefault(item => item.Id.Equals(ammo.ItemId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? ColonyOptimizer.Core.DisplayName.FromIdentifier(ammo.ItemId)}"));
        AmmoIconPath = guard.Ammunition.Select(ammo => database.Items.FirstOrDefault(item => item.Id.Equals(ammo.ItemId, StringComparison.OrdinalIgnoreCase))?.IconPath).FirstOrDefault(icon => !string.IsNullOrWhiteSpace(icon));
        CooldownSeconds = guard.CooldownShotSeconds;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public GuardShift GuardShift { get; }
    public string Shift => $"{GuardShift} ({_timing.GetGuardShiftSeconds(GuardShift):0.#} s)";
    public string Ammo { get; }
    public string? AmmoIconPath { get; }
    public decimal CooldownSeconds { get; }
    public int EstimatedShotsPerCycle => AmmoMode switch
    {
        GuardAmmoMode.CustomRoundsPerCycle => Math.Max(0, CustomRoundsPerCycle ?? 0),
        GuardAmmoMode.HostilePeriodOnly => RoundShots(_timing.GetHostileGuardOverlapSeconds(GuardShift)),
        GuardAmmoMode.CustomUtilisation => RoundShots(_timing.GetGuardShiftSeconds(GuardShift), UtilisationPercent),
        _ => RoundShots(_timing.GetGuardShiftSeconds(GuardShift))
    };
    [ObservableProperty] private int count;
    [ObservableProperty] private GuardAmmoMode ammoMode = GuardAmmoMode.EntireShiftWorstCase;
    [ObservableProperty] private decimal utilisationPercent = 100m;
    [ObservableProperty] private int? customRoundsPerCycle;

    partial void OnAmmoModeChanged(GuardAmmoMode value) => OnPropertyChanged(nameof(EstimatedShotsPerCycle));
    partial void OnUtilisationPercentChanged(decimal value) => OnPropertyChanged(nameof(EstimatedShotsPerCycle));
    partial void OnCustomRoundsPerCycleChanged(int? value) => OnPropertyChanged(nameof(EstimatedShotsPerCycle));

    public void UpdateTiming(GameTiming timing)
    {
        _timing = timing;
        OnPropertyChanged(nameof(Shift));
        OnPropertyChanged(nameof(EstimatedShotsPerCycle));
    }

    private int RoundShots(decimal durationSeconds, decimal utilisation = 100m) =>
        CooldownSeconds <= 0m
            ? 0
            : (int)Math.Ceiling(durationSeconds / CooldownSeconds * Math.Clamp(utilisation, 0m, 100m) / 100m);
}

public partial class TrapRow : ObservableObject
{
    public TrapRow(TrapDefinition trap, GameDatabase database)
    {
        Id = trap.Id;
        DisplayName = trap.DisplayName;
        IconPath = trap.IconPath;
        Ammunition = database.Items.FirstOrDefault(item => item.Id.Equals(trap.AmmunitionItemId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? ColonyOptimizer.Core.DisplayName.FromIdentifier(trap.AmmunitionItemId);
        AmmunitionIconPath = database.Items.FirstOrDefault(item => item.Id.Equals(trap.AmmunitionItemId, StringComparison.OrdinalIgnoreCase))?.IconPath;
        AmmunitionCapacity = trap.AmmunitionCapacity;
        ReloadSecondsPerAmmunition = trap.ReloadSecondsPerAmmunition;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string? IconPath { get; }
    public string Ammunition { get; }
    public string? AmmunitionIconPath { get; }
    public int AmmunitionCapacity { get; }
    public decimal ReloadSecondsPerAmmunition { get; }
    public int FullReloadPerCycle => Math.Max(0, Count) * AmmunitionCapacity;
    [ObservableProperty] private int count;

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(FullReloadPerCycle));
}

public sealed record GraphRootOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record WorldSaveOption(string Path)
{
    public string DisplayName => $"{System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path))} - {Path}";
    public override string ToString() => DisplayName;
}

public enum NodeLayoutDirection
{
    Right,
    Down
}

public sealed class VisualGraphNode
{
    public const double Width = 180d;
    public const double Height = 58d;

    public VisualGraphNode(string id, string label, string kind)
    {
        Id = id;
        Label = label;
        Kind = kind;
    }

    public string Id { get; }
    public string Label { get; }
    public string Kind { get; }
    public string Fill => Kind.Equals("Recipe", StringComparison.OrdinalIgnoreCase) ? "#315A4C" : "#1F5361";
    public string Border => Kind.Equals("Recipe", StringComparison.OrdinalIgnoreCase) ? "#83D3A5" : "#75C9D8";
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed record VisualGraphLink(double X1, double Y1, double X2, double Y2, double Thickness, string ToolTip);
