using System.Text.Json;
using System.IO;

namespace ColonyOptimizer.App;

public static class AppRuntime
{
    public static bool IsVisualSmokeTest => Environment.GetEnvironmentVariable("COLONY_OPTIMIZER_VISUAL_SMOKE_TEST") == "1"
        || Environment.GetCommandLineArgs().Any(argument => argument.Equals("--visual-smoke", StringComparison.OrdinalIgnoreCase));

    public static string VisualSmokeRoot => Path.Combine(Path.GetTempPath(), "ColonyOptimizer", "visual-smoke");
}

public sealed class UserSettings
{
    public string? LastGameDataDirectory { get; set; }
    public string? LinkedSaveGamePath { get; set; }
    public string? LastWorldSaveDirectory { get; set; }
    public bool HasCompletedInitialWorldDiscovery { get; set; }
    public string? LastPlanPath { get; set; }
    public string? LastPlanDirectory { get; set; }
    public string? NodeLayoutDirection { get; set; }
    public int? NodeSpacing { get; set; }
    public int? LayerSpacing { get; set; }
    public List<string> RecentPlans { get; set; } = [];

    public void AddRecentPlan(string path)
    {
        RecentPlans.RemoveAll(candidate => candidate.Equals(path, StringComparison.OrdinalIgnoreCase));
        RecentPlans.Insert(0, path);
        if (RecentPlans.Count > 10)
        {
            RecentPlans.RemoveRange(10, RecentPlans.Count - 10);
        }
    }
}

public sealed class UserSettingsStore
{
    private static readonly string Root = AppRuntime.IsVisualSmokeTest
        ? AppRuntime.VisualSmokeRoot
        : Environment.GetEnvironmentVariable("COLONY_OPTIMIZER_SETTINGS_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ColonyOptimizer");
    private static readonly string PathName = Path.Combine(Root, "settings.json");

    public UserSettings Load()
    {
        try
        {
            return File.Exists(PathName) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(PathName), JsonDefaults.Options) ?? new UserSettings() : new UserSettings();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            FileLogger.Write(exception, "load-settings");
            return new UserSettings();
        }
    }

    public void Save(UserSettings settings)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(Root);
            temporaryPath = $"{PathName}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonDefaults.Options));
            File.Move(temporaryPath, PathName, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            FileLogger.Write(exception, "save-settings");
        }
        finally
        {
            try
            {
                if (temporaryPath is not null && File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                FileLogger.Write(exception, "clean-up-settings");
            }
        }
    }
}

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

public static class FileLogger
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ColonyOptimizer", "Logs");

    public static void Write(Exception exception, string operation)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, $"colony-optimizer-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            var entry = JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.UtcNow, Operation = operation, Exception = exception.GetType().FullName, exception.Message, exception.StackTrace });
            File.AppendAllText(path, entry + Environment.NewLine);
            foreach (var oldFile in Directory.EnumerateFiles(Root, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Skip(10))
            {
                File.Delete(oldFile);
            }
        }
        catch (Exception)
        {
            // Logging must never prevent the user from seeing the original error.
        }
    }
}
