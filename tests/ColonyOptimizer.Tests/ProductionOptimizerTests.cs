using ColonyOptimizer.Core;
using ColonyOptimizer.GameData;
using ColonyOptimizer.Optimization;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace ColonyOptimizer.Tests;

public sealed class ProductionOptimizerTests
{
    [Fact]
    public void loads_the_checked_out_vanilla_manifest_and_core_production_data()
    {
        var database = new GameDataLoader().Load(FindVanillaGameDataPath());

        Assert.True(database.Recipes.Count > 150);
        Assert.True(database.Sciences.Count > 50);
        Assert.True(database.Guards.Count >= 10);
        Assert.Equal(6, database.Traps.Count);
        Assert.True(database.Tools.Count >= 5);
        Assert.True(database.MiningSources.Count >= 8);
        Assert.True(database.CropFarmSources.Count >= 9);
        Assert.Equal(720m, database.Timing.CycleSeconds);
        Assert.Equal(444m, database.Timing.WorkerActiveSeconds);
        Assert.Contains(database.Recipes, recipe => recipe.Id == "pipliz.fletcher.crossbowbolt");
        Assert.Contains(database.Recipes, recipe => recipe.Id == "pipliz.minerjob.infiniteiron" && recipe.CooldownSeconds == 60m);
        Assert.Contains(database.Guards, guard => guard.Ammunition.Any(ammo => ammo.ItemId == "crossbowbolt"));
        Assert.Contains(database.Traps, trap => trap.Id == "projectiletrap" && trap.AmmunitionItemId == "projectiletrapammo" && trap.AmmunitionCapacity == 10);
        Assert.All(database.Traps, trap => Assert.NotEmpty(trap.AmmunitionItemId));
        Assert.Contains(database.Items, item => item.Id == "bed3");
        Assert.Contains(database.Items, item => item.Id == "architrave");
        Assert.DoesNotContain(database.Recipes.SelectMany(recipe => recipe.Inputs.Concat(recipe.Outputs)), amount => amount.ItemId is "logtemperate" or "logtaiga" or "leavestemperate" or "leavestaiga");
        var wheatFarm = Assert.Single(database.CropFarmSources, source => source.Id == "pipliz.wheatfarm");
        Assert.Equal(3, wheatFarm.StageCount);
        Assert.Equal(2m, wheatFarm.GrowthCyclesPerHarvest);
        Assert.Equal("pipliz.farming", wheatFarm.RequiredScience);
        Assert.Contains(wheatFarm.Outputs, output => output.ItemId == "wheat" && output.Amount == 1m);
        Assert.Contains(wheatFarm.Outputs, output => output.ItemId == "straw" && output.Chance == 0.25m);
        AssertCropFarmCoverage(database);
    }

