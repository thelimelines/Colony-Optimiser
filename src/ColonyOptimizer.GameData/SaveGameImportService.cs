using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace ColonyOptimizer.GameData;

public sealed class SaveGameImportService
{
    private static readonly object SqliteProviderLock = new();
    private static bool _sqliteProviderInitialised;

    public SaveGameImportResult Import(string worldDatabasePath, SaveGameColonyGroupSelection? colonyGroupSelection = null)
    {
        using var connection = OpenReadOnlyConnection(worldDatabasePath);

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

        var colonyGroups = ReadColonyGroups(connection);
        var selectedGroups = colonyGroupSelection is null
            ? colonyGroups
            : colonyGroups.Where(group => Matches(colonyGroupSelection, group)).ToArray();
        if (colonyGroupSelection is not null && selectedGroups.Count == 0)
        {
            throw new SelectedColonyGroupMissingException();
        }

        var completedIndexes = new HashSet<int>();
        var importedGroupCount = 0;
        foreach (var colonyGroup in selectedGroups)
        {
            if (TryReadCompletedScienceIndexes(colonyGroup.Json, completedIndexes))
            {
                importedGroupCount++;
                continue;
            }

            if (colonyGroupSelection is not null)
            {
                throw new SelectedColonyGroupUnreadableException();
            }
        }

        var unlocked = completedIndexes.Where(scienceByIndex.ContainsKey).Select(index => scienceByIndex[index])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SaveGameImportResult(worldDatabasePath, unlocked, completedIndexes.Count, scienceByIndex.Count, importedGroupCount, colonyGroupSelection?.StableIdentity);
    }

