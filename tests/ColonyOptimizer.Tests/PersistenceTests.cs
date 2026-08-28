using ColonyOptimizer.App;

namespace ColonyOptimizer.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ColonyOptimizer", "persistence-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void load_returns_default_settings_when_the_settings_file_is_locked()
    {
        Directory.CreateDirectory(_root);
        var settingsPath = Path.Combine(_root, "settings.json");
        File.WriteAllText(settingsPath, "{ \"LastPlanPath\": \"locked.colonyplan\" }");

        using var lockHandle = new FileStream(settingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var settings = new UserSettingsStore(_root).Load();

        Assert.Null(settings.LastPlanPath);
    }

    [Fact]
    public void save_ignores_an_unusable_settings_root()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        File.WriteAllText(_root, "not a directory");

        var exception = Record.Exception(() => new UserSettingsStore(_root).Save(new UserSettings
        {
            LastPlanPath = "plan.colonyplan"
        }));

        Assert.Null(exception);
    }

    [Fact]
    public void saves_a_stable_colony_group_identity_without_relying_on_a_sqlite_rowid()
    {
        var store = new UserSettingsStore(_root);
        store.Save(new UserSettings
        {
            LinkedSaveGamePath = "world.sqlite3",
            LinkedSaveColonyGroupIdentity = "A1B2C3",
            LinkedSaveColonyGroupRowId = null
        });

        var reopened = store.Load();

        Assert.Equal("world.sqlite3", reopened.LinkedSaveGamePath);
        Assert.Equal("A1B2C3", reopened.LinkedSaveColonyGroupIdentity);
        Assert.Null(reopened.LinkedSaveColonyGroupRowId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        else if (File.Exists(_root))
        {
            File.Delete(_root);
        }
    }
}
