using ColonyOptimizer.Core;

namespace ColonyOptimizer.Tests;

public sealed class GameDatabaseIndexTests
{
    [Fact]
    public void ID_lookups_preserve_first_definition_and_refresh_after_a_list_grows()
    {
        var database = new GameDatabase();
        var first = new ItemDefinition { Id = "wheat", DisplayName = "First wheat" };
        database.Items.Add(first);
        database.Items.Add(new ItemDefinition { Id = "WHEAT", DisplayName = "Second wheat" });

        Assert.Same(first, database.FindItem("wHeAt"));

        var bread = new ItemDefinition { Id = "bread", DisplayName = "Bread" };
        database.Items.Add(bread);

        Assert.Same(bread, database.FindItem("BREAD"));
    }

    [Fact]
    public void ID_lookups_cover_the_optimiser_definition_types()
    {
        var database = new GameDatabase();
        var recipe = new RecipeDefinition { Id = "recipe" };
        var job = new JobTypeDefinition { Id = "job" };
        var tool = new ToolDefinition { Id = "tool" };
        var toolset = new ToolsetDefinition { Id = "toolset" };
        var science = new ScienceDefinition { Id = "science" };
        var guard = new GuardTypeDefinition { Id = "guard" };
        var trap = new TrapDefinition { Id = "trap" };
        database.Recipes.Add(recipe);
        database.Jobs.Add(job);
        database.Tools.Add(tool);
        database.Toolsets.Add(toolset);
        database.Sciences.Add(science);
        database.Guards.Add(guard);
        database.Traps.Add(trap);

        Assert.Same(recipe, database.FindRecipe("RECIPE"));
        Assert.Same(job, database.FindJob("JOB"));
        Assert.Same(tool, database.FindTool("TOOL"));
        Assert.Same(toolset, database.FindToolset("TOOLSET"));
        Assert.Same(science, database.FindScience("SCIENCE"));
        Assert.Same(guard, database.FindGuard("GUARD"));
        Assert.Same(trap, database.FindTrap("TRAP"));
    }
}
