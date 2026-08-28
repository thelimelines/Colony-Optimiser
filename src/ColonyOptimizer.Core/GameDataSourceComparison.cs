namespace ColonyOptimizer.Core;

/// <summary>Compares plan provenance without treating a local file path as a data-version change.</summary>
public static class GameDataSourceComparison
{
    public static string? GetDifferenceWarning(GameDataSourceInfo? planSource, GameDataSourceInfo? activeSource)
    {
        if (planSource is null || activeSource is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(planSource.Commit) && !string.IsNullOrWhiteSpace(activeSource.Commit)
            && !planSource.Commit.Equals(activeSource.Commit, StringComparison.OrdinalIgnoreCase))
        {
            return "This plan was created using different Colony Survival data. Results may differ.";
        }

        if (!string.IsNullOrWhiteSpace(planSource.Version) && !string.IsNullOrWhiteSpace(activeSource.Version)
            && !planSource.Version.Equals(activeSource.Version, StringComparison.OrdinalIgnoreCase))
        {
            return "This plan was created using different Colony Survival data. Results may differ.";
        }

        if (!planSource.SourceType.Equals(activeSource.SourceType, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(planSource.Version) || string.IsNullOrWhiteSpace(activeSource.Version))
            && (string.IsNullOrWhiteSpace(planSource.Commit) || string.IsNullOrWhiteSpace(activeSource.Commit)))
        {
            return "This plan was created using a different game-data source. Results may differ.";
        }

        return null;
    }
}
