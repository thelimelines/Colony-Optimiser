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
