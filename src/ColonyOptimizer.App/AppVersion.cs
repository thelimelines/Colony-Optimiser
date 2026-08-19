namespace ColonyOptimizer.App;

public static class AppVersion
{
    private static readonly Version? Version = typeof(AppVersion).Assembly.GetName().Version;

    public static string Display => Version?.ToString(3) ?? "Unknown";
    public static string DisplayWithPrefix => $"v{Display}";
}
