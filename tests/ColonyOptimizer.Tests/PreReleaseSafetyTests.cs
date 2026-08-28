using System.IO.Compression;
using System.Net;
using System.Text;
using ColonyOptimizer.App;
using ColonyOptimizer.GameData;

namespace ColonyOptimizer.Tests;

public sealed class PreReleaseSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ColonyOptimizer", "pre-release-safety-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task atomic_write_keeps_the_existing_plan_when_the_destination_is_locked()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "existing.colonyplan");
        File.WriteAllText(path, "original plan");

        using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var exception = await Record.ExceptionAsync(() => AtomicTextFile.WriteAsync(path, "replacement plan"));
        Assert.True(exception is IOException or UnauthorizedAccessException);

        lockHandle.Dispose();
        Assert.Equal("original plan", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task atomic_write_reports_an_unusable_destination_directory()
    {
        Directory.CreateDirectory(_root);
        var unusableDirectory = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(unusableDirectory, "file");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => AtomicTextFile.WriteAsync(Path.Combine(unusableDirectory, "plan.colonyplan"), "content"));
    }

    [Fact]
    public void new_plan_resets_progression_and_solver_settings_to_blank_defaults()
    {
        var viewModel = new MainWindowViewModel
        {
            EfficiencyPercent = 73m,
            HeadroomPercent = 15m,
            SelectedObjective = ColonyOptimizer.Core.OptimizationObjective.LowestRawResourceConsumption,
            SelectedStochasticPolicy = ColonyOptimizer.Core.StochasticOutputPolicy.Conservative,
            SelectedPlanName = "Configured plan"
        };
        viewModel.Targets.Add(new DemandRow("wheat", "Wheat", 15m, ColonyOptimizer.Core.DemandUnit.PerMinute));
        viewModel.ExternalItems.Add(new ExternalItemRow("logs", "Logs"));
        viewModel.ScienceRows.Add(new SelectableEntry("science", "Science", true));
        viewModel.ToolRows.Add(new SelectableEntry("tools", "Tools", true));

        viewModel.NewPlanCommand.Execute(null);

        Assert.Empty(viewModel.Targets);
        Assert.Empty(viewModel.ExternalItems);
        Assert.All(viewModel.ScienceRows, row => Assert.False(row.IsSelected));
        Assert.All(viewModel.ToolRows, row => Assert.False(row.IsSelected));
        Assert.Equal(100m, viewModel.EfficiencyPercent);
        Assert.Equal(0m, viewModel.HeadroomPercent);
        Assert.Equal(ColonyOptimizer.Core.OptimizationObjective.FewestWorkers, viewModel.SelectedObjective);
        Assert.Equal(ColonyOptimizer.Core.StochasticOutputPolicy.ExpectedValue, viewModel.SelectedStochasticPolicy);
        Assert.Equal("Untitled plan", viewModel.SelectedPlanName);
    }

    [Fact]
    public void game_data_discovery_skips_a_locked_library_file_and_keeps_other_candidates()
    {
        var steamRoot = Path.Combine(_root, "Steam");
        var gameData = Path.Combine(steamRoot, "steamapps", "common", "Colony Survival", "gamedata");
        Directory.CreateDirectory(Path.Combine(gameData, "baseconfig"));
        File.WriteAllText(Path.Combine(gameData, "baseconfig", "modInfo.json"), "[]");
        var libraryFolders = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        File.WriteAllText(libraryFolders, "\"libraryfolders\"\n{");

        using var lockHandle = new FileStream(libraryFolders, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var discovered = new GameDataAcquisition().FindGameDataDirectories([steamRoot]);

        Assert.Equal([gameData], discovered);
    }

    [Fact]
    public void locked_temporary_game_data_is_left_for_later_cleanup()
    {
        Directory.CreateDirectory(_root);
        var temporaryFile = Path.Combine(_root, "source.zip");
        File.WriteAllText(temporaryFile, "temporary content");

        using var lockHandle = new FileStream(temporaryFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(GameDataAcquisition.TryDeleteDirectory(_root, "test-clean-up"));

        lockHandle.Dispose();
        Assert.True(GameDataAcquisition.TryDeleteDirectory(_root, "test-clean-up"));
    }

    [Fact]
    public async Task downloaded_data_uses_the_resolved_commit_and_replaces_a_validated_cache()
    {
        var cacheRoot = Path.Combine(_root, "cache");
        var existingCache = Path.Combine(cacheRoot, "GitHub");
        Directory.CreateDirectory(existingCache);
        File.WriteAllText(Path.Combine(existingCache, "previous.txt"), "keep until replacement is ready");
        using var handler = new CommitPinnedDownloadHandler();
        var acquisition = new GameDataAcquisition(cacheRoot, handler);

        var downloaded = await acquisition.DownloadLatestAsync();

        Assert.Equal(CommitPinnedDownloadHandler.Commit, downloaded.Commit);
        Assert.Equal(Path.Combine(cacheRoot, "GitHub"), downloaded.GameDataPath);
        Assert.True(File.Exists(Path.Combine(downloaded.GameDataPath, "baseconfig", "modInfo.json")));
        Assert.False(File.Exists(Path.Combine(downloaded.GameDataPath, "previous.txt")));
        Assert.Equal(
            [
                "https://api.github.com/repos/pipliz/ColonySurvival/commits/master",
                $"https://github.com/pipliz/ColonySurvival/archive/{CommitPinnedDownloadHandler.Commit}.zip"
            ],
            handler.RequestUris);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            GameDataAcquisition.TryDeleteDirectory(_root, "clean-up-test-directory");
        }
    }

    private sealed class CommitPinnedDownloadHandler : HttpMessageHandler
    {
        public const string Commit = "0123456789abcdef0123456789abcdef01234567";
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.AbsoluteUri.EndsWith("/commits/master", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"sha\":\"{Commit}\"}}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(CreateRepositoryZip())
            });
        }

        private static byte[] CreateRepositoryZip()
        {
            using var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry($"ColonySurvival-{Commit}/gamedata/baseconfig/modInfo.json");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("[]");
            }

            return stream.ToArray();
        }
    }
}
