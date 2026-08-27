using System.IO.Compression;
using System.Text.Json;

namespace ColonyOptimizer.GameData;

public sealed class GameDataAcquisition
{
    private const string RepositoryZipUrl = "https://github.com/pipliz/ColonySurvival/archive/refs/heads/master.zip";
    private const string CommitUrl = "https://api.github.com/repos/pipliz/ColonySurvival/commits/master";

    public string CacheRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ColonyOptimizer",
        "GameData");

    public IReadOnlyList<string> FindInstalledGameDataDirectories() => FindGameDataDirectories(GetReadyDriveRoots());

    public IReadOnlyList<string> FindWorldSaveDatabases(IEnumerable<string>? driveRoots = null)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gameDataPath in FindGameDataDirectories(driveRoots ?? GetReadyDriveRoots()))
        {
            var saveGamesPath = Path.Combine(gameDataPath, "savegames");
            if (!Directory.Exists(saveGamesPath))
            {
                continue;
            }

            try
            {
                foreach (var worldDatabase in Directory.EnumerateFiles(saveGamesPath, "world.sqlite3", SearchOption.AllDirectories))
                {
                    candidates.Add(worldDatabase);
                }
            }
            catch (IOException)
            {
                // A partially synchronised cloud save must not prevent other Steam libraries being discovered.
            }
            catch (UnauthorizedAccessException)
            {
                // The manual picker remains available when a save folder cannot be read.
            }
        }

        return candidates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IReadOnlyList<string> FindGameDataDirectories(IEnumerable<string> driveRoots)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamRoots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
        };

        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var steamPath = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(steamPath))
                {
                    steamRoots.Add(steamPath);
                }
            }
        }
        catch (System.Security.SecurityException)
        {
            // The manual picker remains available when a registry policy blocks inspection.
        }

        foreach (var steamRoot in steamRoots.Concat(driveRoots).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            AddIfGameData(candidates, Path.Combine(steamRoot, "steamapps", "common", "Colony Survival", "gamedata"));
            var libraries = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraries))
            {
                continue;
            }

            foreach (var library in ParseSteamLibraryFolders(File.ReadAllText(libraries)))
            {
                AddIfGameData(candidates, Path.Combine(library, "steamapps", "common", "Colony Survival", "gamedata"));
            }
        }

        return candidates.OrderBy(path => path).ToArray();
    }

    private static IReadOnlyList<string> GetReadyDriveRoots()
    {
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    roots.Add(drive.RootDirectory.FullName);
                }
            }
            catch (IOException)
            {
                // Removable drives can disappear while the discovery scan is in progress.
            }
        }

        return roots;
    }

    public async Task<DownloadedGameData> DownloadLatestAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(CacheRoot);
        var workPath = Path.Combine(CacheRoot, $"download-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(workPath, "source.zip");
        Directory.CreateDirectory(workPath);

        try
        {
            using var client = new HttpClient();
            var applicationVersion = typeof(GameDataAcquisition).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"ColonyOptimizer/{applicationVersion}");
            await using (var source = await client.GetStreamAsync(RepositoryZipUrl, cancellationToken))
            await using (var target = File.Create(zipPath))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            var extractPath = Path.Combine(workPath, "extract");
            ZipFile.ExtractToDirectory(zipPath, extractPath);
            var repositoryRoot = Directory.EnumerateDirectories(extractPath).SingleOrDefault()
                ?? throw new InvalidDataException("The GitHub download did not contain a repository directory.");
            var gamedataSource = Path.Combine(repositoryRoot, "gamedata");
            _ = GameDataLoader.ResolveGameDataPath(gamedataSource);

            string? commit = null;
            try
            {
                using var commitDocument = JsonDocument.Parse(await client.GetStringAsync(CommitUrl, cancellationToken));
                commit = commitDocument.RootElement.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
            }
            catch (HttpRequestException)
            {
                // The downloaded data is usable even when commit metadata is unavailable.
            }

            var destination = Path.Combine(CacheRoot, "GitHub");
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            Directory.Move(gamedataSource, destination);
            return new DownloadedGameData(destination, commit, DateTimeOffset.UtcNow);
        }
        finally
        {
            if (Directory.Exists(workPath))
            {
                Directory.Delete(workPath, recursive: true);
            }
        }
    }

    private static IEnumerable<string> ParseSteamLibraryFolders(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains("\"path\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var segments = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var path = segments.SkipWhile(segment => !segment.Equals("path", StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(path))
            {
                yield return path.Replace("\\\\", "\\");
            }
        }
    }

    private static void AddIfGameData(ISet<string> destinations, string path)
    {
        if (File.Exists(Path.Combine(path, "baseconfig", "modInfo.json")))
        {
            destinations.Add(path);
        }
    }
}

public sealed record DownloadedGameData(string GameDataPath, string? Commit, DateTimeOffset DownloadedAt);
