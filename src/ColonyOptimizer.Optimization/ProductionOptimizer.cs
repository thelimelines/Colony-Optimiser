using ColonyOptimizer.Core;
using Google.OrTools.Sat;

namespace ColonyOptimizer.Optimization;

public sealed class ProductionOptimizer
{
    private const long ItemScale = 1_000_000;
    private const long MillisecondsPerSecond = 1_000;

    public OptimizationResult Optimize(GameDatabase database, ProductionPlan plan, OptimizationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);

        var timing = settings.TimingOverride.Apply(database.Timing);
        var demand = BuildDemand(database, plan, timing);
        var availableRecipes = database.Recipes
            .Concat(BuildCropFarmRecipes(database, plan, timing))
            .Concat(BuildForestryRecipes(database, plan, timing))
            .ToArray();
        var eligibility = DetermineEligibleRecipes(plan, availableRecipes);
        var externalItems = DetermineEffectiveExternalItems(plan, availableRecipes);
        var result = new OptimizationResult { Demand = demand };
        result.Messages.AddRange(eligibility.Messages);

        AddPreflightMessages(demand, availableRecipes, eligibility, externalItems, result.Messages);
        if (result.Messages.Any(message => message.Severity == OptimizationMessageSeverity.Error))
        {
            return result;
        }

        var model = new CpModel();
        var craftVariables = eligibility.Recipes.ToDictionary(
            recipe => recipe.Id,
            recipe => model.NewIntVar(0, settings.MaxCraftsPerRecipe, $"craft_{Sanitize(recipe.Id)}"),
            StringComparer.OrdinalIgnoreCase);

        var jobCapacities = eligibility.Recipes
            .GroupBy(recipe => recipe.JobTypeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => ResolveJobCapacity(database, plan, settings, timing, group.Key), StringComparer.OrdinalIgnoreCase);
        // A job block is either a worker-operated job or an automated machine. Both
        // need a capacity variable, but only the former contributes to worker count.
        var jobBlockVariables = jobCapacities.ToDictionary(
            entry => entry.Key,
            entry => model.NewIntVar(0, settings.MaxWorkersPerJob, $"job_blocks_{Sanitize(entry.Key)}"),
            StringComparer.OrdinalIgnoreCase);

        AddSelectedToolsWithoutProducers(availableRecipes, jobCapacities, externalItems);
        AddMaterialConstraints(model, settings.StochasticOutputPolicy, demand, externalItems, eligibility.Recipes, craftVariables, jobCapacities);
        AddWorkloadConstraints(model, eligibility.Recipes, craftVariables, jobBlockVariables, jobCapacities);

        var totalWorkers = CreateMetric(
            model,
            jobBlockVariables.Where(entry => !jobCapacities[entry.Key].IsAutomatedQueue).Select(entry => entry.Value),
            jobBlockVariables.Where(entry => !jobCapacities[entry.Key].IsAutomatedQueue).Select(_ => 1L));
        var totalMachineBlocks = CreateMetric(
            model,
            jobBlockVariables.Where(entry => jobCapacities[entry.Key].IsAutomatedQueue).Select(entry => entry.Value),
            jobBlockVariables.Where(entry => jobCapacities[entry.Key].IsAutomatedQueue).Select(_ => 1L));
        var workerRecipeOutputs = eligibility.Recipes
            .Where(recipe => !jobCapacities[recipe.JobTypeId].IsAutomatedQueue)
            .SelectMany(recipe => recipe.Outputs)
            .Select(output => output.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var automatedFallbackRecipes = eligibility.Recipes
            .Where(recipe =>
                jobCapacities[recipe.JobTypeId].IsAutomatedQueue &&
                plan.RecipePolicies.GetValueOrDefault(recipe.Id) is not RecipePolicy.Preferred and not RecipePolicy.Forced &&
                recipe.Outputs.All(output => workerRecipeOutputs.Contains(output.ItemId)))
            .ToArray();
        var automatedFallbackPenalty = CreateMetric(
            model,
            automatedFallbackRecipes.Select(recipe => craftVariables[recipe.Id]),
            automatedFallbackRecipes.Select(_ => 1L));
        var preferencePenalty = CreateMetric(
            model,
            craftVariables.Where(entry => plan.RecipePolicies.GetValueOrDefault(entry.Key) != RecipePolicy.Preferred && plan.RecipePolicies.GetValueOrDefault(entry.Key) != RecipePolicy.Forced).Select(entry => entry.Value),
            craftVariables.Where(entry => plan.RecipePolicies.GetValueOrDefault(entry.Key) != RecipePolicy.Preferred && plan.RecipePolicies.GetValueOrDefault(entry.Key) != RecipePolicy.Forced).Select(_ => 1L));
        var workload = CreateMetric(
            model,
            eligibility.Recipes.Select(recipe => craftVariables[recipe.Id]),
            eligibility.Recipes.Select(recipe => EffectiveWorkloadMilliseconds(recipe, jobCapacities[recipe.JobTypeId])));
        var rawConsumption = CreateMetric(
            model,
            eligibility.Recipes.Select(recipe => craftVariables[recipe.Id]),
            eligibility.Recipes.Select(recipe =>
            {
                var capacity = jobCapacities[recipe.JobTypeId];
                var externalInputs = recipe.Inputs.Where(input => externalItems.Contains(input.ItemId)).Sum(input => input.Amount);
                var externalToolUse = capacity.IsConsumableTool && externalItems.Contains(capacity.SelectedToolId!)
                    ? ToolUsePerCraft(recipe, capacity)
                    : 0m;
                return Scale(externalInputs + externalToolUse);
            }));

        var solver = new CpSolver { StringParameters = "max_time_in_seconds: 20 num_search_workers: 8" };
        using var cancellationRegistration = cancellationToken.Register(solver.StopSearch);
        var status = SolveLexicographically(model, solver, settings.Objective, automatedFallbackPenalty, totalWorkers, totalMachineBlocks, preferencePenalty, rawConsumption, workload);
        result.SolverStatus = status.ToString();
        result.IsOptimal = status == CpSolverStatus.Optimal;
        if (status is not CpSolverStatus.Optimal and not CpSolverStatus.Feasible)
        {
            result.Messages.Add(new OptimizationMessage(OptimizationMessageSeverity.Error, BuildInfeasibleExplanation(demand, eligibility.Recipes, externalItems)));
            return result;
        }

        if (!result.IsOptimal)
        {
            result.Messages.Add(new OptimizationMessage(OptimizationMessageSeverity.Warning, "The solver reached its time limit before proving an optimum. This is a feasible approximate result."));
        }

        PopulateResult(database, plan, timing, settings.StochasticOutputPolicy, demand, externalItems, eligibility.Recipes, craftVariables, jobBlockVariables, jobCapacities, solver, result);
        result.IsFeasible = true;
        return result;
    }

