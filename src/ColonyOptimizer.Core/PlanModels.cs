namespace ColonyOptimizer.Core;

public enum DemandUnit
{
    PerSecond,
    PerMinute,
    PerCycle
}

public enum RecipePolicy
{
    Allowed,
    Preferred,
    Forbidden,
    Forced
}

public enum OptimizationObjective
{
    FewestWorkers,
    LowestRawResourceConsumption,
    PreferredRecipesFirst
}

public enum StochasticOutputPolicy
{
    ExpectedValue,
    IgnoreOptionalOutputs,
    Conservative
}

public enum GuardAmmoMode
{
    EntireShiftWorstCase,
    HostilePeriodOnly,
    CustomUtilisation,
    CustomRoundsPerCycle
}

public sealed class DemandTarget
{
    public string ItemId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DemandUnit Unit { get; set; } = DemandUnit.PerMinute;

    public decimal ToPerCycle(GameTiming timing) => Unit switch
    {
        DemandUnit.PerSecond => Amount * timing.CycleSeconds,
        DemandUnit.PerCycle => Amount,
        _ => Amount * timing.CycleSeconds / 60m
    };
}

public sealed class GuardAssignment
{
    public string GuardTypeId { get; set; } = string.Empty;
    public int Count { get; set; }
    public GuardAmmoMode AmmoMode { get; set; } = GuardAmmoMode.EntireShiftWorstCase;
    public decimal UtilisationPercent { get; set; } = 100m;
    public int? CustomRoundsPerCycle { get; set; }
}

public sealed class TrapAssignment
{
    public string TrapTypeId { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class ProductionPlan
{
    public string Name { get; set; } = "Untitled plan";
    public List<DemandTarget> Targets { get; set; } = [];
    public HashSet<string> UnlockedSciences { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AvailableTools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RecipePolicy> RecipePolicies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CropFarmLayout> CropFarmLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ForestryLayout> ForestryLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExternalItems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<GuardAssignment> Guards { get; set; } = [];
    public List<TrapAssignment> Traps { get; set; } = [];
}

public sealed class OptimizationSettings
{
    public decimal EfficiencyPercent { get; set; } = 100m;
    public decimal HeadroomPercent { get; set; }
    public OptimizationObjective Objective { get; set; } = OptimizationObjective.FewestWorkers;
    public StochasticOutputPolicy StochasticOutputPolicy { get; set; } = StochasticOutputPolicy.ExpectedValue;
    public int MaxCraftsPerRecipe { get; set; } = 100_000;
    public int MaxWorkersPerJob { get; set; } = 10_000;
    public TimingOverride TimingOverride { get; set; } = new();
}

public sealed class ForestryLayout
{
    public int ForesterCount { get; set; }
    public int PlotWidth { get; set; }
    public int PlotLength { get; set; }

    public static int GetTreeSlotCount(int width, int length) =>
        Math.Max(0, width / 3) * Math.Max(0, length / 3);
}

public sealed class CropFarmLayout
{
    public int Width { get; set; }
    public int Length { get; set; }
}

public sealed class TimingOverride
{
    public bool IsEnabled { get; set; }
    public decimal GameTimeScale { get; set; } = GameTiming.Default.GameTimeScale;
    public decimal DayTimeStart { get; set; } = GameTiming.Default.DayTimeStart;
    public decimal DayTimeEnd { get; set; } = GameTiming.Default.DayTimeEnd;
    public decimal GuardShiftDayStart { get; set; } = GameTiming.Default.GuardShiftDayStart;
    public decimal GuardShiftDayEnd { get; set; } = GameTiming.Default.GuardShiftDayEnd;
    public decimal GuardShiftNightStart { get; set; } = GameTiming.Default.GuardShiftNightStart;
    public decimal GuardShiftNightEnd { get; set; } = GameTiming.Default.GuardShiftNightEnd;
    public decimal SleepTimeStart { get; set; } = GameTiming.Default.SleepTimeStart;
    public decimal SleepTimeEnd { get; set; } = GameTiming.Default.SleepTimeEnd;

    public GameTiming Apply(GameTiming defaults) => !IsEnabled
        ? defaults
        : new GameTiming(
            Math.Max(1m, GameTimeScale),
            NormaliseHour(DayTimeStart),
            NormaliseHour(DayTimeEnd),
            NormaliseHour(GuardShiftDayStart),
            NormaliseHour(GuardShiftDayEnd),
            NormaliseHour(GuardShiftNightStart),
            NormaliseHour(GuardShiftNightEnd),
            NormaliseHour(SleepTimeStart),
            NormaliseHour(SleepTimeEnd));

    private static decimal NormaliseHour(decimal hour)
    {
        var normalised = hour % 24m;
        return normalised < 0m ? normalised + 24m : normalised;
    }
}

public sealed class SavedPlanDocument
{
    public const int CurrentFormatVersion = 7;

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public ProductionPlan Plan { get; set; } = new();
    public OptimizationSettings Settings { get; set; } = new();
    public GameDataSourceInfo? DataSource { get; set; }
}
