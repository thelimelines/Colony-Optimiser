using System.Text.Json;

namespace ColonyOptimizer.Core;

/// <summary>Best-effort diagnostic logging shared by the desktop and game-data layers.</summary>
public static class DiagnosticLog
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