    private static Dictionary<string, DemandBreakdown> BuildDemand(GameDatabase database, ProductionPlan plan, GameTiming timing)
    {
        var demand = new Dictionary<string, DemandBreakdown>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in plan.Targets.Where(target => target.Amount > 0m && !string.IsNullOrWhiteSpace(target.ItemId)))
        {
            var breakdown = GetDemand(demand, target.ItemId);
            breakdown.PlayerPerCycle += target.ToPerCycle(timing);
        }

        foreach (var assignment in plan.Guards.Where(assignment => assignment.Count > 0))
        {
            var guard = database.Guards.FirstOrDefault(candidate => candidate.Id.Equals(assignment.GuardTypeId, StringComparison.OrdinalIgnoreCase));
            if (guard is null || guard.CooldownShotSeconds <= 0m)
            {
                continue;
            }

            var shots = CalculateGuardShots(timing, guard, assignment.AmmoMode, assignment.UtilisationPercent, assignment.CustomRoundsPerCycle);

            foreach (var ammo in guard.Ammunition)
            {
                GetDemand(demand, ammo.ItemId).GuardPerCycle += shots * assignment.Count * ammo.Amount;
            }
        }

        foreach (var assignment in plan.Traps.Where(assignment => assignment.Count > 0))
        {
            var trap = database.Traps.FirstOrDefault(candidate => candidate.Id.Equals(assignment.TrapTypeId, StringComparison.OrdinalIgnoreCase));
            if (trap is null || trap.AmmunitionCapacity <= 0 || string.IsNullOrWhiteSpace(trap.AmmunitionItemId))
            {
                continue;
            }

            // Trap fixers restore the configured capacity one round at a time. Budgeting one full
            // refill per cycle keeps enough consumable ammunition available after each work period.
            GetDemand(demand, trap.AmmunitionItemId).TrapPerCycle += assignment.Count * trap.AmmunitionCapacity;
        }

