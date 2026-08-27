using System.Text.Json;
using ColonyOptimizer.Core;

namespace ColonyOptimizer.GameData;

public sealed class GameDataLoader
{
    private static readonly Lazy<IReadOnlyList<string>> InstalledIconDirectories = new(FindInstalledIconDirectories);
    private static readonly HashSet<string> RecipeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "cooldown", "requires", "results", "requiresScience", "defaultLimit", "defaultPriority", "sortWeight"
    };

    public GameDatabase Load(string selectedPath, CancellationToken cancellationToken = default)
    {
        var gamedataPath = ResolveGameDataPath(selectedPath);
        var baseConfigPath = Path.Combine(gamedataPath, "baseconfig");
        var manifestPath = Path.Combine(baseConfigPath, "modInfo.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"No compatible gamedata/baseconfig/modInfo.json was found under '{selectedPath}'.");
        }

        var database = new GameDatabase
        {
            Source = new GameDataSourceInfo("Local directory", gamedataPath, Version: TryReadCompatibleVersion(manifestPath))
        };

        LoadTiming(database, Path.Combine(gamedataPath, "settings", "server.json"));
        var manifest = ReadJson(manifestPath);
        try
        {
            var root = manifest.RootElement;
            var module = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (!module.TryGetProperty("jsonFiles", out var registeredFiles) || registeredFiles.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("modInfo.json does not contain a jsonFiles array.");
            }

            var entries = registeredFiles.EnumerateArray().Select((entry, ordinal) => new ManifestEntry(
                GetString(entry, "fileType"),
                GetString(entry, "relativePath"),
                GetString(entry, "npcType"),
                GetInt(entry, "index", 0),
                ordinal)).OrderBy(entry => entry.Index).ThenBy(entry => entry.Ordinal).ToArray();

            var jobBlocks = new Dictionary<string, JobBlockInfo>(StringComparer.OrdinalIgnoreCase);
            var growables = new Dictionary<string, GrowableInfo>(StringComparer.OrdinalIgnoreCase);
            var simpleFarmPatches = new List<SimpleFarmPatch>();
            foreach (var entry in entries.Where(entry => entry.FileType.Equals("generateBlocks", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                LoadGeneratedBlocks(database, ResolveManifestPath(baseConfigPath, entry.RelativePath), entry.RelativePath, jobBlocks);
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolveManifestPath(baseConfigPath, entry.RelativePath);
                switch (entry.FileType)
                {
                    case "addNewTypes":
                        LoadTypes(database, path, entry.RelativePath);
                        break;
                    case "setToolsets":
                        LoadToolsets(database, path, entry.RelativePath);
                        break;
                    case "addScience":
                        LoadSciences(database, path, entry.RelativePath);
                        break;
                    case "addOrReplaceNPCRecipes":
                        LoadRecipes(database, path, entry.RelativePath, entry.NpcType);
                        break;
                    case "addOrReplacePlayerRecipes":
                        // Inventory crafting is manually initiated by the player, so it must not
                        // appear as a production worker, source, or data-quality warning.
                        break;
                    case "addOrOverrideGrowableTypes":
                        LoadGrowables(path, entry.RelativePath, growables, database.Diagnostics);
                        break;
                    case "addOrOverrideAreaJobs":
                        LoadSimpleFarmPatches(path, entry.RelativePath, simpleFarmPatches, database.Diagnostics);
                        break;
                }
            }

            BuildCropFarmSources(database, simpleFarmPatches, growables);
            BuildForestrySources(database);
            BuildMiningRecipes(database, jobBlocks);
            BuildJobs(database, jobBlocks);
            ResolveReferences(database);
            return database;
        }
        finally
        {
            manifest.Dispose();
        }
    }

    public static string ResolveGameDataPath(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            throw new ArgumentException("A game-data directory is required.", nameof(selectedPath));
        }

        var fullPath = Path.GetFullPath(selectedPath);
        var candidates = new[]
        {
            fullPath,
            Path.Combine(fullPath, "gamedata"),
            Path.Combine(fullPath, "Colony Survival", "gamedata")
        };

        var gamedataPath = candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "baseconfig", "modInfo.json")));
        return gamedataPath ?? throw new InvalidDataException("The selected folder is not a Colony Survival gamedata directory.");
    }

    private static void LoadTiming(GameDatabase database, string path)
    {
        if (!File.Exists(path))
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, "server.json is missing. Default timing values are being used.", path);
            return;
        }

        using var document = ReadJson(path);
        if (!document.RootElement.TryGetProperty("Time", out var time))
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, "server.json does not contain a Time section. Default timing values are being used.", path);
            return;
        }

        var defaults = GameTiming.Default;
        database.Timing = new GameTiming(
            GetDecimal(time, "GameTimeScale", defaults.GameTimeScale),
            GetDecimal(time, "DayTimeStart", defaults.DayTimeStart),
            GetDecimal(time, "DayTimeEnd", defaults.DayTimeEnd),
            GetDecimal(time, "GuardShiftDayStart", defaults.GuardShiftDayStart),
            GetDecimal(time, "GuardShiftDayEnd", defaults.GuardShiftDayEnd),
            GetDecimal(time, "GuardShiftNightStart", defaults.GuardShiftNightStart),
            GetDecimal(time, "GuardShiftNightEnd", defaults.GuardShiftNightEnd),
            GetDecimal(time, "SleepTimeStart", defaults.SleepTimeStart),
            GetDecimal(time, "SleepTimeEnd", defaults.SleepTimeEnd));
    }

    private static void LoadTypes(GameDatabase database, string path, string sourcePath)
    {
        if (!File.Exists(path))
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, "Registered item-type file is missing.", sourcePath);
            return;
        }

        using var document = ReadJson(path);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, "Item-type file is not an object and was skipped.", sourcePath);
            return;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var definition = property.Value;
            var item = database.GetOrAddItem(property.Name);
            item.IsResolved = true;
            item.DisplayName = DisplayName.FromIdentifier(property.Name);
            item.IconPath = ResolveIconPath(path, GetString(definition, "icon"), property.Name);
            item.Category = TryReadFirstArrayString(definition, "categories");
            LoadOnRemoveOutputs(database, item, definition, sourcePath);
            LoadMiningSource(database, property.Name, item.DisplayName, definition, sourcePath);

            if (!definition.TryGetProperty("attachBehaviour", out var behaviours) || behaviours.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var behaviour in behaviours.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
            {
                if (!GetString(behaviour, "id").Equals("tool", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Upsert(database.Tools, tool => tool.Id, property.Name, () => new ToolDefinition
                {
                    Id = property.Name,
                    DisplayName = item.DisplayName,
                    CraftingSpeed = GetDecimal(behaviour, "craftingspeed", 1m),
                    Durability = GetDecimal(behaviour, "durability", 0m),
                    RequiresStockpileItem = GetBoolean(behaviour, "requiresItem", true),
                    RequiredScience = NullIfEmpty(GetString(behaviour, "scienceHint"))
                });
            }
        }
    }

    private static void LoadMiningSource(GameDatabase database, string typeId, string displayName, JsonElement definition, string sourcePath)
    {
        if (!definition.TryGetProperty("customData", out var customData) || customData.ValueKind != JsonValueKind.Object || !GetBoolean(customData, "minerIsMineable", false))
        {
            return;
        }

        var outputItemId = NormalizeMaterialId(GetString(definition, "onRemoveType"));
        var miningTimeSeconds = GetDecimal(customData, "minerMiningTime", 0m);
        if (string.IsNullOrWhiteSpace(outputItemId) || miningTimeSeconds <= 0m)
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, $"Mineable type '{typeId}' is missing onRemoveType or minerMiningTime and cannot be modelled as miner output.", sourcePath);
            return;
        }

        database.GetOrAddItem(outputItemId);
        Upsert(database.MiningSources, source => source.Id, typeId, () => new MiningSourceDefinition
        {
            Id = typeId,
            DisplayName = displayName,
            OutputItemId = outputItemId,
            MiningTimeSeconds = miningTimeSeconds,
            SourceFile = sourcePath
        });
    }

    private static void LoadOnRemoveOutputs(GameDatabase database, ItemDefinition item, JsonElement definition, string sourcePath)
    {
        item.OnRemoveOutputs.Clear();
        if (definition.TryGetProperty("onRemove", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var output in outputs.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
            {
                var itemId = NormalizeMaterialId(GetString(output, "type"));
                if (string.IsNullOrWhiteSpace(itemId))
                {
                    database.Diagnostics.Add(DiagnosticLevel.Warning, $"Item type '{item.Id}' has an onRemove entry without an item type.", sourcePath);
                    continue;
                }

                database.GetOrAddItem(itemId);
                var chance = GetDecimal(output, "chance", 1m);
                item.OnRemoveOutputs.Add(new ItemAmount(itemId, GetDecimal(output, "amount", 1m), chance, chance < 1m));
            }
        }

        if (item.OnRemoveOutputs.Count > 0)
        {
            return;
        }

        var fallbackType = NormalizeMaterialId(GetString(definition, "onRemoveType"));
        if (!string.IsNullOrWhiteSpace(fallbackType))
        {
            database.GetOrAddItem(fallbackType);
            item.OnRemoveOutputs.Add(new ItemAmount(fallbackType, GetDecimal(definition, "onRemoveAmount", 1m)));
        }
    }

    private static void LoadToolsets(GameDatabase database, string path, string sourcePath)
    {
        if (!File.Exists(path))
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, "Registered toolset file is missing.", sourcePath);
            return;
        }

        using var document = ReadJson(path);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, "Toolset file is not an array and was skipped.", sourcePath);
            return;
        }

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var id = GetString(entry, "key");
            if (string.IsNullOrEmpty(id))
            {
                database.Diagnostics.Add(DiagnosticLevel.Warning, "Toolset without a key was skipped.", sourcePath);
                continue;
            }

            Upsert(database.Toolsets, toolset => toolset.Id, id, () =>
            {
                var toolset = new ToolsetDefinition { Id = id, UseMultiplier = GetDecimal(entry, "useMultiplier", 1m) };
                if (entry.TryGetProperty("usable", out var usable) && usable.ValueKind == JsonValueKind.Array)
                {
                    toolset.UsableTools.AddRange(usable.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!));
                }

                return toolset;
            });
        }
    }

    private static void LoadSciences(GameDatabase database, string path, string sourcePath)
    {
        if (!File.Exists(path))
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, "Registered science file is missing.", sourcePath);
            return;
        }

        using var document = ReadJson(path);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, "Science file is not an array and was skipped.", sourcePath);
            return;
        }

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var id = GetString(entry, "key");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            Upsert(database.Sciences, science => science.Id, id, () =>
            {
                var science = new ScienceDefinition { Id = id, DisplayName = DisplayName.FromIdentifier(id) };
                if (entry.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Array)
                {
                    science.Dependencies.AddRange(dependencies.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!));
                }

                if (entry.TryGetProperty("unlocksClientHints", out var hints) && hints.ValueKind == JsonValueKind.Array)
                {
                    science.UnlockedNpcTypeIds.AddRange(hints.EnumerateArray()
                        .Where(hint => hint.ValueKind == JsonValueKind.Object && GetString(hint, "type").Equals("NPCType", StringComparison.OrdinalIgnoreCase))
                        .Select(hint => GetString(hint, "data"))
                        .Where(id => !string.IsNullOrWhiteSpace(id)));
                }

                return science;
            });
        }
    }

    private static void LoadGrowables(string path, string sourcePath, IDictionary<string, GrowableInfo> growables, GameDataDiagnostics diagnostics)
    {
        if (!File.Exists(path))
        {
            diagnostics.Add(DiagnosticLevel.Warning, "Registered growable-types file is missing.", sourcePath);
            return;
        }

        using var document = ReadJson(path);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(DiagnosticLevel.Warning, "Growable-types file is not an array and was skipped.", sourcePath);
            return;
        }

        foreach (var entry in document.RootElement.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
        {
            var id = GetString(entry, "identifier");
            if (string.IsNullOrWhiteSpace(id) || !entry.TryGetProperty("stages", out var stages) || stages.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var parsedStages = stages.EnumerateArray()
                .Where(stage => stage.ValueKind == JsonValueKind.Object)
                .Select(stage => new GrowableStageInfo(GetString(stage, "type"), GetDecimal(stage, "growthTime", 10m)))
                .Where(stage => !string.IsNullOrWhiteSpace(stage.TypeId))
                .ToArray();
            if (parsedStages.Length >= 2)
            {
                growables[id] = new GrowableInfo(GetString(entry, "growthType"), parsedStages, sourcePath);
            }
        }
    }

    private static void LoadSimpleFarmPatches(string path, string sourcePath, ICollection<SimpleFarmPatch> farms, GameDataDiagnostics diagnostics)
    {
        if (!File.Exists(path))
        {
            diagnostics.Add(DiagnosticLevel.Warning, "Registered area-jobs file is missing.", sourcePath);
            return;
        }

        using var document = ReadJson(path);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(DiagnosticLevel.Warning, "Area-jobs file is not an array and was skipped.", sourcePath);
            return;
        }

        foreach (var entry in document.RootElement.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
        {
            if (!GetString(entry, "patchType").Equals("simpleFarm", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = GetString(entry, "identifier");
            var npcType = GetString(entry, "npcType");
            var stages = entry.TryGetProperty("stages", out var stageArray) && stageArray.ValueKind == JsonValueKind.Array
                ? stageArray.EnumerateArray().Where(stage => stage.ValueKind == JsonValueKind.String).Select(stage => stage.GetString()!).ToArray()
                : [];
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(npcType) || stages.Length < 2)
            {
                diagnostics.Add(DiagnosticLevel.Warning, "Simple farm without an identifier, NPC type, or at least two stages was skipped.", sourcePath);
                continue;
            }

            farms.Add(new SimpleFarmPatch(id, npcType, stages, GetInt(entry, "fertileRequirement", 0), sourcePath));
        }
    }

    private static void LoadRecipes(GameDatabase database, string path, string sourcePath, string jobTypeId)
    {
        if (!File.Exists(path))
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, "Registered recipe file is missing.", sourcePath);
            return;
        }

        using var document = ReadJson(path);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, "Recipe file is not an array and was skipped.", sourcePath);
            return;
        }

        var index = 0;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            index++;
            if (entry.ValueKind != JsonValueKind.Object)
            {
                database.Diagnostics.Add(DiagnosticLevel.Warning, $"Recipe {index} is not an object and was skipped.", sourcePath);
                continue;
            }

            var id = GetString(entry, "name");
            if (string.IsNullOrEmpty(id))
            {
                database.Diagnostics.Add(DiagnosticLevel.Warning, $"Recipe {index} has no name and was skipped.", sourcePath);
                continue;
            }

            var recipe = new RecipeDefinition
            {
                Id = id,
                DisplayName = DisplayName.FromIdentifier(id),
                JobTypeId = jobTypeId,
                CooldownSeconds = GetDecimal(entry, "cooldown", 0m),
                RequiredScience = NullIfEmpty(GetString(entry, "requiresScience")),
                SortWeight = GetInt(entry, "sortWeight", 0),
                SourceFile = sourcePath,
                SourceIndex = index
            };

            LoadAmounts(database, recipe.Inputs, entry, "requires", sourcePath, id);
            LoadAmounts(database, recipe.Outputs, entry, "results", sourcePath, id);
            if (recipe.CooldownSeconds <= 0m || recipe.Outputs.Count == 0)
            {
                database.Diagnostics.Add(DiagnosticLevel.Warning, $"Recipe '{id}' has no positive cooldown or no outputs and was excluded from optimisation.", sourcePath);
                continue;
            }

            foreach (var property in entry.EnumerateObject().Where(property => !RecipeFields.Contains(property.Name)))
            {
                database.Diagnostics.Add(DiagnosticLevel.Information, $"Recipe '{id}' contains unsupported field '{property.Name}', which was retained only in diagnostics.", sourcePath);
            }

            var existingIndex = database.Recipes.FindIndex(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                database.Recipes[existingIndex] = recipe;
            }
            else
            {
                database.Recipes.Add(recipe);
            }
        }
    }

    private static void LoadAmounts(GameDatabase database, List<ItemAmount> destination, JsonElement recipe, string field, string sourcePath, string recipeId)
    {
        if (!recipe.TryGetProperty(field, out var values))
        {
            return;
        }

        if (values.ValueKind != JsonValueKind.Array)
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, $"Recipe '{recipeId}' has a non-array '{field}' field.", sourcePath);
            return;
        }

        foreach (var entry in values.EnumerateArray())
        {
            var itemId = NormalizeMaterialId(GetString(entry, "type"));
            if (string.IsNullOrEmpty(itemId))
            {
                database.Diagnostics.Add(DiagnosticLevel.Warning, $"Recipe '{recipeId}' has a {field} entry with no item type.", sourcePath);
                continue;
            }

            database.GetOrAddItem(itemId);
            destination.Add(new ItemAmount(
                itemId,
                GetDecimal(entry, "amount", 1m),
                GetDecimal(entry, "chance", 1m),
                GetBoolean(entry, "isOptional", false)));
        }
    }

    private static void LoadGeneratedBlocks(GameDatabase database, string path, string sourcePath, Dictionary<string, JobBlockInfo> jobBlocks)
    {
        if (!File.Exists(path))
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, "Registered generated-block file is missing.", sourcePath);
            return;
        }

        using var document = ReadJson(path);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in document.RootElement.EnumerateArray())
        {
            LoadTrap(database, entry, path, sourcePath);
            var typeName = GetString(entry, "typeName");
            if (string.IsNullOrEmpty(typeName) || !entry.TryGetProperty("baseType", out var baseType) || baseType.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var item = database.GetOrAddItem(typeName);
            item.IsResolved = true;
            item.DisplayName = DisplayName.FromIdentifier(typeName);
            item.IconPath = ResolveIconPath(path, GetString(baseType, "icon"), typeName);
            item.Category = TryReadFirstArrayString(baseType, "categories");

            if (!baseType.TryGetProperty("attachBehaviour", out var behaviours) || behaviours.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var behaviour in behaviours.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
            {
                var behaviourId = GetString(behaviour, "id");
                if (behaviourId.Equals("guard", StringComparison.OrdinalIgnoreCase))
                {
                    LoadGuard(database, typeName, behaviour, sourcePath);
                    continue;
                }

                if (behaviourId.Equals("minerjob", StringComparison.OrdinalIgnoreCase))
                {
                    var minerNpcType = NullIfEmpty(GetString(behaviour, "npcType")) ?? "pipliz.minerjob";
                    jobBlocks[minerNpcType] = new JobBlockInfo(typeName, NullIfEmpty(GetString(behaviour, "toolset")) ?? "default");
                    continue;
                }

                var isAutomatedQueue = behaviourId.Equals("autocrafter", StringComparison.OrdinalIgnoreCase);
                if (!isAutomatedQueue && !behaviourId.Equals("craftingblock", StringComparison.OrdinalIgnoreCase) && !behaviourId.Equals("craftingblockresearcher", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Both the unique Autocrafter and repeatable machines such as the
                // Oddity Press use the autocrafter behaviour.  Its allowMultiple
                // setting distinguishes the single shared queue from machines the
                // colony can build more than once.
                var isSingleBlock = isAutomatedQueue && !GetBoolean(behaviour, "allowMultiple", true);

                var npcType = GetString(behaviour, "npcType");
                if (string.IsNullOrEmpty(npcType))
                {
                    npcType = GetString(behaviour, "recipeNPCType");
                }

                if (!string.IsNullOrEmpty(npcType))
                {
                    // Manual crafting blocks use the game's ordinary tool selection when they omit toolset.
                    // Queued machines do not employ a worker and therefore do not consume a tool.
                    jobBlocks[npcType] = new JobBlockInfo(typeName, isAutomatedQueue ? null : NullIfEmpty(GetString(behaviour, "toolset")) ?? "default", isAutomatedQueue, isSingleBlock);
                }
            }
        }
    }

    private static void BuildCropFarmSources(GameDatabase database, IEnumerable<SimpleFarmPatch> farms, IReadOnlyDictionary<string, GrowableInfo> growables)
    {
        foreach (var farm in farms)
        {
            var growable = growables.Values.FirstOrDefault(candidate => candidate.Stages.Select(stage => stage.TypeId).SequenceEqual(farm.Stages, StringComparer.OrdinalIgnoreCase));
            if (growable is null)
            {
                database.Diagnostics.Add(DiagnosticLevel.Warning, $"Simple farm '{farm.Id}' has no matching growable definition and cannot be modelled.", farm.SourceFile);
                continue;
            }

            var growthCycles = GetGrowthCyclesPerHarvest(growable, farm.Stages.Count);
            if (growthCycles <= 0m)
            {
                database.Diagnostics.Add(DiagnosticLevel.Warning, $"Simple farm '{farm.Id}' has unsupported growth type '{growable.GrowthType}' and cannot be modelled.", farm.SourceFile);
                continue;
            }

            var finalStage = database.Items.FirstOrDefault(item => item.Id.Equals(farm.Stages[^1], StringComparison.OrdinalIgnoreCase));
            if (finalStage is null || finalStage.OnRemoveOutputs.Count == 0)
            {
                database.Diagnostics.Add(DiagnosticLevel.Warning, $"Simple farm '{farm.Id}' has no harvest outputs on final stage '{farm.Stages[^1]}' and cannot be modelled.", farm.SourceFile);
                continue;
            }

            var primaryOutput = finalStage.OnRemoveOutputs[0];
            var requiredScience = database.Sciences.FirstOrDefault(science => science.UnlockedNpcTypeIds.Contains(farm.NpcType, StringComparer.OrdinalIgnoreCase))?.Id;
            var source = new CropFarmSourceDefinition
            {
                Id = farm.Id,
                DisplayName = $"{DisplayName.FromIdentifier(primaryOutput.ItemId)} Farm",
                JobTypeId = farm.NpcType,
                GrowthType = growable.GrowthType,
                StageCount = farm.Stages.Count,
                GrowthCyclesPerHarvest = growthCycles,
                HarvestActionSecondsPerTile = 1.5m,
                FertilityRequirement = farm.FertilityRequirement,
                RequiredScience = requiredScience,
                SourceFile = farm.SourceFile
            };
            source.Outputs.AddRange(finalStage.OnRemoveOutputs);
            Upsert(database.CropFarmSources, candidate => candidate.Id, source.Id, () => source);
        }
    }

    private static decimal GetGrowthCyclesPerHarvest(GrowableInfo growable, int stageCount)
    {
        if (growable.GrowthType.Equals("FirstNightRandom", StringComparison.OrdinalIgnoreCase) || growable.GrowthType.Equals("FirstDayRandom", StringComparison.OrdinalIgnoreCase))
        {
            return stageCount - 1;
        }

        if (growable.GrowthType.Equals("Always", StringComparison.OrdinalIgnoreCase))
        {
            return growable.Stages.Take(stageCount - 1).Sum(stage => stage.GrowthTimeHours) / 24m;
        }

        return 0m;
    }

    private static void BuildForestrySources(GameDatabase database)
    {
        const string foresterJobId = "pipliz.forester";
        var requiredScience = database.Sciences.FirstOrDefault(science => science.UnlockedNpcTypeIds.Contains(foresterJobId, StringComparer.OrdinalIgnoreCase))?.Id;
        var logItem = database.GetOrAddItem("logs");
        logItem.DisplayName = "Logs";
        logItem.IconPath ??= database.Items.FirstOrDefault(item => item.Id.Equals("logtemperate", StringComparison.OrdinalIgnoreCase) || item.Id.Equals("logtaiga", StringComparison.OrdinalIgnoreCase))?.IconPath;
        var leavesItem = database.GetOrAddItem("leaves");
        leavesItem.DisplayName = "Leaves";
        leavesItem.IconPath ??= database.Items.FirstOrDefault(item => item.Id.Equals("leavestemperate", StringComparison.OrdinalIgnoreCase) || item.Id.Equals("leavestaiga", StringComparison.OrdinalIgnoreCase))?.IconPath;

        if (!database.Items.Any(item => item.Id.Equals("logs", StringComparison.OrdinalIgnoreCase)) || !database.Items.Any(item => item.Id.Equals("leaves", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var source = new ForestrySourceDefinition
        {
            Id = "forestry",
            DisplayName = "Forestry",
            JobTypeId = foresterJobId,
            LogItemId = "logs",
            LeavesItemId = "leaves",
            RequiredScience = requiredScience,
            SourceFile = "Server SimulationGraph forester constants"
        };
        Upsert(database.ForestrySources, candidate => candidate.Id, source.Id, () => source);
    }

    private static void BuildMiningRecipes(GameDatabase database, IReadOnlyDictionary<string, JobBlockInfo> jobBlocks)
    {
        if (database.MiningSources.Count == 0)
        {
            return;
        }

        const string minerJobId = "pipliz.minerjob";
        jobBlocks.TryGetValue(minerJobId, out var minerBlock);

        var minerScience = database.Sciences.Any(science => science.Id.Equals("pipliz.miner", StringComparison.OrdinalIgnoreCase))
            ? "pipliz.miner"
            : null;
        foreach (var source in database.MiningSources)
        {
            // A miner block is attached to one core resource.  Keep each output
            // resource in its own capacity group so the result can report the
            // required miners for that resource without duplicating shared totals.
            var resourceJobId = $"{minerJobId}.{source.OutputItemId}";
            Upsert(database.Jobs, job => job.Id, resourceJobId, () => new JobTypeDefinition
            {
                Id = resourceJobId,
                DisplayName = $"Miner ({DisplayName.FromIdentifier(source.OutputItemId)})",
                JobBlockId = minerBlock?.BlockTypeId,
                ToolsetId = minerBlock?.ToolsetId
            });

            var recipe = new RecipeDefinition
            {
                Id = $"pipliz.minerjob.{source.Id}",
                DisplayName = $"Mine {DisplayName.FromIdentifier(source.OutputItemId)} ({source.DisplayName})",
                JobTypeId = resourceJobId,
                CooldownSeconds = source.MiningTimeSeconds,
                RequiredScience = minerScience,
                SourceFile = source.SourceFile
            };
            recipe.Outputs.Add(new ItemAmount(source.OutputItemId, 1m));
            Upsert(database.Recipes, candidate => candidate.Id, recipe.Id, () => recipe);
        }
    }

    private static void LoadGuard(GameDatabase database, string blockTypeId, JsonElement behaviour, string sourcePath)
    {
        var npcType = GetString(behaviour, "npcType");
        var sleepType = GetString(behaviour, "sleepType");
        if (string.IsNullOrEmpty(npcType) || string.IsNullOrEmpty(sleepType))
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, $"Guard block '{blockTypeId}' does not declare npcType and sleepType.", sourcePath);
            return;
        }

        var shift = sleepType.Equals("Night", StringComparison.OrdinalIgnoreCase) ? GuardShift.Day : GuardShift.Night;
        if (!sleepType.Equals("Night", StringComparison.OrdinalIgnoreCase) && !sleepType.Equals("Day", StringComparison.OrdinalIgnoreCase))
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, $"Guard '{npcType}' uses unknown sleepType '{sleepType}', treated as night shift.", sourcePath);
        }

        var guard = new GuardTypeDefinition
        {
            Id = npcType,
            DisplayName = DisplayName.FromIdentifier(npcType),
            NpcTypeId = npcType,
            Shift = shift,
            CooldownShotSeconds = GetDecimal(behaviour, "cooldownShot", 0m),
            Damage = GetDecimal(behaviour, "damage", 0m),
            Range = GetDecimal(behaviour, "range", 0m),
            SourceFile = sourcePath
        };

        if (behaviour.TryGetProperty("shootRequirements", out var ammunition) && ammunition.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in ammunition.EnumerateArray())
            {
                var itemId = NormalizeMaterialId(GetString(entry, "type"));
                if (!string.IsNullOrEmpty(itemId))
                {
                    database.GetOrAddItem(itemId);
                    guard.Ammunition.Add(new ItemAmount(itemId, GetDecimal(entry, "amount", 1m)));
                }
            }
        }

        if (guard.CooldownShotSeconds <= 0m || guard.Ammunition.Count == 0)
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, $"Guard '{npcType}' has no positive firing cooldown or no ammunition and cannot create a demand.", sourcePath);
        }

        Upsert(database.Guards, candidate => candidate.Id, guard.Id, () => guard);
    }

    private static void LoadTrap(GameDatabase database, JsonElement entry, string sourceFilePath, string sourcePath)
    {
        if (!entry.TryGetProperty("trapConfig", out var configuration) || configuration.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var id = GetString(entry, "trapName");
        var ammunitionItemId = NormalizeMaterialId(GetString(configuration, "ammoType"));
        var capacity = GetInt(configuration, "ammoMax", 0);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ammunitionItemId) || capacity <= 0)
        {
            database.Diagnostics.Add(DiagnosticLevel.Warning, "Trap definition with no name, ammunition, or capacity was skipped.", sourcePath);
            return;
        }

        database.GetOrAddItem(ammunitionItemId);
        var iconPath = entry.TryGetProperty("typeEmpty", out var emptyType) && emptyType.ValueKind == JsonValueKind.Object
            ? ResolveIconPath(sourceFilePath, GetString(emptyType, "icon"), "trap")
            : null;
        Upsert(database.Traps, trap => trap.Id, id, () => new TrapDefinition
        {
            Id = id,
            DisplayName = DisplayName.FromIdentifier(id),
            IconPath = iconPath,
            AmmunitionItemId = ammunitionItemId,
            AmmunitionCapacity = capacity,
            ReloadSecondsPerAmmunition = GetDecimal(configuration, "reload", 0m),
            SourceFile = sourcePath
        });
    }

    private static string NormalizeMaterialId(string itemId) => itemId.ToLowerInvariant() switch
    {
        "logtemperate" or "logtaiga" => "logs",
        "leavestemperate" or "leavestaiga" => "leaves",
        _ => itemId
    };

    private static void BuildJobs(GameDatabase database, IReadOnlyDictionary<string, JobBlockInfo> jobBlocks)
    {
        foreach (var cropFarm in database.CropFarmSources)
        {
            Upsert(database.Jobs, job => job.Id, cropFarm.JobTypeId, () => new JobTypeDefinition
            {
                Id = cropFarm.JobTypeId,
                DisplayName = DisplayName.FromIdentifier(cropFarm.JobTypeId)
            });
        }

        foreach (var forestry in database.ForestrySources)
        {
            Upsert(database.Jobs, job => job.Id, forestry.JobTypeId, () => new JobTypeDefinition
            {
                Id = forestry.JobTypeId,
                DisplayName = "Forester"
            });
        }

        foreach (var recipeGroup in database.Recipes.GroupBy(recipe => recipe.JobTypeId, StringComparer.OrdinalIgnoreCase))
        {
            if (database.Jobs.Any(job => job.Id.Equals(recipeGroup.Key, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            jobBlocks.TryGetValue(recipeGroup.Key, out var block);
            database.Jobs.Add(new JobTypeDefinition
            {
                Id = recipeGroup.Key,
                DisplayName = DisplayName.FromIdentifier(recipeGroup.Key),
                JobBlockId = block?.BlockTypeId,
                ToolsetId = block?.ToolsetId,
                IsAutomatedQueue = block?.IsAutomatedQueue ?? false,
                IsSingleBlock = block?.IsSingleBlock ?? false
            });
        }
    }

    private static void ResolveReferences(GameDatabase database)
    {
        foreach (var recipe in database.Recipes)
        {
            var job = database.Jobs.FirstOrDefault(candidate => candidate.Id.Equals(recipe.JobTypeId, StringComparison.OrdinalIgnoreCase));
            recipe.RequiredToolset = job?.ToolsetId;
        }

        // Lua bootstrap types such as Bed3 and Architrave are referenced by recipes but do not
        // have an addNewTypes JSON entry. Resolve their installed icon by item ID as a final pass.
        var manifestPath = Path.Combine(database.Source.SourcePath, "baseconfig", "modInfo.json");
        foreach (var item in database.Items.Where(item => string.IsNullOrWhiteSpace(item.IconPath)))
        {
            item.IconPath = ResolveIconPath(manifestPath, string.Empty, item.Id);
        }
    }

    private static string? ResolveIconPath(string sourceFilePath, string iconPath, string? itemId = null)
    {
        var directory = Path.GetDirectoryName(sourceFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            var resolved = Path.GetFullPath(Path.Combine(directory, iconPath.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(resolved))
            {
                return resolved;
            }

            candidates.Add(Path.GetFileName(resolved));
        }

        if (!string.IsNullOrWhiteSpace(itemId))
        {
            candidates.Add($"{itemId}.png");
            var baseId = TrimPaintSuffix(itemId);
            if (!baseId.Equals(itemId, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add($"{baseId}.png");
            }
        }

        foreach (var iconDirectory in InstalledIconDirectories.Value)
        {
            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var fallback = Path.Combine(iconDirectory, candidate);
                if (File.Exists(fallback))
                {
                    return fallback;
                }
            }
        }

        return null;
    }

    private static string TrimPaintSuffix(string itemId)
    {
        foreach (var suffix in new[] { "red", "green", "blue", "white", "yellow", "black" })
        {
            if (itemId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return itemId[..^suffix.Length];
            }
        }

        return itemId;
    }

    private static IReadOnlyList<string> FindInstalledIconDirectories()
    {
        try
        {
            return new GameDataAcquisition().FindInstalledGameDataDirectories()
                .Select(path => Path.Combine(path, "textures", "icons"))
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ResolveManifestPath(string baseConfigPath, string relativePath) =>
        Path.GetFullPath(Path.Combine(baseConfigPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string? TryReadCompatibleVersion(string manifestPath)
    {
        using var document = ReadJson(manifestPath);
        var root = document.RootElement;
        var module = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
        return module.TryGetProperty("compatibleversions", out var versions) && versions.ValueKind == JsonValueKind.Array
            ? versions.EnumerateArray().FirstOrDefault().GetString()
            : null;
    }

    private static JsonDocument ReadJson(string path) => JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    });

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : string.Empty;

    private static int GetInt(JsonElement element, string propertyName, int defaultValue) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value) ? value : defaultValue;

    private static decimal GetDecimal(JsonElement element, string propertyName, decimal defaultValue) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var value) ? value : defaultValue;

    private static bool GetBoolean(JsonElement element, string propertyName, bool defaultValue) =>
        element.TryGetProperty(propertyName, out var property) && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False) ? property.GetBoolean() : defaultValue;

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? TryReadFirstArrayString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().FirstOrDefault(value => value.ValueKind == JsonValueKind.String).GetString()
            : null;

    private static void Upsert<T>(List<T> collection, Func<T, string> idSelector, string id, Func<T> create)
    {
        var index = collection.FindIndex(entry => idSelector(entry).Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            collection[index] = create();
        }
        else
        {
            collection.Add(create());
        }
    }

    private sealed record ManifestEntry(string FileType, string RelativePath, string NpcType, int Index, int Ordinal);
    private sealed record GrowableStageInfo(string TypeId, decimal GrowthTimeHours);
    private sealed record GrowableInfo(string GrowthType, IReadOnlyList<GrowableStageInfo> Stages, string SourceFile);
    private sealed record SimpleFarmPatch(string Id, string NpcType, IReadOnlyList<string> Stages, int FertilityRequirement, string SourceFile);
    private sealed record JobBlockInfo(string? BlockTypeId, string? ToolsetId, bool IsAutomatedQueue = false, bool IsSingleBlock = false);
}