    [Fact]
    public void Shares_one_worker_between_two_recipe_workloads()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Recipes.Add(Recipe("make-a", "worker", 40m, "a"));
        database.Recipes.Add(Recipe("make-b", "worker", 50m, "b"));
        var plan = Plan(("a", 1m), ("b", 1m));

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible);
        Assert.Equal(1, result.TotalWorkers);
        Assert.Equal(90m, result.JobRequirements.Single().WorkloadSeconds);
        Assert.Contains(result.ProductionFlows, flow => flow.SourceId == "recipe:make-a" && flow.TargetId == "item:a" && flow.Amount == 1m);
    }

    [Fact]
    public void Chooses_the_higher_output_recipe_when_minimising_workers()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Recipes.Add(Recipe("slow", "worker", 100m, "widget", 1m));
        database.Recipes.Add(Recipe("bulk", "worker", 100m, "widget", 2m));
        var plan = Plan(("widget", 2m));

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible);
        Assert.Equal(1, result.TotalWorkers);
        Assert.Equal(1, result.RecipeAllocations.Single(allocation => allocation.RecipeId == "bulk").CraftsPerCycle);
    }

    [Fact]
    public void prefers_a_worker_recipe_when_an_automated_queue_can_produce_the_same_output()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Jobs.Add(new JobTypeDefinition { Id = "machine", DisplayName = "Machine", IsAutomatedQueue = true });
        database.Recipes.Add(Recipe("worker-widget", "worker", 100m, "widget"));
        database.Recipes.Add(Recipe("machine-widget", "machine", 1m, "widget"));

        var result = new ProductionOptimizer().Optimize(database, Plan(("widget", 1m)), new OptimizationSettings());

        Assert.True(result.IsFeasible);
        Assert.Contains(result.RecipeAllocations, allocation => allocation.RecipeId == "worker-widget");
        Assert.DoesNotContain(result.RecipeAllocations, allocation => allocation.RecipeId == "machine-widget");
    }

    [Fact]
    public void uses_an_automated_queue_when_the_worker_route_cannot_supply_its_input()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Jobs.Add(new JobTypeDefinition { Id = "machine", DisplayName = "Machine", IsAutomatedQueue = true });
        var workerRecipe = Recipe("worker-widget", "worker", 100m, "widget");
        workerRecipe.Inputs.Add(new ItemAmount("manual-input", 1m));
        database.Recipes.Add(workerRecipe);
        database.Recipes.Add(Recipe("machine-widget", "machine", 1m, "widget"));
        database.Recipes.Add(new RecipeDefinition
        {
            Id = "locked-manual-input",
            DisplayName = "Locked Manual Input",
            JobTypeId = "worker",
            CooldownSeconds = 1m,
            RequiredScience = "unavailable-science",
            Outputs = { new ItemAmount("manual-input", 1m) }
        });

        var result = new ProductionOptimizer().Optimize(database, Plan(("widget", 1m)), new OptimizationSettings());

        Assert.True(result.IsFeasible, string.Join(Environment.NewLine, result.Messages.Select(message => message.Text)));
        Assert.Contains(result.RecipeAllocations, allocation => allocation.RecipeId == "machine-widget");
        Assert.DoesNotContain(result.RecipeAllocations, allocation => allocation.RecipeId == "worker-widget");
    }

    [Theory]
    [InlineData(72, 1)]
    [InlineData(73, 2)]
    [InlineData(720, 10)]
    public void limits_automated_machine_throughput_by_full_cycle_capacity(decimal demand, long expectedMachines)
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Jobs.Add(new JobTypeDefinition { Id = "machine", DisplayName = "Machine", IsAutomatedQueue = true });
        database.Recipes.Add(Recipe("machine-widget", "machine", 10m, "widget"));

        var result = new ProductionOptimizer().Optimize(database, Plan(("widget", demand)), new OptimizationSettings());

        Assert.True(result.IsFeasible);
        Assert.True(result.IsOptimal);
        Assert.Equal(0, result.TotalWorkers);
        Assert.Equal(expectedMachines, result.TotalMachineBlocks);
        var machine = Assert.Single(result.JobRequirements);
        Assert.True(machine.IsAutomatedQueue);
        Assert.Equal(0, machine.Workers);
        Assert.Equal(expectedMachines, machine.MachineBlocks);
    }

    [Fact]
    public void shares_automated_machine_capacity_between_queued_recipes()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Jobs.Add(new JobTypeDefinition { Id = "machine", DisplayName = "Machine", IsAutomatedQueue = true });
        database.Recipes.Add(Recipe("machine-a", "machine", 10m, "a"));
        database.Recipes.Add(Recipe("machine-b", "machine", 20m, "b"));

        var result = new ProductionOptimizer().Optimize(database, Plan(("a", 36m), ("b", 18m)), new OptimizationSettings());

        Assert.True(result.IsFeasible);
        Assert.Equal(1, Assert.Single(result.JobRequirements).MachineBlocks);
    }

    [Theory]
    [InlineData(2, 100, 0)]
    [InlineData(5, 5, 1)]
    [InlineData(4, 8, 2)]
    public void forestry_uses_physical_three_by_three_tree_slots(int width, int length, int expectedTrees)
    {
        var database = CreateDatabase(activeSeconds: 720m);
        database.ForestrySources.Add(new ForestrySourceDefinition
        {
            Id = "forest",
            DisplayName = "Forest",
            JobTypeId = "worker",
            LogItemId = "logs",
            LeavesItemId = "leaves",
            TreesPerForesterPerCycle = 100,
            LogsPerTree = 1,
            LeavesPerTree = 1,
            WorkSecondsPerForesterCycle = 1m
        });
        var plan = Plan(("logs", Math.Max(1, expectedTrees)));
        plan.ForestryLayouts["forest"] = new ForestryLayout { ForesterCount = 1, PlotWidth = width, PlotLength = length };

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.Equal(expectedTrees > 0, result.IsFeasible);
        if (expectedTrees > 0)
        {
            Assert.Equal(expectedTrees, result.TotalOutputs.Single(output => output.ItemId == "logs").PerCycle);
        }
    }

    [Fact]
    public void persists_current_crop_and_forestry_dimensions_through_plan_json_round_trip()
    {
        var document = new SavedPlanDocument
        {
            Plan = new ProductionPlan
            {
                CropFarmLayouts = new Dictionary<string, CropFarmLayout>(StringComparer.OrdinalIgnoreCase)
                {
                    ["crop"] = new CropFarmLayout { Width = 7, Length = 7 }
                },
                ForestryLayouts = new Dictionary<string, ForestryLayout>(StringComparer.OrdinalIgnoreCase)
                {
                    ["forest"] = new ForestryLayout { ForesterCount = 2, PlotWidth = 6, PlotLength = 15 }
                }
            }
        };

        var restored = JsonSerializer.Deserialize<SavedPlanDocument>(JsonSerializer.Serialize(document));

        Assert.NotNull(restored);
        Assert.Equal(SavedPlanDocument.CurrentFormatVersion, restored.FormatVersion);
        var layout = Assert.Single(restored.Plan.CropFarmLayouts).Value;
        Assert.Equal(7, layout.Width);
        Assert.Equal(7, layout.Length);
        var forestry = Assert.Single(restored.Plan.ForestryLayouts).Value;
        Assert.Equal(2, forestry.ForesterCount);
        Assert.Equal(6, forestry.PlotWidth);
        Assert.Equal(15, forestry.PlotLength);
    }

    [Fact]
    public void loads_obsolete_plan_fields_tolerantly_and_resaves_only_the_current_schema()
    {
        var database = CreateDatabase(activeSeconds: 720m);
        var crop = new CropFarmSourceDefinition
        {
            Id = "crop",
            DisplayName = "Crop",
            JobTypeId = "worker",
            DefaultFieldTiles = 49,
            GrowthCyclesPerHarvest = 1m
        };
        crop.Outputs.Add(new ItemAmount("produce", 1m));
        database.CropFarmSources.Add(crop);
        const string oldPlan = """
            {
              "FormatVersion": 1,
              "Plan": {
                "Name": "Old plan",
                "CropFarmTileCounts": { "crop": 49 },
                "ForestryLayouts": { "forest": { "TreesPerForester": 9 } },
                "FuturePlanField": true
              },
              "FutureDocumentField": "ignored"
            }
            """;
        var document = JsonSerializer.Deserialize<SavedPlanDocument>(oldPlan);

        Assert.NotNull(document);
        Assert.Equal(1, document.FormatVersion);
        Assert.Equal("Old plan", document.Plan.Name);
        Assert.Empty(document.Plan.CropFarmLayouts);
        var forestry = Assert.Single(document.Plan.ForestryLayouts).Value;
        Assert.Equal(3, forestry.PlotWidth);
        Assert.Equal(33, forestry.PlotLength);

        document.Plan.Targets.Add(new DemandTarget { ItemId = "produce", Amount = 49m, Unit = DemandUnit.PerCycle });
        var result = new ProductionOptimizer().Optimize(database, document.Plan, new OptimizationSettings());

        Assert.True(result.IsFeasible);
        Assert.Equal(49m, result.TotalOutputs.Single(output => output.ItemId == "produce").PerCycle);

        using var resaved = JsonDocument.Parse(JsonSerializer.Serialize(new SavedPlanDocument
        {
            Plan = document.Plan,
            Settings = document.Settings,
            DataSource = document.DataSource
        }));
        Assert.Equal(SavedPlanDocument.CurrentFormatVersion, resaved.RootElement.GetProperty("FormatVersion").GetInt32());
        var savedPlan = resaved.RootElement.GetProperty("Plan");
        Assert.False(savedPlan.TryGetProperty("CropFarmTileCounts", out _));
        Assert.False(savedPlan.GetProperty("ForestryLayouts").GetProperty("forest").TryGetProperty("TreesPerForester", out _));
    }

    [Fact]
    public void satisfies_a_multilevel_material_chain_globally()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Recipes.Add(Recipe("make-component", "worker", 20m, "component"));
        var assembly = Recipe("assemble-product", "worker", 20m, "product");
        assembly.Inputs.Add(new ItemAmount("component", 1m));
        database.Recipes.Add(assembly);

        var result = new ProductionOptimizer().Optimize(database, Plan(("product", 1m)), new OptimizationSettings());

        Assert.True(result.IsFeasible);
        Assert.Equal(1, result.TotalWorkers);
        Assert.Equal(2, result.RecipeAllocations.Count);
    }

    [Fact]
    public void applies_headroom_as_a_real_capacity_constraint()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Recipes.Add(Recipe("widgets", "worker", 50m, "widget"));

        var result = new ProductionOptimizer().Optimize(database, Plan(("widget", 2m)), new OptimizationSettings { HeadroomPercent = 10m });

        Assert.True(result.IsFeasible);
        Assert.Equal(2, result.TotalWorkers);
    }

    [Fact]
    public void excludes_forbidden_recipes()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Recipes.Add(Recipe("allowed", "worker", 100m, "widget"));
        database.Recipes.Add(Recipe("forbidden", "worker", 10m, "widget"));
        var plan = Plan(("widget", 1m));
        plan.RecipePolicies["forbidden"] = RecipePolicy.Forbidden;

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible);
        Assert.DoesNotContain(result.RecipeAllocations, allocation => allocation.RecipeId == "forbidden");
    }

    [Fact]
    public void applies_available_tool_speed_to_capacity()
    {
        var database = CreateDatabase(activeSeconds: 100m, toolset: "basic");
        database.Toolsets.Add(new ToolsetDefinition { Id = "basic" });
        database.Toolsets[0].UsableTools.Add("fast-tool");
        database.Tools.Add(new ToolDefinition { Id = "fast-tool", DisplayName = "Fast tool", CraftingSpeed = 2m });
        database.Recipes.Add(Recipe("widgets", "worker", 100m, "widget"));
        var plan = Plan(("widget", 2m));
        plan.AvailableTools.Add("fast-tool");

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible);
        Assert.Equal(1, result.TotalWorkers);
        Assert.Equal("fast-tool", result.JobRequirements.Single().SelectedToolId);
    }

    [Fact]
    public void reports_missing_science_as_an_explanation()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Recipes.Add(new RecipeDefinition
        {
            Id = "widgets",
            DisplayName = "Widgets",
            JobTypeId = "worker",
            CooldownSeconds = 10m,
            RequiredScience = "advanced"
        });
        database.Recipes[0].Outputs.Add(new ItemAmount("widget", 1m));
        var plan = Plan(("widget", 1m));
        plan.UnlockedSciences.Add("different-science");

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.False(result.IsFeasible);
        Assert.Contains(result.Messages, message => message.Text.Contains("advanced", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void calculates_expected_value_outputs()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        var recipe = Recipe("reclaim", "worker", 10m, "widget", 1m);
        recipe.Outputs[0] = new ItemAmount("widget", 1m, 0.5m, true);
        database.Recipes.Add(recipe);
        var plan = Plan(("widget", 1m));

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings
        {
            StochasticOutputPolicy = StochasticOutputPolicy.ExpectedValue
        });

        Assert.True(result.IsFeasible);
        Assert.Equal(2, result.RecipeAllocations.Single().CraftsPerCycle);
    }

    [Fact]
    public void conservative_mode_does_not_treat_chance_returns_as_guaranteed()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        var recipe = Recipe("reclaim", "worker", 10m, "widget", 1m);
        recipe.Outputs[0] = new ItemAmount("widget", 1m, 0.95m, true);
        database.Recipes.Add(recipe);

        var result = new ProductionOptimizer().Optimize(database, Plan(("widget", 1m)), new OptimizationSettings { StochasticOutputPolicy = StochasticOutputPolicy.Conservative });

        Assert.False(result.IsFeasible);
    }

    [Fact]
    public void reports_externally_supplied_materials_without_hiding_them()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        var recipe = Recipe("widget", "worker", 10m, "widget");
        recipe.Inputs.Add(new ItemAmount("ore", 2m));
        database.Recipes.Add(recipe);
        var plan = Plan(("widget", 1m));
        plan.ExternalItems.Add("ore");

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible);
        Assert.Equal(2m, result.ExternalRequirements.Single(requirement => requirement.ItemId == "ore").PerCycle);
    }

    [Fact]
    public void automatically_reports_unmodelled_source_inputs_as_external_requirements()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        var recipe = Recipe("widget", "worker", 10m, "widget");
        recipe.Inputs.Add(new ItemAmount("ore", 2m));
        database.Recipes.Add(recipe);

        var result = new ProductionOptimizer().Optimize(database, Plan(("widget", 1m)), new OptimizationSettings());

        var requirement = Assert.Single(result.ExternalRequirements);
        Assert.True(result.IsFeasible);
        Assert.Equal("ore", requirement.ItemId);
        Assert.Equal(2m, requirement.PerCycle);
        Assert.True(requirement.IsAutomatic);
    }

    [Fact]
    public void tracks_selected_tool_wear_and_plans_replacements()
    {
        var database = CreateDatabase(activeSeconds: 100m, toolset: "basic");
        database.Jobs.Add(new JobTypeDefinition { Id = "toolmaker", DisplayName = "Toolmaker", ActiveSecondsPerCycle = 100m });
        database.Toolsets.Add(new ToolsetDefinition { Id = "basic" });
        database.Toolsets[0].UsableTools.Add("fast-tool");
        database.Tools.Add(new ToolDefinition { Id = "fast-tool", DisplayName = "Fast tool", CraftingSpeed = 2m, Durability = 50m });
        database.Recipes.Add(Recipe("make-tool", "toolmaker", 10m, "fast-tool"));
        database.Recipes.Add(Recipe("widgets", "worker", 100m, "widget"));
        var plan = Plan(("widget", 1m));
        plan.AvailableTools.Add("fast-tool");

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible);
        var requirement = Assert.Single(result.ToolRequirements);
        Assert.Equal(1, requirement.Quantity);
        Assert.Equal("fast-tool", requirement.ToolId);
        Assert.Equal(1m, requirement.ReplacementPerCycle);
        Assert.Equal(1, result.RecipeAllocations.Single(allocation => allocation.RecipeId == "make-tool").CraftsPerCycle);
    }

    [Fact]
    public void plans_replacement_batches_for_low_tool_wear_rates()
    {
        var database = CreateDatabase(activeSeconds: 100m, toolset: "basic");
        database.Jobs.Add(new JobTypeDefinition { Id = "toolmaker", DisplayName = "Toolmaker", ActiveSecondsPerCycle = 100m });
        database.Toolsets.Add(new ToolsetDefinition { Id = "basic" });
        database.Toolsets[0].UsableTools.Add("durable-tool");
        database.Tools.Add(new ToolDefinition { Id = "durable-tool", DisplayName = "Durable tool", CraftingSpeed = 2m, Durability = 10_000m });
        database.Recipes.Add(Recipe("make-tool", "toolmaker", 1m, "durable-tool"));
        database.Recipes.Add(Recipe("widgets", "worker", 1m, "widget"));
        var plan = Plan(("widget", 1m));
        plan.AvailableTools.Add("durable-tool");

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible);
        Assert.Equal(0.00005m, Assert.Single(result.ToolRequirements).ReplacementPerCycle);
        Assert.Equal(1, result.RecipeAllocations.Single(allocation => allocation.RecipeId == "make-tool").CraftsPerCycle);
    }

    [Fact]
    public void adds_guard_ammunition_to_global_demand()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Guards.Add(new GuardTypeDefinition
        {
            Id = "night-guard",
            DisplayName = "Night guard",
            NpcTypeId = "night-guard",
            Shift = GuardShift.Night,
            CooldownShotSeconds = 10m
        });
        database.Guards[0].Ammunition.Add(new ItemAmount("bolt", 1m));
        var plan = new ProductionPlan();
        plan.Guards.Add(new GuardAssignment { GuardTypeId = "night-guard", Count = 2 });
        plan.ExternalItems.Add("bolt");

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.Demand["bolt"].GuardPerCycle > 0m);
        Assert.Equal(result.Demand["bolt"].GuardPerCycle, result.ExternalRequirements.Single().PerCycle);
    }

    [Fact]
    public void adds_one_full_trap_reload_per_cycle_to_global_demand()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Traps.Add(new TrapDefinition { Id = "trap", DisplayName = "Trap", AmmunitionItemId = "trap-ammo", AmmunitionCapacity = 3, ReloadSecondsPerAmmunition = 10m });
        var plan = new ProductionPlan { ExternalItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "trap-ammo" } };
        plan.Traps.Add(new TrapAssignment { TrapTypeId = "trap", Count = 4 });

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible);
        Assert.Equal(12m, result.Demand["trap-ammo"].TrapPerCycle);
        Assert.Equal(12m, result.ExternalRequirements.Single().PerCycle);
    }

    [Fact]
    public void uses_independent_day_and_night_guard_shift_durations()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Timing = new GameTiming(120m, 4m, 20m, 4m, 18m, 20m, 4m, 20m, 4m);
        database.Guards.Add(new GuardTypeDefinition { Id = "day", DisplayName = "Day", NpcTypeId = "day", Shift = GuardShift.Day, CooldownShotSeconds = 10m });
        database.Guards.Add(new GuardTypeDefinition { Id = "night", DisplayName = "Night", NpcTypeId = "night", Shift = GuardShift.Night, CooldownShotSeconds = 10m });
        database.Guards[0].Ammunition.Add(new ItemAmount("bolt", 1m));
        database.Guards[1].Ammunition.Add(new ItemAmount("bolt", 1m));
        var plan = new ProductionPlan { ExternalItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bolt" } };
        plan.Guards.Add(new GuardAssignment { GuardTypeId = "day", Count = 1 });
        plan.Guards.Add(new GuardAssignment { GuardTypeId = "night", Count = 1 });

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.Equal(66m, result.Demand["bolt"].GuardPerCycle);
    }

    [Fact]
    public void applies_utilisation_only_when_the_guard_model_requests_it()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        database.Timing = new GameTiming(120m, 4m, 20m, 4m, 18m, 20m, 4m, 20m, 4m);
        database.Guards.Add(new GuardTypeDefinition { Id = "day", DisplayName = "Day", NpcTypeId = "day", Shift = GuardShift.Day, CooldownShotSeconds = 10m });
        database.Guards[0].Ammunition.Add(new ItemAmount("bolt", 1m));
        var plan = new ProductionPlan { ExternalItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bolt" } };
        plan.Guards.Add(new GuardAssignment { GuardTypeId = "day", Count = 1, AmmoMode = GuardAmmoMode.CustomUtilisation, UtilisationPercent = 50m });

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.Equal(21m, result.Demand["bolt"].GuardPerCycle);
    }

    [Fact]
    public void finds_a_wrought_iron_plan_with_configured_progression()
    {
        var database = new GameDataLoader().Load(FindVanillaGameDataPath());
        var plan = Plan(("ironwrought", 10m));
        plan.UnlockedSciences = database.Sciences.Select(science => science.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        plan.AvailableTools = database.Tools.Select(tool => tool.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible, string.Join(Environment.NewLine, result.Messages.Select(message => message.Text)));
        Assert.Contains(result.RecipeAllocations, allocation => allocation.RecipeId == "pipliz.coppersmith.ironwrought");
        Assert.Contains(result.RecipeAllocations, allocation => allocation.RecipeId == "pipliz.minerjob.infiniteiron");
        Assert.DoesNotContain(result.ExternalRequirements, requirement => requirement.ItemId == "ironore");
        Assert.NotEmpty(result.ProductionFlows);
        Assert.True(result.ProductionFlows.SelectMany(flow => new[] { flow.SourceId, flow.TargetId }).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
    }

    [Fact]
    public void models_wheat_area_farms_from_nightly_growth_stages()
    {
        var database = new GameDataLoader().Load(FindVanillaGameDataPath());
        var plan = Plan(("wheat", 100m));
        plan.UnlockedSciences.Add("pipliz.farming");
        plan.CropFarmLayouts["pipliz.wheatfarm"] = new CropFarmLayout { Width = 1, Length = 200 };

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible, string.Join(Environment.NewLine, result.Messages.Select(message => message.Text)));
        var allocation = Assert.Single(result.RecipeAllocations, entry => entry.RecipeId == "pipliz.wheatfarm.harvest");
        Assert.Equal(1, allocation.CraftsPerCycle);
        Assert.Equal("Farm", allocation.UnitLabel);
        Assert.Contains(result.JobRequirements, job => job.JobTypeId == "pipliz.wheatfarmer" && job.Workers == 1);
        Assert.DoesNotContain(result.ExternalRequirements, requirement => requirement.ItemId == "wheat");
    }

    [Fact]
    public void models_every_vanilla_simple_crop_farm_in_one_plan()
    {
        var database = new GameDataLoader().Load(FindVanillaGameDataPath());
        var plan = new ProductionPlan
        {
            UnlockedSciences = database.Sciences.Select(science => science.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
        foreach (var source in database.CropFarmSources)
        {
            var primaryOutput = source.Outputs[0].ItemId;
            plan.Targets.Add(new DemandTarget { ItemId = primaryOutput, Amount = 50m, Unit = DemandUnit.PerCycle });
        }

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible, string.Join(Environment.NewLine, result.Messages.Select(message => message.Text)));
        Assert.Equal(9, result.RecipeAllocations.Count(allocation => allocation.RecipeId.EndsWith(".harvest", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(result.ExternalRequirements, requirement => plan.Targets.Any(target => target.ItemId == requirement.ItemId));
    }

    [Fact]
    public void models_forestry_with_the_standard_three_by_thirty_three_plot()
    {
        var database = new GameDataLoader().Load(FindVanillaGameDataPath());
        var plan = Plan(("logs", 100m));
        plan.UnlockedSciences = database.Sciences.Select(science => science.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible, string.Join(Environment.NewLine, result.Messages.Select(message => message.Text)));
        Assert.Contains(database.ForestrySources, source => source.Id == "forestry" && source.DefaultForesterCount == 1 && source.DefaultPlotWidth == 3 && source.DefaultPlotLength == 33);
        Assert.Contains(result.RecipeAllocations, allocation => allocation.RecipeId == "forestry.harvest" && allocation.UnitLabel == "Forest");
        Assert.Contains(result.JobRequirements, job => job.JobTypeId == "pipliz.forester" && job.Workers == 3);
    }

    [Fact]
    public void applies_the_default_toolset_to_manual_crafting_blocks()
    {
        var database = new GameDataLoader().Load(FindVanillaGameDataPath());

        Assert.Contains(database.Jobs, job => job.Id == "pipliz.tailor" && job.ToolsetId == "default");
        Assert.Contains(database.Jobs, job => job.Id == "pipliz.cook" && job.ToolsetId == "default");
        Assert.Contains(database.Jobs, job => job.Id == "pipliz.odditypress" && job.IsAutomatedQueue && job.ToolsetId is null);
    }

    [Fact]
    public void models_oddity_press_as_an_automatic_queued_machine()
    {
        var database = new GameDataLoader().Load(FindVanillaGameDataPath());
        var recipe = database.Recipes.First(candidate => candidate.JobTypeId.Equals("pipliz.odditypress", StringComparison.OrdinalIgnoreCase));
        var plan = Plan((recipe.Outputs[0].ItemId, 1m));
        plan.UnlockedSciences = database.Sciences.Select(science => science.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings());

        Assert.True(result.IsFeasible, string.Join(Environment.NewLine, result.Messages.Select(message => message.Text)));
        Assert.Contains(database.Jobs, job => job.Id == "pipliz.odditypress" && job.IsAutomatedQueue);
        Assert.Contains(result.RecipeAllocations, allocation => allocation.RecipeId == recipe.Id && allocation.IsAutomatedQueue);
        var machine = Assert.Single(result.JobRequirements, job => job.JobTypeId == "pipliz.odditypress");
        Assert.True(machine.IsAutomatedQueue);
        Assert.Equal(0, machine.Workers);
        Assert.True(machine.MachineBlocks > 0);
    }

    [Fact]
    public void applies_cycle_timing_override_to_per_minute_targets()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        var plan = new ProductionPlan
        {
            Targets =
            [
                new DemandTarget { ItemId = "widget", Amount = 1m, Unit = DemandUnit.PerMinute }
            ],
            ExternalItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "widget" }
        };

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings
        {
            TimingOverride = new TimingOverride { IsEnabled = true, GameTimeScale = 240m }
        });

        Assert.True(result.IsFeasible);
        Assert.Equal(6m, result.Demand["widget"].PlayerPerCycle);
        Assert.Equal(6m, result.ExternalRequirements.Single().PerCycle);
    }

    [Fact]
    public void applies_cycle_timing_override_to_per_second_targets()
    {
        var database = CreateDatabase(activeSeconds: 100m);
        var plan = new ProductionPlan
        {
            Targets =
            [
                new DemandTarget { ItemId = "widget", Amount = 1m, Unit = DemandUnit.PerSecond }
            ],
            ExternalItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "widget" }
        };

        var result = new ProductionOptimizer().Optimize(database, plan, new OptimizationSettings
        {
            TimingOverride = new TimingOverride { IsEnabled = true, GameTimeScale = 240m }
        });

        Assert.True(result.IsFeasible);
        Assert.Equal(360m, result.Demand["widget"].PlayerPerCycle);
        Assert.Equal(360m, result.ExternalRequirements.Single().PerCycle);
    }

    [Fact]
    public void imports_completed_sciences_from_a_read_only_world_database()
    {
        var root = Path.Combine(Path.GetTempPath(), $"colony-optimizer-{Guid.NewGuid():N}");
        var worldPath = Path.Combine(root, "world.sqlite3");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(worldPath, []);
            var importer = new SaveGameImportService();
            Assert.Throws<SqliteException>(() => importer.Import(worldPath));

            using (var connection = new SqliteConnection($"Data Source={worldPath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE science_mapping (name TEXT NOT NULL, [index] INTEGER NOT NULL);
                    CREATE TABLE colonygroups (json TEXT);
                    INSERT INTO science_mapping (name, [index]) VALUES ('pipliz.farming', 2), ('pipliz.forestry', 7);
                    INSERT INTO colonygroups (json) VALUES ('{"science":{"completed":[2,7]}}');
                    """;
                command.ExecuteNonQuery();
            }

            var imported = importer.Import(worldPath);

            Assert.Equal(2, imported.UnlockedScienceIds.Count);
            Assert.Contains("pipliz.farming", imported.UnlockedScienceIds);
            Assert.Contains("pipliz.forestry", imported.UnlockedScienceIds);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void finds_the_last_launched_world_database()
    {
        var root = Path.Combine(Path.GetTempPath(), $"colony-optimizer-{Guid.NewGuid():N}");
        var worldPath = Path.Combine(root, "savegames", "_cloud", "123", "Example", "world.sqlite3");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(worldPath)!);
            File.WriteAllBytes(worldPath, []);
            File.WriteAllText(Path.Combine(root, "savegames", "last_launch_options.json"), """{"LoadOptions":{"WorldName":"_cloud/123/Example"}}""");

            var found = new SaveGameImportService().FindLastWorldDatabase(root);

            Assert.Equal(worldPath, found);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void discovers_world_databases_in_a_standard_steam_library_on_a_supplied_drive_root()
    {
        var root = Path.Combine(Path.GetTempPath(), $"colony-optimizer-{Guid.NewGuid():N}");
        var gameDataPath = Path.Combine(root, "steamapps", "common", "Colony Survival", "gamedata");
        var firstWorld = Path.Combine(gameDataPath, "savegames", "Local world", "world.sqlite3");
        var cloudWorld = Path.Combine(gameDataPath, "savegames", "_cloud", "123", "Cloud world", "world.sqlite3");
        try
        {
            Directory.CreateDirectory(Path.Combine(gameDataPath, "baseconfig"));
            File.WriteAllText(Path.Combine(gameDataPath, "baseconfig", "modInfo.json"), "[]");
            Directory.CreateDirectory(Path.GetDirectoryName(firstWorld)!);
            Directory.CreateDirectory(Path.GetDirectoryName(cloudWorld)!);
            File.WriteAllBytes(firstWorld, []);
            File.WriteAllBytes(cloudWorld, []);

            var discovered = new GameDataAcquisition().FindWorldSaveDatabases([root]);

            var worldsOnTemporaryDrive = discovered.Where(path => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.Equal(2, worldsOnTemporaryDrive.Length);
            Assert.Contains(firstWorld, worldsOnTemporaryDrive);
            Assert.Contains(cloudWorld, worldsOnTemporaryDrive);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static GameDatabase CreateDatabase(decimal activeSeconds, string? toolset = null)
    {
        var database = new GameDatabase();
        database.Jobs.Add(new JobTypeDefinition
        {
            Id = "worker",
            DisplayName = "Worker",
            ToolsetId = toolset,
            ActiveSecondsPerCycle = activeSeconds
        });
        return database;
    }

    private static RecipeDefinition Recipe(string id, string jobTypeId, decimal cooldownSeconds, string outputId, decimal outputAmount = 1m)
    {
        var recipe = new RecipeDefinition
        {
            Id = id,
            DisplayName = id,
            JobTypeId = jobTypeId,
            CooldownSeconds = cooldownSeconds
        };
        recipe.Outputs.Add(new ItemAmount(outputId, outputAmount));
        return recipe;
    }

    private static void AssertCropFarmCoverage(GameDatabase database)
    {
        var expected = new Dictionary<string, (string Output, int Fertility, decimal Cycles)>(StringComparer.OrdinalIgnoreCase)
        {
            ["pipliz.wheatfarm"] = ("wheat", 1, 2m),
            ["pipliz.cabbagefarm"] = ("cabbage", 1, 1m),
            ["pipliz.barleyfarmer"] = ("barley", 1, 1m),
            ["pipliz.cottonfarmer"] = ("cotton", 4, 1m),
            ["pipliz.hempfarmer"] = ("hemp", 2, 1m),
            ["pipliz.alkanetfarmer"] = ("alkanet", 2, 1m),
            ["pipliz.wolfsbanefarmer"] = ("wolfsbane", 2, 1m),
            ["pipliz.hollyhockfarmer"] = ("hollyhock", 2, 1m),
            ["pipliz.flaxfarm"] = ("flax", 1, 1m)
        };

        Assert.Equal(expected.Count, database.CropFarmSources.Count);
        foreach (var (id, expectedFarm) in expected)
        {
            var farm = Assert.Single(database.CropFarmSources, source => source.Id == id);
            Assert.Equal(expectedFarm.Fertility, farm.FertilityRequirement);
            Assert.Equal(expectedFarm.Cycles, farm.GrowthCyclesPerHarvest);
            Assert.Contains(farm.Outputs, output => output.ItemId == expectedFarm.Output);
        }
    }

    private static ProductionPlan Plan(params (string Item, decimal Amount)[] targets) => new()
    {
        Targets = targets.Select(target => new DemandTarget
        {
            ItemId = target.Item,
            Amount = target.Amount,
            Unit = DemandUnit.PerCycle
        }).ToList()
    };

    private static string FindVanillaGameDataPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("COLONY_SURVIVAL_GAMEDATA");
        if (!string.IsNullOrWhiteSpace(configuredPath) && Directory.Exists(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "work", "ColonySurvival", "gamedata")))
            {
                return Path.Combine(current.FullName, "work", "ColonySurvival", "gamedata");
            }
        }

        throw new DirectoryNotFoundException("The checked-out vanilla game data was not found.");
    }
}