        return demand;
    }

    private static DemandBreakdown GetDemand(IDictionary<string, DemandBreakdown> demand, string itemId)
    {
        if (!demand.TryGetValue(itemId, out var breakdown))
        {
            breakdown = new DemandBreakdown { ItemId = itemId };
            demand.Add(itemId, breakdown);
        }

        return breakdown;
    }

    private static IEnumerable<RecipeDefinition> BuildCropFarmRecipes(GameDatabase database, ProductionPlan plan, GameTiming timing)
    {
        foreach (var source in database.CropFarmSources)
        {
            var savedLayout = plan.CropFarmLayouts.GetValueOrDefault(source.Id);
            var fieldTiles = savedLayout is { Width: > 0, Length: > 0 }
                ? savedLayout.Width * savedLayout.Length
                : Math.Max(1, source.DefaultFieldTiles);
            if (source.GrowthCyclesPerHarvest <= 0m || source.Outputs.Count == 0)
            {
                continue;
            }

            var harvestedTilesPerCycle = fieldTiles / source.GrowthCyclesPerHarvest;
            var recipe = new RecipeDefinition
            {
                Id = $"{source.Id}.harvest",
                DisplayName = source.DisplayName,
                JobTypeId = source.JobTypeId,
                CooldownSeconds = timing.CycleSeconds,
                WorkloadSeconds = fieldTiles * source.HarvestActionSecondsPerTile / source.GrowthCyclesPerHarvest,
                DedicatedWorkersPerCraft = 1,
                UnitLabel = "Farm",
                RequiredScience = source.RequiredScience,
                SourceFile = source.SourceFile
            };
            recipe.Outputs.AddRange(source.Outputs.Select(output => output with { Amount = output.Amount * harvestedTilesPerCycle }));
            yield return recipe;
        }
    }

    private static IEnumerable<RecipeDefinition> BuildForestryRecipes(GameDatabase database, ProductionPlan plan, GameTiming timing)
    {
        foreach (var source in database.ForestrySources)
        {
            var layout = plan.ForestryLayouts.GetValueOrDefault(source.Id);
            var foresters = layout is { ForesterCount: > 0 }
                ? layout.ForesterCount
                : Math.Max(1, source.DefaultForesterCount);
            var plotWidth = layout is { PlotWidth: > 0 }
                ? layout.PlotWidth
                : Math.Max(1, source.DefaultPlotWidth);
            var plotLength = layout is { PlotLength: > 0 }
                ? layout.PlotLength
                : Math.Max(1, source.DefaultPlotLength);
            var totalTrees = ForestryLayout.GetTreeSlotCount(plotWidth, plotLength);
            var job = database.Jobs.FirstOrDefault(candidate => candidate.Id.Equals(source.JobTypeId, StringComparison.OrdinalIgnoreCase));
            var activeSecondsPerForester = job?.ActiveSecondsPerCycle ?? timing.WorkerActiveSeconds;
            var harvestCapacityPerForester = source.GetHarvestCapacityPerForester(activeSecondsPerForester);
            if (harvestCapacityPerForester <= 0)
            {
                continue;
            }

            var harvestCapacity = foresters * harvestCapacityPerForester;
            var harvestedTrees = Math.Min(totalTrees, harvestCapacity);
            var workersRequired = Math.Max(1, (int)Math.Ceiling(harvestedTrees / (decimal)harvestCapacityPerForester));
            var recipe = new RecipeDefinition
            {
                Id = $"{source.Id}.harvest",
                DisplayName = source.DisplayName,
                JobTypeId = source.JobTypeId,
                CooldownSeconds = timing.CycleSeconds,
                WorkloadSeconds = harvestedTrees * source.WorkSecondsPerTree,
                DedicatedWorkersPerCraft = workersRequired,
                UnitLabel = "Forest",
                RequiredScience = source.RequiredScience,
                SourceFile = source.SourceFile
            };
            recipe.Outputs.Add(new ItemAmount(source.LogItemId, harvestedTrees * source.LogsPerTree));
            recipe.Outputs.Add(new ItemAmount(source.LeavesItemId, harvestedTrees * source.LeavesPerTree));
            yield return recipe;
        }
    }

    private static RecipeEligibility DetermineEligibleRecipes(ProductionPlan plan, IReadOnlyCollection<RecipeDefinition> availableRecipes)
    {
        var messages = new List<OptimizationMessage>();
        var forcedOutputs = availableRecipes
            .Where(recipe => plan.RecipePolicies.GetValueOrDefault(recipe.Id) == RecipePolicy.Forced)
            .SelectMany(recipe => recipe.Outputs)
            .Select(output => output.ItemId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var eligible = new List<RecipeDefinition>();
        foreach (var recipe in availableRecipes)
        {
            if (recipe.JobTypeId.Equals("player", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var policy = plan.RecipePolicies.GetValueOrDefault(recipe.Id);
            if (policy == RecipePolicy.Forbidden)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(recipe.RequiredScience) && !plan.UnlockedSciences.Contains(recipe.RequiredScience))
            {
                continue;
            }

            if (policy != RecipePolicy.Forced && recipe.Outputs.Any(output => forcedOutputs.Contains(output.ItemId)))
            {
                continue;
            }

            eligible.Add(recipe);
        }

        foreach (var output in forcedOutputs)
        {
            if (!eligible.Any(recipe => recipe.Outputs.Any(amount => amount.ItemId.Equals(output, StringComparison.OrdinalIgnoreCase))))
            {
                messages.Add(new OptimizationMessage(OptimizationMessageSeverity.Error, $"Forced recipes for '{output}' cannot be used because they are forbidden or need unavailable science."));
            }
        }

        return new RecipeEligibility(eligible, messages);
    }

    private static int CalculateGuardShots(GameTiming timing, GuardTypeDefinition guard, GuardAmmoMode mode, decimal utilisationPercent, int? customRoundsPerCycle)
    {
        return mode switch
        {
            GuardAmmoMode.CustomRoundsPerCycle => Math.Max(0, customRoundsPerCycle ?? 0),
            GuardAmmoMode.HostilePeriodOnly => RoundShots(timing.GetHostileGuardOverlapSeconds(guard.Shift), guard.CooldownShotSeconds),
            GuardAmmoMode.CustomUtilisation => RoundShots(timing.GetGuardShiftSeconds(guard.Shift), guard.CooldownShotSeconds, utilisationPercent),
            _ => RoundShots(timing.GetGuardShiftSeconds(guard.Shift), guard.CooldownShotSeconds)
        };
    }

    private static int RoundShots(decimal durationSeconds, decimal cooldownShotSeconds, decimal utilisationPercent = 100m) =>
        cooldownShotSeconds <= 0m
            ? 0
            : (int)Math.Ceiling(durationSeconds / cooldownShotSeconds * Math.Clamp(utilisationPercent, 0m, 100m) / 100m);

    private static HashSet<string> DetermineEffectiveExternalItems(ProductionPlan plan, IReadOnlyCollection<RecipeDefinition> availableRecipes)
    {
        var externalItems = new HashSet<string>(plan.ExternalItems, StringComparer.OrdinalIgnoreCase);
        var recipes = availableRecipes.Where(recipe => !recipe.JobTypeId.Equals("player", StringComparison.OrdinalIgnoreCase)).ToArray();
        var producerItems = recipes.SelectMany(recipe => recipe.Outputs).Select(output => output.ItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceItem in recipes.SelectMany(recipe => recipe.Inputs).Select(input => input.ItemId).Where(itemId => !producerItems.Contains(itemId)))
        {
            externalItems.Add(sourceItem);
        }

        return externalItems;
    }

    private static void AddSelectedToolsWithoutProducers(IReadOnlyCollection<RecipeDefinition> recipes, IReadOnlyDictionary<string, JobCapacity> capacities, ISet<string> externalItems)
    {
        foreach (var toolId in capacities.Values.Where(capacity => capacity.IsConsumableTool).Select(capacity => capacity.SelectedToolId!))
        {
            if (!recipes.Any(recipe => recipe.Outputs.Any(output => output.ItemId.Equals(toolId, StringComparison.OrdinalIgnoreCase))))
            {
                externalItems.Add(toolId);
            }
        }
    }

    private static void AddPreflightMessages(IReadOnlyDictionary<string, DemandBreakdown> demand, IReadOnlyCollection<RecipeDefinition> availableRecipes, RecipeEligibility eligibility, IReadOnlySet<string> externalItems, ICollection<OptimizationMessage> messages)
    {
        foreach (var item in demand.Keys.Where(item => !externalItems.Contains(item)))
        {
            if (eligibility.Recipes.Any(recipe => recipe.Outputs.Any(output => output.ItemId.Equals(item, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            var allRecipes = availableRecipes.Where(recipe => recipe.Outputs.Any(output => output.ItemId.Equals(item, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (allRecipes.Length == 0)
            {
                messages.Add(new OptimizationMessage(OptimizationMessageSeverity.Error, $"'{DisplayName.FromIdentifier(item)}' has no parsed producer. Mark it externally supplied or load data that defines its production."));
                continue;
            }

            var science = allRecipes.Select(recipe => recipe.RequiredScience).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
            var explanation = science.Length > 0
                ? $"'{DisplayName.FromIdentifier(item)}' cannot be produced because its enabled recipes require: {string.Join(", ", science)}."
                : $"All recipes producing '{DisplayName.FromIdentifier(item)}' are forbidden or forced out.";
            messages.Add(new OptimizationMessage(OptimizationMessageSeverity.Error, explanation));
        }
    }

    private static void AddMaterialConstraints(CpModel model, StochasticOutputPolicy stochasticOutputPolicy, IReadOnlyDictionary<string, DemandBreakdown> demand, IReadOnlySet<string> externalItems, IReadOnlyCollection<RecipeDefinition> recipes, IReadOnlyDictionary<string, IntVar> craftVariables, IReadOnlyDictionary<string, JobCapacity> capacities)
    {
        var itemIds = recipes.SelectMany(recipe => recipe.Inputs.Concat(recipe.Outputs)).Select(amount => amount.ItemId)
            .Concat(demand.Keys)
            .Concat(capacities.Values.Where(capacity => capacity.IsConsumableTool).Select(capacity => capacity.SelectedToolId!))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var itemId in itemIds)
        {
            if (externalItems.Contains(itemId))
            {
                continue;
            }

            var variables = new List<IntVar>();
            var coefficients = new List<long>();
            foreach (var recipe in recipes)
            {
                var produced = recipe.Outputs.Where(amount => amount.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
                    .Sum(amount => GetOutputCoefficient(amount, stochasticOutputPolicy));
                var consumed = recipe.Inputs.Where(amount => amount.ItemId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
                    .Sum(amount => amount.Amount);
                var capacity = capacities[recipe.JobTypeId];
                if (capacity.IsConsumableTool && capacity.SelectedToolId!.Equals(itemId, StringComparison.OrdinalIgnoreCase))
                {
                    consumed += ToolUsePerCraft(recipe, capacity);
                }
                var coefficient = Scale(produced - consumed);
                if (coefficient != 0)
                {
                    variables.Add(craftVariables[recipe.Id]);
                    coefficients.Add(coefficient);
                }
            }

            var required = Scale(demand.TryGetValue(itemId, out var target) ? target.TotalPerCycle : 0m);
            model.Add(CreateExpression(model, variables, coefficients) >= required);
        }
    }

    private static void AddWorkloadConstraints(CpModel model, IReadOnlyCollection<RecipeDefinition> recipes, IReadOnlyDictionary<string, IntVar> craftVariables, IReadOnlyDictionary<string, IntVar> jobBlockVariables, IReadOnlyDictionary<string, JobCapacity> capacities)
    {
        foreach (var group in recipes.GroupBy(recipe => recipe.JobTypeId, StringComparer.OrdinalIgnoreCase))
        {
            var capacity = capacities[group.Key];
            var crafts = group.Select(recipe => craftVariables[recipe.Id]).ToArray();
            var milliseconds = group.Select(recipe => EffectiveWorkloadMilliseconds(recipe, capacity)).ToArray();
            model.Add(CreateExpression(model, crafts, milliseconds) <= jobBlockVariables[group.Key] * capacity.AvailableMillisecondsPerBlock);

            var dedicatedCrafts = capacity.IsAutomatedQueue
                ? []
                : group.Where(recipe => recipe.DedicatedWorkersPerCraft > 0).ToArray();
            if (dedicatedCrafts.Length > 0)
            {
                model.Add(CreateExpression(
                    model,
                    dedicatedCrafts.Select(recipe => craftVariables[recipe.Id]),
                    dedicatedCrafts.Select(recipe => (long)recipe.DedicatedWorkersPerCraft)) <= jobBlockVariables[group.Key]);
            }
        }
    }

    private static JobCapacity ResolveJobCapacity(GameDatabase database, ProductionPlan plan, OptimizationSettings settings, GameTiming timing, string jobTypeId)
    {
        var job = database.Jobs.FirstOrDefault(candidate => candidate.Id.Equals(jobTypeId, StringComparison.OrdinalIgnoreCase));
        // Queued machines run for the whole game cycle; worker-active time applies
        // only to colonists operating a job block.
        var activeSeconds = job?.IsAutomatedQueue == true
            ? timing.CycleSeconds
            : job?.ActiveSecondsPerCycle ?? timing.WorkerActiveSeconds;
        var availableMilliseconds = (long)Math.Floor(activeSeconds * MillisecondsPerSecond * Math.Clamp(settings.EfficiencyPercent, 0m, 100m) / 100m * (100m - Math.Clamp(settings.HeadroomPercent, 0m, 50m)) / 100m);
        var toolset = database.Toolsets.FirstOrDefault(candidate => candidate.Id.Equals(job?.ToolsetId, StringComparison.OrdinalIgnoreCase));
        var candidates = (toolset?.UsableTools ?? []).Where(plan.AvailableTools.Contains)
            .Select(toolId => database.Tools.FirstOrDefault(tool => tool.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase)))
            .Where(tool => tool is not null)
            .Cast<ToolDefinition>()
            .ToArray();
        var selectedTool = candidates.OrderByDescending(tool => tool.CraftingSpeed).FirstOrDefault();
        var multiplier = Math.Max(0.001m, (selectedTool?.CraftingSpeed ?? 1m) * (toolset?.UseMultiplier ?? 1m));
        return new JobCapacity(
            availableMilliseconds,
            selectedTool?.Id,
            multiplier,
            selectedTool?.Durability ?? 0m,
            selectedTool?.RequiresStockpileItem ?? false,
            job?.IsAutomatedQueue ?? false);
    }

    private static CpSolverStatus SolveLexicographically(CpModel model, CpSolver solver, OptimizationObjective objective, ObjectiveMetric automatedFallbacks, ObjectiveMetric totalWorkers, ObjectiveMetric totalMachineBlocks, ObjectiveMetric preferences, ObjectiveMetric rawConsumption, ObjectiveMetric workload)
    {
        var order = objective == OptimizationObjective.PreferredRecipesFirst
            ? new[] { preferences, automatedFallbacks, totalWorkers, totalMachineBlocks, workload }
            : objective == OptimizationObjective.LowestRawResourceConsumption
                ? new[] { rawConsumption, automatedFallbacks, totalWorkers, totalMachineBlocks, preferences, workload }
                : new[] { totalWorkers, totalMachineBlocks, automatedFallbacks, preferences, workload };

        CpSolverStatus status = CpSolverStatus.Unknown;
        foreach (var metric in order)
        {
            model.Minimize(metric.Expression);
            status = solver.Solve(model);
            if (status is not CpSolverStatus.Optimal and not CpSolverStatus.Feasible)
            {
                return status;
            }

            // A feasible incumbent has not proved this objective's optimum. Freezing
            // it would exclude better primary solutions from later objective stages.
            if (status != CpSolverStatus.Optimal)
            {
                return status;
            }

            model.Add(metric.Expression == metric.GetValue(solver));
        }

        return status;
    }

    private static void PopulateResult(GameDatabase database, ProductionPlan plan, GameTiming timing, StochasticOutputPolicy stochasticOutputPolicy, IReadOnlyDictionary<string, DemandBreakdown> demand, IReadOnlySet<string> externalItems, IReadOnlyCollection<RecipeDefinition> recipes, IReadOnlyDictionary<string, IntVar> craftVariables, IReadOnlyDictionary<string, IntVar> jobBlockVariables, IReadOnlyDictionary<string, JobCapacity> capacities, CpSolver solver, OptimizationResult result)
    {
        var netFlows = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipe in recipes)
        {
            var crafts = solver.Value(craftVariables[recipe.Id]);
            if (crafts <= 0)
            {
                continue;
            }

            var capacity = capacities[recipe.JobTypeId];
            result.RecipeAllocations.Add(new RecipeAllocation
            {
                RecipeId = recipe.Id,
                RecipeDisplayName = recipe.DisplayName,
                JobTypeId = recipe.JobTypeId,
                CraftsPerCycle = crafts,
                UnitLabel = recipe.UnitLabel,
                EffectiveCooldownSeconds = EffectiveWorkloadMilliseconds(recipe, capacity) / (decimal)MillisecondsPerSecond,
                SelectedToolId = capacity.SelectedToolId,
                WorkloadSeconds = crafts * EffectiveWorkloadMilliseconds(recipe, capacity) / (decimal)MillisecondsPerSecond,
                IsAutomatedQueue = capacity.IsAutomatedQueue
            });

            var recipeNodeId = $"recipe:{recipe.Id}";
            foreach (var input in recipe.Inputs)
            {
                result.ProductionFlows.Add(new ProductionFlow
                {
                    SourceId = $"item:{input.ItemId}",
                    SourceLabel = database.Items.FirstOrDefault(item => item.Id.Equals(input.ItemId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? DisplayName.FromIdentifier(input.ItemId),
                    SourceKind = "Item",
                    SourceJobBlock = null,
                    TargetId = recipeNodeId,
                    TargetLabel = recipe.DisplayName,
                    TargetKind = "Recipe",
                    TargetJobBlock = database.Jobs.FirstOrDefault(job => job.Id.Equals(recipe.JobTypeId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? DisplayName.FromIdentifier(recipe.JobTypeId),
                    ItemId = input.ItemId,
                    Amount = crafts * input.Amount
                });
            }

            foreach (var output in recipe.Outputs)
            {
                netFlows[output.ItemId] = netFlows.GetValueOrDefault(output.ItemId) + crafts * GetOutputCoefficient(output, stochasticOutputPolicy);
                result.ProductionFlows.Add(new ProductionFlow
                {
                    SourceId = recipeNodeId,
                    SourceLabel = recipe.DisplayName,
                    SourceKind = "Recipe",
                    SourceJobBlock = database.Jobs.FirstOrDefault(job => job.Id.Equals(recipe.JobTypeId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? DisplayName.FromIdentifier(recipe.JobTypeId),
                    TargetId = $"item:{output.ItemId}",
                    TargetLabel = database.Items.FirstOrDefault(item => item.Id.Equals(output.ItemId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? DisplayName.FromIdentifier(output.ItemId),
                    TargetKind = "Item",
                    TargetJobBlock = null,
                    ItemId = output.ItemId,
                    Amount = crafts * GetOutputCoefficient(output, stochasticOutputPolicy)
                });
            }

            foreach (var input in recipe.Inputs)
            {
                netFlows[input.ItemId] = netFlows.GetValueOrDefault(input.ItemId) - crafts * input.Amount;
            }

            if (capacity.IsConsumableTool)
            {
                var toolId = capacity.SelectedToolId!;
                netFlows[toolId] = netFlows.GetValueOrDefault(toolId) - crafts * ToolUsePerCraft(recipe, capacity);
            }
        }

        foreach (var job in jobBlockVariables)
        {
            var blockCount = solver.Value(job.Value);
            if (blockCount <= 0)
            {
                continue;
            }

            var capacity = capacities[job.Key];
            var workloadSeconds = result.RecipeAllocations.Where(allocation => allocation.JobTypeId.Equals(job.Key, StringComparison.OrdinalIgnoreCase)).Sum(allocation => allocation.WorkloadSeconds);
            result.JobRequirements.Add(new JobRequirement
            {
                JobTypeId = job.Key,
                JobDisplayName = database.Jobs.FirstOrDefault(candidate => candidate.Id.Equals(job.Key, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? DisplayName.FromIdentifier(job.Key),
                Workers = capacity.IsAutomatedQueue ? 0 : blockCount,
                MachineBlocks = capacity.IsAutomatedQueue ? blockCount : 0,
                IsAutomatedQueue = capacity.IsAutomatedQueue,
                WorkloadSeconds = workloadSeconds,
                CapacitySeconds = blockCount * capacity.AvailableMillisecondsPerBlock / (decimal)MillisecondsPerSecond,
                UtilisationPercent = blockCount == 0 || capacity.AvailableMillisecondsPerBlock == 0 ? 0m : workloadSeconds / (blockCount * capacity.AvailableMillisecondsPerBlock / (decimal)MillisecondsPerSecond) * 100m,
                SelectedToolId = capacity.SelectedToolId,
                SelectedToolDisplayName = capacity.SelectedToolId is null
                    ? null
                    : database.Tools.FirstOrDefault(candidate => candidate.Id.Equals(capacity.SelectedToolId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? DisplayName.FromIdentifier(capacity.SelectedToolId)
            });

            if (!capacity.IsAutomatedQueue && capacity.IsConsumableTool)
            {
                var toolId = capacity.SelectedToolId!;
                var tool = database.Tools.FirstOrDefault(candidate => candidate.Id.Equals(toolId, StringComparison.OrdinalIgnoreCase));
                var replacementPerCycle = recipes
                    .Where(recipe => recipe.JobTypeId.Equals(job.Key, StringComparison.OrdinalIgnoreCase))
                    .Sum(recipe => solver.Value(craftVariables[recipe.Id]) * ToolUsePerCraft(recipe, capacity));
                result.ToolRequirements.Add(new ToolRequirement
                {
                    JobTypeId = job.Key,
                    JobDisplayName = database.Jobs.FirstOrDefault(candidate => candidate.Id.Equals(job.Key, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? DisplayName.FromIdentifier(job.Key),
                    ToolId = toolId,
                    ToolDisplayName = tool?.DisplayName ?? DisplayName.FromIdentifier(toolId),
                    Quantity = blockCount,
                    CraftingSpeed = tool?.CraftingSpeed ?? 1m,
                    Durability = tool?.Durability ?? 0m,
                    ReplacementPerCycle = replacementPerCycle,
                    ReplacementPerMinute = timing.CycleSeconds > 0m ? replacementPerCycle * 60m / timing.CycleSeconds : 0m
                });
            }
        }

        foreach (var externalItem in externalItems)
        {
            var required = (demand.TryGetValue(externalItem, out var target) ? target.TotalPerCycle : 0m) - netFlows.GetValueOrDefault(externalItem);
            if (required > 0m)
            {
                result.ExternalRequirements.Add(new ExternalRequirement
                {
                    ItemId = externalItem,
                    ItemDisplayName = database.Items.FirstOrDefault(item => item.Id.Equals(externalItem, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? DisplayName.FromIdentifier(externalItem),
                    PerCycle = required,
                    PerMinute = timing.CycleSeconds > 0m ? required * 60m / timing.CycleSeconds : 0m,
                    IsAutomatic = !plan.ExternalItems.Contains(externalItem)
                });
            }
        }

        foreach (var output in netFlows.Where(flow => flow.Value > 0m).OrderBy(flow => database.Items.FirstOrDefault(item => item.Id.Equals(flow.Key, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? flow.Key))
        {
            result.TotalOutputs.Add(new ProductionOutput
            {
                ItemId = output.Key,
                ItemDisplayName = database.Items.FirstOrDefault(item => item.Id.Equals(output.Key, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? DisplayName.FromIdentifier(output.Key),
                PerCycle = output.Value,
                PerMinute = timing.CycleSeconds > 0m ? output.Value * 60m / timing.CycleSeconds : 0m
            });
        }
    }

    private static string BuildInfeasibleExplanation(IReadOnlyDictionary<string, DemandBreakdown> demand, IReadOnlyCollection<RecipeDefinition> recipes, IReadOnlySet<string> externalItems)
    {
        var constrained = demand.Keys.FirstOrDefault(item => !externalItems.Contains(item) && !recipes.Any(recipe => recipe.Outputs.Any(output => output.ItemId.Equals(item, StringComparison.OrdinalIgnoreCase))));
        return constrained is not null
            ? $"No enabled recipe produces '{DisplayName.FromIdentifier(constrained)}'. Review science, recipe rules, and external-source settings."
            : "No feasible production plan exists. The enabled recipes cannot satisfy all intermediate material balances and capacity limits.";
    }

    private static long EffectiveWorkloadMilliseconds(RecipeDefinition recipe, JobCapacity capacity) =>
        Math.Max(1L, (long)Math.Ceiling((recipe.WorkloadSeconds ?? recipe.CooldownSeconds) / capacity.ToolMultiplier * MillisecondsPerSecond));

    private static decimal ToolUsePerCraft(RecipeDefinition recipe, JobCapacity capacity) =>
        !capacity.IsConsumableTool
            ? 0m
            : EffectiveWorkloadMilliseconds(recipe, capacity) / (capacity.ToolDurabilitySeconds * MillisecondsPerSecond);

    private static decimal GetOutputCoefficient(ItemAmount amount, StochasticOutputPolicy policy)
    {
        return policy switch
        {
            StochasticOutputPolicy.IgnoreOptionalOutputs when amount.IsOptional => 0m,
            StochasticOutputPolicy.Conservative when amount.Chance < 1m => 0m,
            _ => amount.ExpectedAmount
        };
    }

    private static long Scale(decimal value) => checked((long)Math.Round(value * ItemScale, MidpointRounding.AwayFromZero));

    private static LinearExpr CreateExpression(CpModel model, IEnumerable<IntVar> variables, IEnumerable<long> coefficients)
    {
        var variablesArray = variables.ToArray();
        var coefficientsArray = coefficients.ToArray();
        return variablesArray.Length == 0 ? model.NewConstant(0) : LinearExpr.WeightedSum(variablesArray, coefficientsArray);
    }

    private static ObjectiveMetric CreateMetric(CpModel model, IEnumerable<IntVar> variables, IEnumerable<long> coefficients)
    {
        var variableArray = variables.ToArray();
        var coefficientArray = coefficients.ToArray();
        return new ObjectiveMetric(CreateExpression(model, variableArray, coefficientArray), variableArray, coefficientArray);
    }

    private static string Sanitize(string value) => string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '_'));

    private sealed record RecipeEligibility(IReadOnlyCollection<RecipeDefinition> Recipes, IReadOnlyCollection<OptimizationMessage> Messages);
    private sealed record JobCapacity(long AvailableMillisecondsPerBlock, string? SelectedToolId, decimal ToolMultiplier, decimal ToolDurabilitySeconds, bool ToolRequiresStockpileItem, bool IsAutomatedQueue)
    {
        public bool IsConsumableTool => ToolRequiresStockpileItem && !string.IsNullOrWhiteSpace(SelectedToolId) && ToolDurabilitySeconds > 0m;
    }
    private sealed record ObjectiveMetric(LinearExpr Expression, IReadOnlyList<IntVar> Variables, IReadOnlyList<long> Coefficients)
    {
        public long GetValue(CpSolver solver) => Variables.Select((variable, index) => solver.Value(variable) * Coefficients[index]).Sum();
    }
}

public sealed class OptimizationResult
{
    public bool IsFeasible { get; set; }
    public bool IsOptimal { get; set; }
    public string SolverStatus { get; set; } = "Not started";
    public Dictionary<string, DemandBreakdown> Demand { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<OptimizationMessage> Messages { get; } = [];
    public List<JobRequirement> JobRequirements { get; } = [];
    public List<RecipeAllocation> RecipeAllocations { get; } = [];
    public List<ToolRequirement> ToolRequirements { get; } = [];
    public List<ExternalRequirement> ExternalRequirements { get; } = [];
    public List<ProductionOutput> TotalOutputs { get; } = [];
    public List<ProductionFlow> ProductionFlows { get; } = [];
    public long TotalWorkers => JobRequirements.Sum(requirement => requirement.Workers);
    public long TotalMachineBlocks => JobRequirements.Sum(requirement => requirement.MachineBlocks);
    public long TotalJobBlocks => TotalWorkers + TotalMachineBlocks;
}

public enum OptimizationMessageSeverity
{
    Information,
    Warning,
    Error
}

public sealed record OptimizationMessage(OptimizationMessageSeverity Severity, string Text);

public sealed class DemandBreakdown
{
    public string ItemId { get; set; } = string.Empty;
    public decimal PlayerPerCycle { get; set; }
    public decimal GuardPerCycle { get; set; }
    public decimal TrapPerCycle { get; set; }
    public decimal TotalPerCycle => PlayerPerCycle + GuardPerCycle + TrapPerCycle;
}

public sealed class JobRequirement
{
    public string JobTypeId { get; set; } = string.Empty;
    public string JobDisplayName { get; set; } = string.Empty;
    public long Workers { get; set; }
    public long MachineBlocks { get; set; }
    public bool IsAutomatedQueue { get; set; }
    public long BlockCount => IsAutomatedQueue ? MachineBlocks : Workers;
    public decimal WorkloadSeconds { get; set; }
    public decimal CapacitySeconds { get; set; }
    public decimal UtilisationPercent { get; set; }
    public string? SelectedToolId { get; set; }
    public string? SelectedToolDisplayName { get; set; }
}

public sealed class RecipeAllocation
{
    public string RecipeId { get; set; } = string.Empty;
    public string RecipeDisplayName { get; set; } = string.Empty;
    public string JobTypeId { get; set; } = string.Empty;
    public string UnitLabel { get; set; } = "Craft";
    public long CraftsPerCycle { get; set; }
    public decimal EffectiveCooldownSeconds { get; set; }
    public decimal WorkloadSeconds { get; set; }
    public string? SelectedToolId { get; set; }
    public bool IsAutomatedQueue { get; set; }
    public string? IconPath { get; set; }
    public string Mode => IsAutomatedQueue ? "Queued machine" : "Worker job";
}

public sealed class ExternalRequirement
{
    public string ItemId { get; set; } = string.Empty;
    public string ItemDisplayName { get; set; } = string.Empty;
    public decimal PerCycle { get; set; }
    public decimal PerMinute { get; set; }
    public bool IsAutomatic { get; set; }
    public string? IconPath { get; set; }
    public string DisplayName => ItemDisplayName;
    public string Source => IsAutomatic ? "No parsed producer" : "User supplied";
}

public sealed class ProductionOutput
{
    public string ItemId { get; set; } = string.Empty;
    public string ItemDisplayName { get; set; } = string.Empty;
    public decimal PerCycle { get; set; }
    public decimal PerMinute { get; set; }
    public string? IconPath { get; set; }
    public string DisplayName => ItemDisplayName;
}

public sealed class ProductionFlow
{
    public string SourceId { get; set; } = string.Empty;
    public string SourceLabel { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public string? SourceJobBlock { get; set; }
    public string TargetId { get; set; } = string.Empty;
    public string TargetLabel { get; set; } = string.Empty;
    public string TargetKind { get; set; } = string.Empty;
    public string? TargetJobBlock { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public sealed class ToolRequirement
{
    public string JobTypeId { get; set; } = string.Empty;
    public string JobDisplayName { get; set; } = string.Empty;
    public string ToolId { get; set; } = string.Empty;
    public string ToolDisplayName { get; set; } = string.Empty;
    public long Quantity { get; set; }
    public decimal CraftingSpeed { get; set; }
    public decimal Durability { get; set; }
    public decimal ReplacementPerCycle { get; set; }
    public decimal ReplacementPerMinute { get; set; }
}
