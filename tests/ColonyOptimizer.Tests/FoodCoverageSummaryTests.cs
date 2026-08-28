using ColonyOptimizer.App;
using ColonyOptimizer.Core;
using ColonyOptimizer.Optimization;

namespace ColonyOptimizer.Tests;

public sealed class FoodCoverageSummaryTests
{
    [Fact]
    public void calculates_all_food_output_for_workers_and_guards_without_subtracting_food_targets()
    {
        var database = new GameDatabase();
        database.Items.Add(new ItemDefinition { Id = "meal", DisplayName = "Meal", Category = "food" });
        database.Items.Add(new ItemDefinition { Id = "ore", DisplayName = "Ore", Category = "raw" });
        var result = new OptimizationResult { TotalGuards = 2 };
        result.JobRequirements.Add(new JobRequirement { Workers = 8 });
        result.TotalOutputs.Add(new ProductionOutput { ItemId = "meal", PerCycle = 14m });
        result.TotalOutputs.Add(new ProductionOutput { ItemId = "ore", PerCycle = 99m });
        result.Demand["meal"] = new DemandBreakdown { ItemId = "meal", PlayerPerCycle = 2m };

        var coverage = FoodCoverageSummary.Calculate(database, result);

        Assert.Equal(8, coverage.ProductionWorkers);
        Assert.Equal(2, coverage.Guards);
        Assert.Equal(14m, coverage.MealsAvailablePerCycle);
        Assert.Equal(10m, coverage.MealsRequiredPerCycle);
        Assert.Equal(140m, coverage.CoveragePercent);
        Assert.Equal(FoodCoverageLevel.Sufficient, coverage.Level);
        Assert.Contains("4 meals extra", coverage.Tooltip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(11, 10, FoodCoverageLevel.Cautious)]
    [InlineData(9.99, 10, FoodCoverageLevel.Insufficient)]
    public void classifies_the_food_coverage_thresholds(decimal availableMeals, decimal requiredMeals, FoodCoverageLevel expectedLevel)
    {
        var coverage = new FoodCoverageSummary(8, 2, availableMeals, requiredMeals);

        Assert.Equal(expectedLevel, coverage.Level);
    }

    [Fact]
    public void marks_a_plan_without_workers_or_guards_as_not_requiring_meals()
    {
        var coverage = new FoodCoverageSummary(0, 0, 0m, 0m);

        Assert.Equal(FoodCoverageLevel.NotRequired, coverage.Level);
        Assert.Equal("Food: no colonists", coverage.Label);
        Assert.Contains("Machine blocks are not colonists", coverage.Tooltip, StringComparison.Ordinal);
    }
}