    public IReadOnlyList<SaveGameColonyGroup> GetColonyGroups(string worldDatabasePath)
    {
        using var connection = OpenReadOnlyConnection(worldDatabasePath);
        return ReadColonyGroups(connection)
            .Select(group =>
            {
                var completedIndexes = new HashSet<int>();
                var isReadable = TryReadCompletedScienceIndexes(group.Json, completedIndexes);
                return new SaveGameColonyGroup(group.RowId, group.StableIdentity, GetColonyGroupLabel(group.Json, group.RowId), completedIndexes.Count, isReadable);
            })
            .ToArray();
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

    private static bool TryReadCompletedScienceIndexes(string colonyJson, ISet<int> destination)
    {
        try
        {
            using var document = JsonDocument.Parse(colonyJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            if (!document.RootElement.TryGetProperty("science", out var science))
            {
                return true;
            }

            if (science.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!science.TryGetProperty("completed", out var completed))
            {
                return true;
            }

            if (completed.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var value in completed.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index))
                {
                    destination.Add(index);
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static SqliteConnection OpenReadOnlyConnection(string worldDatabasePath)
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
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static IReadOnlyList<StoredColonyGroup> ReadColonyGroups(SqliteConnection connection)
    {
        var groups = new List<StoredColonyGroup>();
        var identityColumns = ReadStableIdentityColumns(connection);
        using var command = connection.CreateCommand();
        if (identityColumns.Count == 0)
        {
            command.CommandText = "SELECT rowid, json FROM colonygroups WHERE json IS NOT NULL ORDER BY rowid";
            using var rowIdReader = command.ExecuteReader();
            while (rowIdReader.Read())
            {
                if (!rowIdReader.IsDBNull(0) && !rowIdReader.IsDBNull(1))
                {
                    groups.Add(new StoredColonyGroup(rowIdReader.GetInt64(0), null, rowIdReader.GetString(1)));
                }
            }

            return groups;
        }

        var identitySql = string.Join(", ", identityColumns.Select(column => QuoteIdentifier(column.Name)));
        var primaryKeyOrderSql = string.Join(", ", identityColumns.Where(column => column.IsPrimaryKey).Select(column => QuoteIdentifier(column.Name)));
        command.CommandText = $"SELECT json, {identitySql} FROM colonygroups WHERE json IS NOT NULL ORDER BY {primaryKeyOrderSql}";
        using var reader = command.ExecuteReader();
        var displayRowId = 0L;
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                groups.Add(new StoredColonyGroup(++displayRowId, CreateStableIdentity(identityColumns, reader, 1), reader.GetString(0)));
            }
        }

        return groups;
    }

    private static IReadOnlyList<IdentityColumn> ReadStableIdentityColumns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"colonygroups\")";
        using var reader = command.ExecuteReader();
        var columns = new List<IdentityColumn>();
        while (reader.Read())
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(5))
            {
                continue;
            }

            columns.Add(new IdentityColumn(reader.GetString(1), Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture)));
        }

        var primaryKeyColumns = columns.Where(column => column.IsPrimaryKey).OrderBy(column => column.Position).ToArray();
        if (primaryKeyColumns.Length == 0)
        {
            return [];
        }

        var creationDateColumn = columns.FirstOrDefault(column => !column.IsPrimaryKey && column.Name.Equals("creation_date", StringComparison.OrdinalIgnoreCase));
        return creationDateColumn is null ? primaryKeyColumns : [.. primaryKeyColumns, creationDateColumn];
    }

    private static string CreateStableIdentity(IReadOnlyList<IdentityColumn> columns, SqliteDataReader reader, int firstValueOrdinal)
    {
        var identity = new StringBuilder();
        for (var index = 0; index < columns.Count; index++)
        {
            AppendIdentityPart(identity, columns[index].Name);
            var value = reader.GetValue(firstValueOrdinal + index);
            AppendIdentityPart(identity, value switch
            {
                byte[] bytes => Convert.ToHexString(bytes),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()
            });
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
    }

    private static void AppendIdentityPart(StringBuilder destination, string? value)
    {
        var text = value ?? "<null>";
        destination.Append(text.Length).Append(':').Append(text).Append('|');
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static bool Matches(SaveGameColonyGroupSelection selection, StoredColonyGroup group) => selection.StableIdentity is not null
        ? selection.StableIdentity.Equals(group.StableIdentity, StringComparison.Ordinal)
        : selection.RowId is { } rowId && rowId == group.RowId;

    private static string GetColonyGroupLabel(string colonyJson, long rowId)
    {
        try
        {
            using var document = JsonDocument.Parse(colonyJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var propertyName in new[] { "name", "colonyName", "groupName", "ownerName", "playerName" })
                {
                    if (TryGetStringProperty(document.RootElement, propertyName, out var value))
                    {
                        return NormaliseLabel(value);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Fall back to the current database row identifier when a record's metadata is malformed.
        }

        return $"Colony group {rowId}";
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                value = property.Value.GetString()!;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string NormaliseLabel(string value)
    {
        var label = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return label.Length <= 48 ? label : $"{label[..45]}...";
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

    private sealed record IdentityColumn(string Name, int Position)
    {
        public bool IsPrimaryKey => Position > 0;
    }
    private sealed record StoredColonyGroup(long RowId, string? StableIdentity, string Json);
}

public sealed class SelectedColonyGroupUnreadableException : Exception
{
    public SelectedColonyGroupUnreadableException()
        : base("The selected colony group could not be read; existing progression was not changed.")
    {
    }
}

public sealed class SelectedColonyGroupMissingException : Exception
{
    public SelectedColonyGroupMissingException()
        : base("The selected colony group is no longer present in this save; existing progression was not changed.")
    {
    }
}

public sealed record SaveGameColonyGroup(long RowId, string? StableIdentity, string Label, int CompletedScienceIndexCount, bool IsReadable)
{
    public SaveGameColonyGroupSelection Selection => new(RowId, StableIdentity);
    public string DisplayName => IsReadable
        ? $"{Label} ({CompletedScienceIndexCount:N0} completed sciences)"
        : $"{Label} (unreadable JSON)";
    public override string ToString() => DisplayName;
}

public sealed record SaveGameColonyGroupSelection(long? RowId, string? StableIdentity);

public sealed record SaveGameImportResult(
    string WorldDatabasePath,
    IReadOnlySet<string> UnlockedScienceIds,
    int CompletedScienceIndexCount,
    int KnownScienceCount,
    int ImportedColonyGroupCount = 0,
    string? ImportedColonyGroupIdentity = null);
