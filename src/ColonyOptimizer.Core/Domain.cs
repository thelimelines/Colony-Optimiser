namespace ColonyOptimizer.Core;

public enum DiagnosticLevel
{
    Information,
    Warning,
    Error
}

public sealed record DataDiagnostic(DiagnosticLevel Level, string Message, string? SourceFile = null);

public sealed class GameDataDiagnostics
{
    public List<DataDiagnostic> Entries { get; } = [];

    public void Add(DiagnosticLevel level, string message, string? sourceFile = null) =>
        Entries.Add(new DataDiagnostic(level, message, sourceFile));
}

public sealed record GameDataSourceInfo(
    string SourceType,
    string SourcePath,
    string? Version = null,
    string? Commit = null,
    DateTimeOffset? DownloadedAt = null);

public sealed class GameDatabase
{
    public GameDataSourceInfo Source { get; set; } = new("Unknown", string.Empty);
    public GameTiming Timing { get; set; } = GameTiming.Default;
    public List<ItemDefinition> Items { get; } = [];
    public List<RecipeDefinition> Recipes { get; } = [];
    public List<MiningSourceDefinition> MiningSources { get; } = [];
    public List<CropFarmSourceDefinition> CropFarmSources { get; } = [];
    public List<ForestrySourceDefinition> ForestrySources { get; } = [];
    public List<JobTypeDefinition> Jobs { get; } = [];
    public List<ToolDefinition> Tools { get; } = [];
    public List<ToolsetDefinition> Toolsets { get; } = [];
    public List<ScienceDefinition> Sciences { get; } = [];
    public List<GuardTypeDefinition> Guards { get; } = [];
    public List<TrapDefinition> Traps { get; } = [];
    public GameDataDiagnostics Diagnostics { get; } = new();

    public ItemDefinition GetOrAddItem(string id)
    {
        var item = Items.FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            return item;
        }

        item = new ItemDefinition { Id = id, DisplayName = DisplayName.FromIdentifier(id), IsResolved = false };
        Items.Add(item);
        return item;
    }
}

public static class DisplayName
{
    public static string FromIdentifier(string identifier)
    {
        var tail = identifier.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? identifier;
        return string.Join(' ', tail.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}

public sealed class ItemDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? IconPath { get; set; }
    public bool IsResolved { get; set; } = true;
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ItemAmount> OnRemoveOutputs { get; } = [];
}

public readonly record struct ItemAmount(string ItemId, decimal Amount, decimal Chance = 1m, bool IsOptional = false)
{
    public decimal ExpectedAmount => Amount * Chance;
}

public sealed class RecipeDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string JobTypeId { get; set; } = string.Empty;
    public decimal CooldownSeconds { get; set; }
    public List<ItemAmount> Inputs { get; } = [];
    public List<ItemAmount> Outputs { get; } = [];
    public string? RequiredScience { get; set; }
    public string? RequiredToolset { get; set; }
    public int SortWeight { get; set; }
    public string SourceFile { get; set; } = string.Empty;
    public int SourceIndex { get; set; }
    public decimal? WorkloadSeconds { get; set; }
    public int DedicatedWorkersPerCraft { get; set; }
    public string UnitLabel { get; set; } = "Craft";
}

public sealed class MiningSourceDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string OutputItemId { get; set; } = string.Empty;
    public decimal MiningTimeSeconds { get; set; }
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class CropFarmSourceDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string JobTypeId { get; set; } = string.Empty;
    public string GrowthType { get; set; } = string.Empty;
    public int StageCount { get; set; }
    public decimal GrowthCyclesPerHarvest { get; set; }
    public decimal HarvestActionSecondsPerTile { get; set; }
    public int FertilityRequirement { get; set; }
    public int DefaultFieldTiles { get; set; } = 100;
    public string? RequiredScience { get; set; }
    public string SourceFile { get; set; } = string.Empty;
    public List<ItemAmount> Outputs { get; } = [];
}

public sealed class ForestrySourceDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string JobTypeId { get; set; } = "pipliz.forester";
    public string LogItemId { get; set; } = string.Empty;
    public string LeavesItemId { get; set; } = string.Empty;
    public int TreesPerForesterPerCycle { get; set; } = 9;
    public int LogsPerTree { get; set; } = 4;
    public int LeavesPerTree { get; set; } = 9;
    public decimal WorkSecondsPerForesterCycle { get; set; } = 390m;
    public int DefaultForesterCount { get; set; } = 1;
    public int DefaultPlotWidth { get; set; } = 3;
    public int DefaultPlotLength { get; set; } = 33;
    public string? RequiredScience { get; set; }
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class JobTypeDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? JobBlockId { get; set; }
    public string? ToolsetId { get; set; }
    public decimal? ActiveSecondsPerCycle { get; set; }
    public bool IsAutomatedQueue { get; set; }
}

