using System.IO.Compression;
using System.Text.Json;
using ColonyOptimizer.Core;

namespace ColonyOptimizer.GameData;

public sealed class GameDataAcquisition
{
    private const string CommitUrl = "https://api.github.com/repos/pipliz/ColonySurvival/commits/master";
    private const string RepositoryZipUrlFormat = "https://github.com/pipliz/ColonySurvival/archive/{0}.zip";
    private readonly HttpMessageHandler? _httpMessageHandler;

    public GameDataAcquisition(string? cacheRoot = null, HttpMessageHandler? httpMessageHandler = null)
    {
        CacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ColonyOptimizer",
            "GameData");
        _httpMessageHandler = httpMessageHandler;
    }

    public string CacheRoot { get; }

    public IReadOnlyList<string> FindInstalledGameDataDirectories()
    {
        try
        {
            return FindGameDataDirectories(GetSteamRoots().Concat(GetReadyDriveRoots()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            DiagnosticLog.Write(exception, "discover-installed-game-data");
            return [];
        }
    }

    public IReadOnlyList<string> FindWorldSaveDatabases(IEnumerable<string>? driveRoots = null)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var gameDataPath in FindGameDataDirectories(GetSteamRoots().Concat(driveRoots ?? GetReadyDriveRoots())))
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

    /// <summary>
    /// Finds game-data folders below Steam roots without allowing an unreadable
    /// library file to prevent the user from choosing a folder manually.
    /// </summary>
    public IReadOnlyList<string> FindGameDataDirectories(IEnumerable<string> steamRoots)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(steamRoot))
                {
                    continue;
                }

                AddIfGameData(candidates, Path.Combine(steamRoot, "steamapps", "common", "Colony Survival", "gamedata"));
                var libraries = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                foreach (var library in TryReadSteamLibraryFolders(libraries))
                {
                    AddIfGameData(candidates, Path.Combine(library, "steamapps", "common", "Colony Survival", "gamedata"));
                }
            }
            catch (IOException)
            {
                // One inaccessible Steam root must not prevent the manual picker or other roots from being checked.
            }
            catch (UnauthorizedAccessException)
            {
                // One inaccessible Steam root must not prevent the manual picker or other roots from being checked.
            }
        }

        return candidates.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> GetSteamRoots()
    {
        var roots = new List<string>
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
                    roots.Add(steamPath);
                }
            }
        }
        catch (System.Security.SecurityException)
        {
            // The manual picker remains available when a registry policy blocks inspection.
        }
        catch (UnauthorizedAccessException)
        {
            // The manual picker remains available when a registry policy blocks inspection.
        }

        return roots;
    }

    private static IReadOnlyList<string> GetReadyDriveRoots()
    {
        var roots = new List<string>();
        try
        {
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
                catch (UnauthorizedAccessException)
                {
                    // A protected drive must not prevent the manual picker from being used.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            DiagnosticLog.Write(exception, "enumerate-drives-for-game-data");
        }

        return roots;
    }

    public async Task<DownloadedGameData> DownloadLatestAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(CacheRoot);
        var workPath = Path.Combine(CacheRoot, $"download-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(workPath, "source.zip");
        string? stagingPath = null;
        Directory.CreateDirectory(workPath);

        try
        {
            using var client = _httpMessageHandler is null ? new HttpClient() : new HttpClient(_httpMessageHandler, disposeHandler: false);
            var applicationVersion = typeof(GameDataAcquisition).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"ColonyOptimizer/{applicationVersion}");
            var commit = await GetLatestCommitAsync(client, cancellationToken);
            var repositoryZipUrl = string.Format(System.Globalization.CultureInfo.InvariantCulture, RepositoryZipUrlFormat, Uri.EscapeDataString(commit));
            await using (var source = await client.GetStreamAsync(repositoryZipUrl, cancellationToken))
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

            var destination = Path.Combine(CacheRoot, "GitHub");
            stagingPath = Path.Combine(CacheRoot, $"GitHub.staging-{Guid.NewGuid():N}");
            Directory.Move(gamedataSource, stagingPath);
            ReplaceCacheDirectory(stagingPath, destination);
            stagingPath = null;
            return new DownloadedGameData(destination, commit, DateTimeOffset.UtcNow);
        }
        finally
        {
            if (stagingPath is not null)
            {
                TryDeleteDirectory(stagingPath, "clean-up-game-data-staging");
            }
            TryDeleteDirectory(workPath, "clean-up-game-data-download");
        }
    }

    /// <summary>Removes a temporary directory without concealing a completed download or its original failure.</summary>
    public static bool TryDeleteDirectory(string path, string operation)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write(exception, operation);
            return false;
        }
    }

    private static async Task<string> GetLatestCommitAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var commitDocument = JsonDocument.Parse(await client.GetStringAsync(CommitUrl, cancellationToken));
        var commit = commitDocument.RootElement.TryGetProperty("sha", out var sha) ? sha.GetString() : null;
        return !string.IsNullOrWhiteSpace(commit)
            ? commit
            : throw new InvalidDataException("The GitHub response did not identify the downloaded game-data revision.");
    }

    private static void ReplaceCacheDirectory(string stagingPath, string destination)
    {
        string? previousPath = null;
        if (Directory.Exists(destination))
        {
            previousPath = $"{destination}.previous-{Guid.NewGuid():N}";
            Directory.Move(destination, previousPath);
        }

        try
        {
            Directory.Move(stagingPath, destination);
        }
        catch
        {
            if (previousPath is not null && !Directory.Exists(destination))
            {
                try
                {
                    Directory.Move(previousPath, destination);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Write(exception, "restore-game-data-cache");
                }
            }
            throw;
        }

        if (previousPath is not null)
        {
            TryDeleteDirectory(previousPath, "clean-up-previous-game-data-cache");
        }
    }

    private static IReadOnlyList<string> TryReadSteamLibraryFolders(string libraries)
    {
        try
        {
            return File.Exists(libraries) ? ParseSteamLibraryFolders(File.ReadAllText(libraries)).ToArray() : [];
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
