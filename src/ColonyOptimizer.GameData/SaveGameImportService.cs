using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace ColonyOptimizer.GameData;

public sealed class SaveGameImportService
{
    private static readonly object SqliteProviderLock = new();
    private static bool _sqliteProviderInitialised;

    public SaveGameImportResult Import(string worldDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(worldDatabasePath) || !File.Exists(worldDatabasePath))
        {
            throw new FileNotFoundException("The selected Colony Survival world.sqlite3 file was not found.", worldDatabasePath);
        }

        EnsureSqliteProvider();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = worldDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var scienceByIndex = new Dictionary<int, string>();
        using (var mappingCommand = connection.CreateCommand())
        {
            mappingCommand.CommandText = "SELECT name, [index] FROM science_mapping";
            using var reader = mappingCommand.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                {
                    scienceByIndex[reader.GetInt32(1)] = reader.GetString(0);
                }
            }
        }

        var completedIndexes = new HashSet<int>();
        using (var coloniesCommand = connection.CreateCommand())
        {
            coloniesCommand.CommandText = "SELECT json FROM colonygroups WHERE json IS NOT NULL";
            using var reader = coloniesCommand.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    ReadCompletedScienceIndexes(reader.GetString(0), completedIndexes);
                }
            }
        }

        var unlocked = completedIndexes.Where(scienceByIndex.ContainsKey).Select(index => scienceByIndex[index])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SaveGameImportResult(worldDatabasePath, unlocked, completedIndexes.Count, scienceByIndex.Count);
    }

    public string? FindLastWorldDatabase(string gameDataPath)
    {
        var saveGamesPath = Path.Combine(gameDataPath, "savegames");
        var launchOptionsPath = Path.Combine(saveGamesPath, "last_launch_options.json");
        if (!File.Exists(launchOptionsPath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(launchOptionsPath));
        if (!document.RootElement.TryGetProperty("LoadOptions", out var loadOptions) || !loadOptions.TryGetProperty("WorldName", out var worldNameProperty))
        {
            return null;
        }

        var worldName = worldNameProperty.GetString();
        if (string.IsNullOrWhiteSpace(worldName))
        {
            return null;
        }

        var relativeWorldPath = worldName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var databasePath = Path.Combine(saveGamesPath, relativeWorldPath, "world.sqlite3");
        return File.Exists(databasePath) ? databasePath : null;
    }

    private static void ReadCompletedScienceIndexes(string colonyJson, ISet<int> destination)
    {
        try
        {
            using var document = JsonDocument.Parse(colonyJson);
            if (!document.RootElement.TryGetProperty("science", out var science) || !science.TryGetProperty("completed", out var completed) || completed.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var value in completed.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index))
                {
                    destination.Add(index);
                }
            }
        }
        catch (JsonException)
        {
            // A malformed colony record cannot safely contribute unlock information.
        }
    }

    private static void EnsureSqliteProvider()
    {
        lock (SqliteProviderLock)
        {
            if (_sqliteProviderInitialised)
            {
                return;
            }

            raw.SetProvider(new SQLite3Provider_winsqlite3());
            raw.FreezeProvider();
            _sqliteProviderInitialised = true;
        }
    }
}

public sealed record SaveGameImportResult(string WorldDatabasePath, IReadOnlySet<string> UnlockedScienceIds, int CompletedScienceIndexCount, int KnownScienceCount);
