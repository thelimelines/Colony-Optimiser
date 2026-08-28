using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
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
    private bool _isLoadingVisualisationSettings;
    private bool _isVisualisationActive;
    private readonly DispatcherTimer _visualisationRefreshDebounceTimer;

    public MainWindowViewModel()
    {
        _visualisationRefreshDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _visualisationRefreshDebounceTimer.Tick += OnVisualisationRefreshDebounceTimerTick;
        RecipeRowsView = CollectionViewSource.GetDefaultView(RecipeRows);
        RecipeRowsView.Filter = MatchesRecipeSearch;
    }

    public BulkObservableCollection<ItemOption> FilteredItems { get; } = [];
    public BulkObservableCollection<DemandRow> Targets { get; } = [];
    public BulkObservableCollection<ExternalItemRow> ExternalItems { get; } = [];
    public BulkObservableCollection<SelectableEntry> ScienceRows { get; } = [];
    public BulkObservableCollection<SelectableEntry> ToolRows { get; } = [];
    public BulkObservableCollection<CropSourceRow> CropSourceRows { get; } = [];
    public BulkObservableCollection<ForestrySourceRow> ForestrySourceRows { get; } = [];
    public BulkObservableCollection<object> AreaJobRows { get; } = [];
    public BulkObservableCollection<RecipeRow> RecipeRows { get; } = [];
    public ICollectionView RecipeRowsView { get; }
    public BulkObservableCollection<GuardRow> GuardRows { get; } = [];
    public BulkObservableCollection<TrapRow> TrapRows { get; } = [];
    public BulkObservableCollection<JobRequirement> JobResults { get; } = [];
    public BulkObservableCollection<RecipeAllocation> AllocationResults { get; } = [];
    public BulkObservableCollection<ToolRequirement> ToolResults { get; } = [];
    public BulkObservableCollection<ExternalRequirement> ExternalResults { get; } = [];
    public BulkObservableCollection<ProductionOutput> OutputResults { get; } = [];
    public ObservableCollection<string> RecentPlans { get; } = [];
    public ObservableCollection<WorldSaveOption> WorldSaveOptions { get; } = [];
    public ObservableCollection<ColonyGroupImportOption> ColonyGroupOptions { get; } = [];

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
    [ObservableProperty] private bool isVisualisationRendering;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private decimal efficiencyPercent = 100m;
    [ObservableProperty] private decimal headroomPercent;
    [ObservableProperty] private OptimizationObjective selectedObjective = OptimizationObjective.FewestWorkers;
    [ObservableProperty] private StochasticOutputPolicy selectedStochasticPolicy = StochasticOutputPolicy.ExpectedValue;
    [ObservableProperty] private string resultHeadline = "No calculation yet";
    [ObservableProperty] private string resultDetail = "Add production targets, configure progression, then optimise.";
    [ObservableProperty] private FoodCoverageSummary? foodCoverage;
    [ObservableProperty] private string selectedPlanName = "Untitled plan";
    [ObservableProperty] private bool isSettingsOpen;
    [ObservableProperty] private bool isWorldSelectionOpen;
    [ObservableProperty] private WorldSaveOption? selectedWorldSave;
    [ObservableProperty] private ColonyGroupImportOption? selectedColonyGroup;
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

    public bool HasColonyGroupOptions => ColonyGroupOptions.Count > 0;

    public bool HasFoodCoverage => FoodCoverage is not null;

    public bool HasVisualisationGraph => _lastResult is { IsFeasible: true }
        && _lastResult.ProductionFlows.Any(flow => flow.Amount > 0m);

    public bool IsNodeVisualiserSelected => SelectedVisualisationIndex == 1;

    public string VisualisationLayoutJson => JsonSerializer.Serialize(CreateVisualisationLayout(), JsonDefaults.Options);

    public bool HasCoreIconAssets => _database is not null
        && new[] { "coppertools", "wheat", "alkanet" }.All(id =>
        {
            var path = _database.FindItem(id)?.IconPath;
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        })
        && _database.Traps.All(trap =>
        {
            var ammunitionIcon = _database.FindItem(trap.AmmunitionItemId)?.IconPath;
            return !string.IsNullOrWhiteSpace(trap.IconPath)
                && File.Exists(trap.IconPath)
                && !string.IsNullOrWhiteSpace(ammunitionIcon)
                && File.Exists(ammunitionIcon);
        });

    public async Task InitializeAsync()
    {
        _userSettings = _settingsStore.Load();
        LinkedSaveGamePath = _userSettings.LinkedSaveGamePath ?? string.Empty;
        await RefreshColonyGroupOptionsAsync();
        _isLoadingVisualisationSettings = true;
        try
        {
            if (Enum.TryParse<NodeLayoutDirection>(_userSettings.NodeLayoutDirection, ignoreCase: true, out var savedLayoutDirection))
            {
                NodeLayoutDirection = savedLayoutDirection;
            }
            NodeSpacing = Math.Clamp(_userSettings.NodeSpacing ?? NodeSpacing, 0, 160);
            LayerSpacing = Math.Clamp(_userSettings.LayerSpacing ?? LayerSpacing, 0, 240);
        }
        finally
        {
            _isLoadingVisualisationSettings = false;
        }
        _userSettings.NodeLayoutDirection = NodeLayoutDirection.ToString();
        _userSettings.NodeSpacing = NodeSpacing;
        _userSettings.LayerSpacing = LayerSpacing;
        foreach (var recentPlanPath in _userSettings.RecentPlans.Where(File.Exists))
        {
            RecentPlans.Add(recentPlanPath);
        }

        var path = _userSettings.LastGameDataDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            try
            {
                path = _acquisition.FindInstalledGameDataDirectories().FirstOrDefault();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                FileLogger.Write(exception, "discover-installed-game-data-at-startup");
                StatusText = "Game data was not found automatically. Choose a folder in Settings to continue.";
            }
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

    public void SetVisualisationActive(bool isActive)
    {
        if (_isVisualisationActive == isActive)
        {
            return;
        }

        _isVisualisationActive = isActive;
        if (isActive)
        {
            RefreshVisualisation();
        }
    }
    partial void OnNodeLayoutDirectionChanged(NodeLayoutDirection value)
    {
        DebounceVisualisationLayoutUpdate();
    }
    partial void OnNodeSpacingChanged(int value)
    {
        if (value is < 0 or > 160)
        {
            NodeSpacing = Math.Clamp(value, 0, 160);
            return;
        }

        DebounceVisualisationLayoutUpdate();
    }
    partial void OnLayerSpacingChanged(int value)
    {
        if (value is < 0 or > 240)
        {
            LayerSpacing = Math.Clamp(value, 0, 240);
            return;
        }

        DebounceVisualisationLayoutUpdate();
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
    partial void OnSelectedColonyGroupChanged(ColonyGroupImportOption? value)
    {
        if (value?.RequiresReselection == true)
        {
            return;
        }

        _userSettings.LinkedSaveColonyGroupIdentity = value?.StableIdentity;
        _userSettings.LinkedSaveColonyGroupRowId = null;
        _settingsStore.Save(_userSettings);
    }
    partial void OnFoodCoverageChanged(FoodCoverageSummary? value) => OnPropertyChanged(nameof(HasFoodCoverage));

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private async Task ChooseSaveGameAsync()
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

        await LinkSaveGameAsync(dialog.FileName, importProgression: true);
    }

    [RelayCommand]
    private async Task ImportLinkedSaveAsync()
    {
        await RefreshColonyGroupOptionsAsync();
        TryApplyLinkedSave(showError: true);
    }

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
    private async Task UseSelectedWorldSaveAsync()
    {
        if (SelectedWorldSave is not null)
        {
            await LinkSaveGameAsync(SelectedWorldSave.Path, importProgression: true);
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
            var previousPlan = CaptureCurrentPlanState();
            var database = await Task.Run(() => _loader.Load(selectedPath));
            ApplyDatabase(database, previousPlan);
            var restoredPlan = await RestoreLastPlanIfAvailableAsync();
            if (ShouldAutomaticallyImportLinkedSave(previousPlan, restoredPlan))
            {
                TryApplyLinkedSave();
            }
            else
            {
                ReportDeferredLinkedSaveImport();
            }
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
            var previousPlan = CaptureCurrentPlanState();
            var downloaded = await _acquisition.DownloadLatestAsync();
            DataDirectory = downloaded.GameDataPath;
            var database = await Task.Run(() => _loader.Load(downloaded.GameDataPath));
            database.Source = new GameDataSourceInfo("GitHub cache", downloaded.GameDataPath, database.Source.Version, downloaded.Commit, downloaded.DownloadedAt);
            ApplyDatabase(database, previousPlan);
            var restoredPlan = await RestoreLastPlanIfAvailableAsync();
            if (ShouldAutomaticallyImportLinkedSave(previousPlan, restoredPlan))
            {
                TryApplyLinkedSave();
            }
            else
            {
                ReportDeferredLinkedSaveImport();
            }
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

        var wroughtIron = _database.FindItem("ironwrought");
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
        foreach (var science in ScienceRows) science.IsSelected = false;
        foreach (var tool in ToolRows) tool.IsSelected = false;
        GuardRows.ToList().ForEach(row => row.Count = 0);
        TrapRows.ToList().ForEach(row => row.Count = 0);
        RecipeRows.ToList().ForEach(row => row.Policy = RecipePolicy.Allowed);
        foreach (var cropSource in CropSourceRows)
        {
            var layout = CropFarmLayout.CreateDefault(cropSource.DefaultFieldTiles);
            cropSource.FieldWidth = layout.Width;
            cropSource.FieldLength = layout.Length;
        }
        foreach (var forestrySource in ForestrySourceRows)
        {
            forestrySource.ForesterCount = forestrySource.DefaultForesterCount;
            forestrySource.PlotWidth = forestrySource.DefaultPlotWidth;
            forestrySource.PlotLength = forestrySource.DefaultPlotLength;
        }
        EfficiencyPercent = 100m;
        HeadroomPercent = 0m;
        SelectedObjective = OptimizationObjective.FewestWorkers;
        SelectedStochasticPolicy = StochasticOutputPolicy.ExpectedValue;
        LoadTimingEditor(_database?.Timing ?? GameTiming.Default);
        SelectedPlanName = "Untitled plan";
        _currentPlanPath = null;
        ClearResults();
        StatusText = "New blank plan";
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

        await WriteExportAsync(dialog.FileName, content.ToString(), "CSV");
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
        await WriteExportAsync(dialog.FileName, JsonSerializer.Serialize(result, JsonDefaults.Options), "JSON");
    }

    private SavedPlanDocument? CaptureCurrentPlanState() => _database is null
        ? null
        : new SavedPlanDocument
        {
            Plan = BuildPlan(),
            Settings = BuildSettings(),
            DataSource = _database.Source
        };

    private void ApplyDatabase(GameDatabase database, SavedPlanDocument? previousPlan = null)
    {
        IconPathToImageConverter.ClearCache();
        _database = database;
        DataDirectory = database.Source.SourcePath;
        DataSourceDisplay = $"{database.Source.SourceType}: {database.Source.SourcePath}" +
            (string.IsNullOrWhiteSpace(database.Source.Commit) ? string.Empty : $"  Commit {database.Source.Commit[..Math.Min(8, database.Source.Commit.Length)]}");
        LoadTimingEditor(database.Timing);
        DiagnosticsText = BuildDiagnostics(database);

        _plannerItemIds = GetPlannerItemIds(database);
        RefreshItemFilter();

        ScienceRows.ReplaceWith(database.Sciences
            .OrderBy(science => science.DisplayName)
            .Select(science => new SelectableEntry(science.Id, science.DisplayName, true)));

        ToolRows.ReplaceWith(database.Tools
            .OrderBy(tool => GetToolSortOrder(tool.Id))
            .ThenBy(tool => tool.DisplayName)
            .Select(tool => new SelectableEntry(tool.Id, tool.DisplayName, true, database.FindItem(tool.Id)?.IconPath, IsToolSectionStart(tool.Id))));

        var cropRows = database.CropFarmSources
            .OrderBy(source => source.DisplayName)
            .Select(source => new CropSourceRow(source, EffectiveTiming, database))
            .ToArray();
        CropSourceRows.ReplaceWith(cropRows);

        var forestryRows = database.ForestrySources
            .OrderBy(source => source.DisplayName)
            .Select(source => new ForestrySourceRow(source, EffectiveTiming, database))
            .ToArray();
        ForestrySourceRows.ReplaceWith(forestryRows);
        AreaJobRows.ReplaceWith(cropRows.Cast<object>().Concat(forestryRows));

        var automatedJobs = database.Jobs.Where(job => job.IsAutomatedQueue).Select(job => job.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var productionRecipes = database.Recipes.Where(recipe => !recipe.JobTypeId.Equals("player", StringComparison.OrdinalIgnoreCase)).ToArray();
        var alternateOutputIds = productionRecipes.SelectMany(recipe => recipe.Outputs.Select(output => new { recipe.Id, output.ItemId }))
            .GroupBy(entry => entry.ItemId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        RecipeRows.ReplaceWith(productionRecipes
            .Where(recipe => recipe.Outputs.Any(output => alternateOutputIds.Contains(output.ItemId)))
            .GroupBy(GetMaterialRecipeSignature, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.First().Outputs[0].ItemId)
            .ThenBy(group => group.First().DisplayName)
            .Select(group =>
            {
                var representative = group.OrderBy(recipe => recipe.RequiredScience is null ? 0 : 1).ThenBy(recipe => recipe.Id).First();
                return new RecipeRow(representative, automatedJobs.Contains(representative.JobTypeId), database, group.Select(recipe => recipe.Id));
            }));

        GuardRows.ReplaceWith(database.Guards
            .OrderBy(guard => GetGuardSortOrder(guard.Id))
            .ThenBy(guard => guard.Shift)
            .Select(guard => new GuardRow(guard, EffectiveTiming, database)));

        TrapRows.ReplaceWith(database.Traps
            .OrderBy(trap => trap.DisplayName)
            .Select(trap => new TrapRow(trap, database)));

        ExternalItems.Clear();
        ClearResults();

        if (previousPlan is not null)
        {
            ApplyPlan(previousPlan.Plan, previousPlan.Settings);
        }
    }

    private void RefreshItemFilter()
    {
        if (_database is null)
        {
            return;
        }

        var selectedId = SelectedItem?.Id;
        var query = ItemSearch.Trim();
        FilteredItems.ReplaceWith(_database.Items
            .Where(item => _plannerItemIds.Contains(item.Id)
                && (string.IsNullOrWhiteSpace(query)
                    || item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.DisplayName)
            .Take(250)
            .Select(item => new ItemOption(item.Id, item.DisplayName, item.IconPath)));

        SelectedItem = !string.IsNullOrWhiteSpace(selectedId)
            ? FilteredItems.FirstOrDefault(item => item.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            : string.IsNullOrWhiteSpace(query) ? FilteredItems.FirstOrDefault() : null;
    }

    private void RefreshRecipeFilter()
    {
        RecipeRowsView.Refresh();
    }

    private bool MatchesRecipeSearch(object candidate) => candidate is RecipeRow row
        && MatchesRecipeSearch(row);

    private bool MatchesRecipeSearch(RecipeRow row)
    {
        var query = RecipeSearch.Trim();
        return string.IsNullOrWhiteSpace(query)
            || row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.JobTypeId.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private ProductionPlan BuildPlan()
    {
        var plan = new ProductionPlan { Name = SelectedPlanName };
        plan.Targets = Targets.Select(row => new DemandTarget { ItemId = row.ItemId, Amount = row.Amount, Unit = row.Unit }).ToList();
        plan.UnlockedSciences = ScienceRows.Where(row => row.IsSelected).Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        plan.AvailableTools = ToolRows.Where(row => row.IsSelected).Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        plan.RecipePolicies = RecipeRows
            .Where(row => row.Policy != RecipePolicy.Allowed)
            .SelectMany(row => row.RelatedRecipeIds.Select(id => new KeyValuePair<string, RecipePolicy>(id, row.Policy)))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        plan.CropFarmLayouts = CropSourceRows
            .Where(row => !CropFarmLayout.IsDefault(row.DefaultFieldTiles, row.FieldWidth, row.FieldLength))
            .ToDictionary(row => row.Id, row => new CropFarmLayout
            {
                Width = Math.Max(1, row.FieldWidth),
                Length = Math.Max(1, row.FieldLength)
            }, StringComparer.OrdinalIgnoreCase);
        plan.ForestryLayouts = ForestrySourceRows
            .Where(row => row.ForesterCount != row.DefaultForesterCount || row.PlotWidth != row.DefaultPlotWidth || row.PlotLength != row.DefaultPlotLength)
            .ToDictionary(row => row.Id, row => new ForestryLayout
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
        JobResults.ReplaceWith(result.JobRequirements.OrderByDescending(job => job.BlockCount));
        AllocationResults.ReplaceWith(result.RecipeAllocations
            .OrderBy(allocation => allocation.JobTypeId)
            .ThenBy(allocation => allocation.RecipeId)
            .Select(allocation =>
            {
                allocation.IconPath = _database?.FindRecipe(allocation.RecipeId)?.Outputs
                    .Select(output => _database.FindItem(output.ItemId)?.IconPath)
                    .FirstOrDefault(icon => !string.IsNullOrWhiteSpace(icon));
                return allocation;
            }));
        ToolResults.ReplaceWith(result.ToolRequirements.OrderBy(tool => tool.ToolDisplayName).ThenBy(tool => tool.JobDisplayName));
        ExternalResults.ReplaceWith(result.ExternalRequirements
            .OrderBy(requirement => requirement.ItemId)
            .Select(requirement =>
            {
                requirement.IconPath = _database?.FindItem(requirement.ItemId)?.IconPath;
                return requirement;
            }));
        OutputResults.ReplaceWith(result.TotalOutputs
            .OrderBy(output => output.ItemDisplayName)
            .Select(output =>
            {
                output.IconPath = _database?.FindItem(output.ItemId)?.IconPath;
                return output;
            }));
        if (!_isVisualisationActive)
        {
            SankeyGraphJson = "{\"mode\":0,\"nodes\":[],\"links\":[]}";
        }
        _lastResult = result;
        RefreshVisualisation();

        if (result.IsFeasible)
        {
            FoodCoverage = _database is null ? null : FoodCoverageSummary.Calculate(_database, result);
            var exactness = result.IsOptimal ? string.Empty : " (approximate)";
            ResultHeadline = $"{result.TotalWorkers:N0} production workers + {result.TotalMachineBlocks:N0} machine blocks{exactness}";
            ResultDetail = $"{result.JobRequirements.Count} job types | {result.RecipeAllocations.Count(allocation => allocation.IsAutomatedQueue)} queued machine outputs | {result.TotalOutputs.Count} planned outputs | {result.ExternalRequirements.Count} external inputs | solver {result.SolverStatus} | cycle {EffectiveTiming.CycleSeconds / 60m:0.##} minutes";
        }
        else
        {
            FoodCoverage = null;
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
        _lastResult = null;
        FoodCoverage = null;
        SankeyGraphJson = "{\"mode\":0,\"nodes\":[],\"links\":[]}";
        ResultHeadline = "No calculation yet";
        ResultDetail = "Add production targets, configure progression, then optimise.";
    }

    private async Task SavePlanToAsync(string path)
    {
        var plan = BuildPlan();
        plan.Name = Path.GetFileNameWithoutExtension(path);
        var document = new SavedPlanDocument { Plan = plan, Settings = BuildSettings(), DataSource = _database?.Source };
        try
        {
            await AtomicTextFile.WriteAsync(path, JsonSerializer.Serialize(document, JsonDefaults.Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            FileLogger.Write(exception, "save-plan");
            StatusText = "Plan could not be saved";
            ShowError("The plan could not be saved. Check that the folder is writable and the file is not open in another program.");
            return;
        }

        _currentPlanPath = path;
        SelectedPlanName = Path.GetFileNameWithoutExtension(path);
        _userSettings.LastPlanPath = path;
        _userSettings.LastPlanDirectory = Path.GetDirectoryName(path);
        _userSettings.AddRecentPlan(path);
        _settingsStore.Save(_userSettings);
        RefreshRecentPlans();
        StatusText = "Plan saved";
    }

    private async Task WriteExportAsync(string path, string content, string format)
    {
        try
        {
            await AtomicTextFile.WriteAsync(path, content);
            StatusText = $"{format} export created";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            FileLogger.Write(exception, $"export-{format.ToLowerInvariant()}");
            StatusText = $"{format} export could not be created";
            ShowError($"The {format} export could not be saved. Check that the folder is writable and the file is not open in another program.");
        }
    }

    private async Task<bool> OpenPlanFromAsync(string path)
    {
        try
        {
            var document = JsonSerializer.Deserialize<SavedPlanDocument>(await File.ReadAllTextAsync(path), JsonDefaults.Options)
                ?? throw new InvalidDataException("The plan file contains no plan document.");
            var sourceWarning = GameDataSourceComparison.GetDifferenceWarning(document.DataSource, _database?.Source);
            ApplyPlan(document.Plan, document.Settings);
            _currentPlanPath = path;
            SelectedPlanName = Path.GetFileNameWithoutExtension(path);
            _userSettings.LastPlanPath = path;
            _userSettings.LastPlanDirectory = Path.GetDirectoryName(path);
            _userSettings.AddRecentPlan(path);
            _settingsStore.Save(_userSettings);
            RefreshRecentPlans();
            StatusText = sourceWarning ?? "Plan loaded";
            return true;
        }
        catch (Exception exception)
        {
            FileLogger.Write(exception, "open-plan");
            ShowError($"The selected plan could not be opened: {exception.Message}");
            return false;
        }
    }

    private async Task<bool> RestoreLastPlanIfAvailableAsync()
    {
        if (_lastPlanRestored || string.IsNullOrWhiteSpace(_userSettings.LastPlanPath) || !File.Exists(_userSettings.LastPlanPath))
        {
            return false;
        }

        if (!await OpenPlanFromAsync(_userSettings.LastPlanPath))
        {
            return false;
        }

        _lastPlanRestored = true;
        return true;
    }

    private void ApplyPlan(ProductionPlan plan, OptimizationSettings settings)
    {
        Targets.ReplaceWith(plan.Targets.Select(target =>
        {
            var item = _database?.FindItem(target.ItemId);
            return new DemandRow(target.ItemId, item?.DisplayName ?? DisplayName.FromIdentifier(target.ItemId), target.Amount, target.Unit, item?.IconPath);
        }));
        ExternalItems.ReplaceWith(plan.ExternalItems.Select(itemId =>
        {
            var item = _database?.FindItem(itemId);
            return new ExternalItemRow(itemId, item?.DisplayName ?? DisplayName.FromIdentifier(itemId), item?.IconPath);
        }));
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

            var defaultLayout = CropFarmLayout.CreateDefault(cropSource.DefaultFieldTiles);
            cropSource.FieldWidth = defaultLayout.Width;
            cropSource.FieldLength = defaultLayout.Length;
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
        foreach (var forestrySource in ForestrySourceRows)
        {
            forestrySource.UpdateTiming(timing);
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
            if (File.Exists(LinkedSaveGamePath))
            {
                SaveImportStatus = "World linked — progression will import after game data is loaded.";
            }
            else if (showError)
            {
                SaveImportStatus = "No readable world.sqlite3 is linked.";
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
                _userSettings.LinkedSaveColonyGroupIdentity = null;
                _userSettings.LinkedSaveColonyGroupRowId = null;
                _userSettings.LastWorldSaveDirectory = Path.GetDirectoryName(detectedSave);
                ClearColonyGroupOptions();
                _settingsStore.Save(_userSettings);
                _ = RefreshColonyGroupOptionsAsync();
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
            if (SelectedColonyGroup?.RequiresReselection == true
                || (SelectedColonyGroup is null && (!string.IsNullOrWhiteSpace(_userSettings.LinkedSaveColonyGroupIdentity) || _userSettings.LinkedSaveColonyGroupRowId is not null)))
            {
                SaveImportStatus = "The saved colony-group selection could not be verified. Choose a current import scope before importing progression.";
                if (showError)
                {
                    ShowError(SaveImportStatus);
                }
                return;
            }

            var imported = _saveImporter.Import(LinkedSaveGamePath, SelectedColonyGroup?.Selection);
            foreach (var science in ScienceRows)
            {
                science.IsSelected = imported.UnlockedScienceIds.Contains(science.Id);
            }
            foreach (var tool in ToolRows)
            {
                var definition = _database.FindTool(tool.Id);
                tool.IsSelected = definition?.RequiredScience is null || imported.UnlockedScienceIds.Contains(definition.RequiredScience);
            }

            var groupScope = imported.ImportedColonyGroupIdentity is not null
                ? "the selected colony group"
                : imported.ImportedColonyGroupCount == 1
                    ? "the single colony group"
                    : $"{imported.ImportedColonyGroupCount:N0} colony groups (combined)";
            SaveImportStatus = $"Imported {imported.UnlockedScienceIds.Count:N0} completed sciences from {groupScope}; tool limits now match their unlocks.";
            StatusText = "Save progress imported";
        }
        catch (SelectedColonyGroupUnreadableException exception)
        {
            FileLogger.Write(exception, "import-save-selected-colony-group");
            SaveImportStatus = "The selected colony group could not be read; existing progression was not changed.";
            if (showError)
            {
                ShowError(SaveImportStatus);
            }
        }
        catch (SelectedColonyGroupMissingException exception)
        {
            FileLogger.Write(exception, "import-save-selected-colony-group");
            SaveImportStatus = "The selected colony group is no longer present in this save; existing progression was not changed.";
            if (showError)
            {
                ShowError(SaveImportStatus);
            }
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

    private async Task LinkSaveGameAsync(string path, bool importProgression)
    {
        var changedWorld = !path.Equals(LinkedSaveGamePath, StringComparison.OrdinalIgnoreCase);
        LinkedSaveGamePath = path;
        _userSettings.LinkedSaveGamePath = path;
        if (changedWorld)
        {
            _userSettings.LinkedSaveColonyGroupIdentity = null;
            _userSettings.LinkedSaveColonyGroupRowId = null;
            ClearColonyGroupOptions();
        }
        _userSettings.LastWorldSaveDirectory = Path.GetDirectoryName(path);
        _settingsStore.Save(_userSettings);
        await RefreshColonyGroupOptionsAsync();
        if (importProgression)
        {
            TryApplyLinkedSave(showError: true);
        }
    }

    private static bool ShouldAutomaticallyImportLinkedSave(SavedPlanDocument? previousPlan, bool restoredPlan) => previousPlan is null && !restoredPlan;

    private void ReportDeferredLinkedSaveImport()
    {
        if (File.Exists(LinkedSaveGamePath))
        {
            SaveImportStatus = "Linked save was not re-imported to preserve the current plan. Select Import progression to update it.";
        }
    }

    private async Task RefreshColonyGroupOptionsAsync()
    {
        var worldPath = LinkedSaveGamePath;
        if (!File.Exists(worldPath))
        {
            SetColonyGroupOptions([]);
            return;
        }

        try
        {
            var groups = await Task.Run(() => _saveImporter.GetColonyGroups(worldPath));
            if (!worldPath.Equals(LinkedSaveGamePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SetColonyGroupOptions(groups);
        }
        catch (Exception exception)
        {
            FileLogger.Write(exception, "read-save-colony-groups");
            if (worldPath.Equals(LinkedSaveGamePath, StringComparison.OrdinalIgnoreCase))
            {
                SetColonyGroupOptions([]);
            }
        }
    }

    private void SetColonyGroupOptions(IReadOnlyList<SaveGameColonyGroup> groups)
    {
        ColonyGroupOptions.Clear();
        if (groups.Count > 0)
        {
            ColonyGroupOptions.Add(ColonyGroupImportOption.Combined);
            foreach (var group in groups)
            {
                ColonyGroupOptions.Add(ColonyGroupImportOption.FromGroup(group));
            }
        }

        var savedIdentity = _userSettings.LinkedSaveColonyGroupIdentity;
        var selectedOption = !string.IsNullOrWhiteSpace(savedIdentity)
            ? ColonyGroupOptions.FirstOrDefault(option => option.StableIdentity == savedIdentity)
            : null;
        if (selectedOption is null && !string.IsNullOrWhiteSpace(savedIdentity))
        {
            selectedOption = ColonyGroupImportOption.Missing(savedIdentity);
            ColonyGroupOptions.Add(selectedOption);
        }
        else if (selectedOption is null && _userSettings.LinkedSaveColonyGroupRowId is { } legacyRowId)
        {
            selectedOption = ColonyGroupImportOption.Legacy(legacyRowId);
            ColonyGroupOptions.Add(selectedOption);
        }

        SelectedColonyGroup = selectedOption ?? ColonyGroupOptions.FirstOrDefault();
        OnPropertyChanged(nameof(HasColonyGroupOptions));
    }

    private void ClearColonyGroupOptions()
    {
        ColonyGroupOptions.Clear();
        SelectedColonyGroup = null;
        OnPropertyChanged(nameof(HasColonyGroupOptions));
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

    private void DebounceVisualisationLayoutUpdate()
    {
        if (_isLoadingVisualisationSettings)
        {
            return;
        }

        _visualisationRefreshDebounceTimer.Stop();
        _visualisationRefreshDebounceTimer.Start();
    }

    private void OnVisualisationRefreshDebounceTimerTick(object? sender, EventArgs eventArgs)
    {
        _visualisationRefreshDebounceTimer.Stop();
        SaveVisualisationSettings();
        OnPropertyChanged(nameof(VisualisationLayoutJson));
    }

    private void RefreshVisualisation()
    {
        if (!_isVisualisationActive)
        {
            return;
        }

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
            layout = CreateVisualisationLayout(),
            nodes = sankeyNodes,
            links = flows.Select(flow => new { source = flow.SourceId, target = flow.TargetId, value = flow.Amount, item = flow.ItemId }).ToArray()
        }, JsonDefaults.Options);
    }

    private object CreateVisualisationLayout() => new
    {
        direction = NodeLayoutDirection == NodeLayoutDirection.Down ? "DOWN" : "RIGHT",
        nodeSpacing = NodeSpacing,
        layerSpacing = LayerSpacing
    };

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
        var defaultLayout = CropFarmLayout.CreateDefault(source.DefaultFieldTiles);
        FieldWidth = defaultLayout.Width;
        FieldLength = defaultLayout.Length;
        IconPath = database.FindItem(source.Outputs[0].ItemId)?.IconPath;
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

    private GameTiming _timing;
    private readonly decimal? _configuredActiveSecondsPerCycle;

    public ForestrySourceRow(ForestrySourceDefinition source, GameTiming timing, GameDatabase database)
    {
        _source = source;
        _timing = timing;
        _configuredActiveSecondsPerCycle = database.FindJob(source.JobTypeId)?.ActiveSecondsPerCycle;
        Id = source.Id;
        DisplayName = source.DisplayName;
        LogIconPath = database.FindItem(source.LogItemId)?.IconPath;
        LeavesIconPath = database.FindItem(source.LeavesItemId)?.IconPath;
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
    private int HarvestCapacityPerForester => _source.GetHarvestCapacityPerForester(_configuredActiveSecondsPerCycle ?? _timing.WorkerActiveSeconds);
    private int HarvestedTrees => Math.Min(TotalTrees, Math.Max(1, ForesterCount) * HarvestCapacityPerForester);
    private static string DisplayNameFromId(string id) => ColonyOptimizer.Core.DisplayName.FromIdentifier(id);

    public void UpdateTiming(GameTiming timing)
    {
        _timing = timing;
        OnPropertyChanged(nameof(OutputPerCycle));
        OnPropertyChanged(nameof(HarvestRate));
        OnPropertyChanged(nameof(Cadence));
    }

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
        OutputIconPath = recipe.Outputs.Select(output => database.FindItem(output.ItemId)?.IconPath).FirstOrDefault(icon => !string.IsNullOrWhiteSpace(icon));
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

    private static string DisplayItem(GameDatabase database, string itemId) => database.FindItem(itemId)?.DisplayName ?? ColonyOptimizer.Core.DisplayName.FromIdentifier(itemId);
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
        Ammo = string.Join(", ", guard.Ammunition.Select(ammo => $"{ammo.Amount:0.##} {database.FindItem(ammo.ItemId)?.DisplayName ?? ColonyOptimizer.Core.DisplayName.FromIdentifier(ammo.ItemId)}"));
        AmmoIconPath = guard.Ammunition.Select(ammo => database.FindItem(ammo.ItemId)?.IconPath).FirstOrDefault(icon => !string.IsNullOrWhiteSpace(icon));
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
        Ammunition = database.FindItem(trap.AmmunitionItemId)?.DisplayName ?? ColonyOptimizer.Core.DisplayName.FromIdentifier(trap.AmmunitionItemId);
        AmmunitionIconPath = database.FindItem(trap.AmmunitionItemId)?.IconPath;
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

public sealed record WorldSaveOption(string Path)
{
    public string DisplayName => $"{System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path))} - {Path}";
    public override string ToString() => DisplayName;
}

public sealed record ColonyGroupImportOption(long? RowId, string? StableIdentity, string DisplayName, bool RequiresReselection = false)
{
    public SaveGameColonyGroupSelection? Selection => RowId is null && StableIdentity is null
        ? null
        : new SaveGameColonyGroupSelection(RowId, StableIdentity);
    public static ColonyGroupImportOption Combined { get; } = new(null, null, "All colony groups (combined — legacy behaviour)");
    public static ColonyGroupImportOption FromGroup(SaveGameColonyGroup group) => new(
        group.RowId,
        group.StableIdentity,
        group.StableIdentity is null ? $"{group.DisplayName} (not persisted: save has no explicit group ID)" : group.DisplayName);
    public static ColonyGroupImportOption Missing(string stableIdentity) => new(null, stableIdentity, "Saved colony group is no longer present — choose another scope", true);
    public static ColonyGroupImportOption Legacy(long rowId) => new(null, null, $"Previously saved group {rowId} must be selected again — choose another scope", true);
    public override string ToString() => DisplayName;
}

public enum FoodCoverageLevel
{
    Sufficient,
    Cautious,
    Insufficient,
    NotRequired
}

public sealed record FoodCoverageSummary(
    long ProductionWorkers,
    long Guards,
    decimal MealsAvailablePerCycle,
    decimal MealsRequiredPerCycle)
{
    public decimal CoveragePercent => MealsRequiredPerCycle <= 0m ? 0m : MealsAvailablePerCycle / MealsRequiredPerCycle * 100m;
    public decimal MealBalancePerCycle => MealsAvailablePerCycle - MealsRequiredPerCycle;
    public FoodCoverageLevel Level => MealsRequiredPerCycle <= 0m
        ? FoodCoverageLevel.NotRequired
        : CoveragePercent > 110m
            ? FoodCoverageLevel.Sufficient
            : CoveragePercent >= 100m
                ? FoodCoverageLevel.Cautious
                : FoodCoverageLevel.Insufficient;
    public string Label => MealsRequiredPerCycle <= 0m ? "Food: no colonists" : $"Food: {CoveragePercent:0.#}%";

    public string Tooltip
    {
        get
        {
            if (MealsRequiredPerCycle <= 0m)
            {
                return "No production workers or guards need meals in this plan. Machine blocks are not colonists.";
            }

            var balance = MealBalancePerCycle >= 0m
                ? $"{MealBalancePerCycle:0.##} meals extra"
                : $"{-MealBalancePerCycle:0.##} meals short";
            return $"Per game day (cycle): {MealsAvailablePerCycle:0.##} meals available; {MealsRequiredPerCycle:0.##} required for {ProductionWorkers:N0} production workers and {Guards:N0} guards. {balance}. Existing or idle colonists are not included.";
        }
    }

    public static FoodCoverageSummary Calculate(GameDatabase database, OptimizationResult result)
    {
        var foodItemIds = database.Items
            .Where(item => item.Category?.Equals("food", StringComparison.OrdinalIgnoreCase) == true)
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var producedMeals = result.TotalOutputs
            .Where(output => foodItemIds.Contains(output.ItemId))
            .Sum(output => output.PerCycle);
        var requiredMeals = result.TotalWorkers + result.TotalGuards;
        return new FoodCoverageSummary(result.TotalWorkers, result.TotalGuards, producedMeals, requiredMeals);
    }
}

public enum NodeLayoutDirection
{
    Right,
    Down
}