public sealed class ToolDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public decimal CraftingSpeed { get; set; } = 1m;
    // The game configuration expresses durability in seconds, then multiplies it to milliseconds at runtime.
    public decimal Durability { get; set; }
    public bool RequiresStockpileItem { get; set; } = true;
    public string? RequiredScience { get; set; }
}

public sealed class ToolsetDefinition
{
    public string Id { get; set; } = string.Empty;
    public List<string> UsableTools { get; } = [];
    public decimal UseMultiplier { get; set; } = 1m;
}

public sealed class ScienceDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Dependencies { get; } = [];
    public List<string> UnlockedNpcTypeIds { get; } = [];
}

public enum GuardShift
{
    Day,
    Night
}

public sealed class GuardTypeDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string NpcTypeId { get; set; } = string.Empty;
    public GuardShift Shift { get; set; }
    public decimal CooldownShotSeconds { get; set; }
    public List<ItemAmount> Ammunition { get; } = [];
    public decimal Damage { get; set; }
    public decimal Range { get; set; }
    public string SourceFile { get; set; } = string.Empty;
}

public sealed class TrapDefinition
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? IconPath { get; set; }
    public string AmmunitionItemId { get; set; } = string.Empty;
    public int AmmunitionCapacity { get; set; }
    public decimal ReloadSecondsPerAmmunition { get; set; }
    public string SourceFile { get; set; } = string.Empty;
}

public sealed record GameTiming(
    decimal GameTimeScale,
    decimal DayTimeStart,
    decimal DayTimeEnd,
    decimal GuardShiftDayStart,
    decimal GuardShiftDayEnd,
    decimal GuardShiftNightStart,
    decimal GuardShiftNightEnd,
    decimal SleepTimeStart,
    decimal SleepTimeEnd)
{
    public static GameTiming Default { get; } = new(120m, 4.5m, 19.5m, 4m, 19m, 17m, 8m, 19.3m, 4.5m);

    public decimal CycleSeconds => GameTimeScale <= 0m ? 0m : 24m * 60m * 60m / GameTimeScale;
    public decimal DaylightSeconds => IntervalSeconds(DayTimeStart, DayTimeEnd);
    public decimal DayGuardSeconds => IntervalSeconds(GuardShiftDayStart, GuardShiftDayEnd);
    public decimal NightGuardSeconds => IntervalSeconds(GuardShiftNightStart, GuardShiftNightEnd);
    public decimal WorkerActiveSeconds => Math.Max(0m, CycleSeconds - IntervalSeconds(SleepTimeStart, SleepTimeEnd));

    public decimal IntervalSeconds(decimal startHour, decimal endHour)
    {
        var gameHours = endHour >= startHour ? endHour - startHour : 24m - startHour + endHour;
        return gameHours * 60m * 60m / GameTimeScale;
    }

    public decimal GetGuardShiftSeconds(GuardShift shift) => shift == GuardShift.Day ? DayGuardSeconds : NightGuardSeconds;

    public decimal GetHostileGuardOverlapSeconds(GuardShift shift)
    {
        var (guardStart, guardEnd) = shift == GuardShift.Day
            ? (GuardShiftDayStart, GuardShiftDayEnd)
            : (GuardShiftNightStart, GuardShiftNightEnd);
        return OverlapHours(guardStart, guardEnd, DayTimeEnd, DayTimeStart) * 60m * 60m / GameTimeScale;
    }

    private static decimal OverlapHours(decimal firstStart, decimal firstEnd, decimal secondStart, decimal secondEnd)
    {
        static IEnumerable<(decimal Start, decimal End)> Segments(decimal start, decimal end)
        {
            if (end >= start)
            {
                yield return (start, end);
                yield break;
            }

            yield return (start, 24m);
            yield return (0m, end);
        }

        return Segments(firstStart, firstEnd).SelectMany(first => Segments(secondStart, secondEnd), (first, second) =>
            Math.Max(0m, Math.Min(first.End, second.End) - Math.Max(first.Start, second.Start))).Sum();
    }
}
