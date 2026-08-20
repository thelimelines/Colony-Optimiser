using System.Xml.Linq;

namespace ColonyOptimizer.Tests;

public sealed class MainWindowLayoutTests
{
    [Theory]
    [InlineData("Planner", "Auto", "*")]
    [InlineData("Defence", "Auto", "Auto", "*", "*")]
    [InlineData("Sources", "Auto", "*")]
    [InlineData("Visualisation", "Auto", "*")]
    public void keeps_tab_rows_in_their_intended_layout(string tabHeader, params string[] expectedHeights)
    {
        var repository = FindWorkspaceDirectory();
        var document = XDocument.Load(Path.Combine(repository, "src", "ColonyOptimizer.App", "MainWindow.xaml"));
        var presentation = (XNamespace)"http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var tab = document.Descendants(presentation + "TabItem")
            .Single(element => string.Equals((string?)element.Attribute("Header"), tabHeader, StringComparison.Ordinal));
        var grid = tab.Element(presentation + "Grid")!;
        var heights = grid.Element(presentation + "Grid.RowDefinitions")!
            .Elements(presentation + "RowDefinition")
            .Select(element => (string?)element.Attribute("Height"))
            .ToArray();

        Assert.Equal(expectedHeights, heights);
    }

    [Fact]
    public void renders_local_game_icons_and_keeps_only_custom_guard_shot_input()
    {
        var repository = FindWorkspaceDirectory();
        var xaml = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.App", "MainWindow.xaml"));

        Assert.Contains("IconPathToImageConverter", xaml, StringComparison.Ordinal);
        Assert.Contains("TrapNameTemplate", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Shots/cycle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Title=\"Colony Optimiser\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AppVersion.DisplayWithPrefix", xaml, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(repository, "src", "ColonyOptimizer.App", "Assets", "ColonyOptimizerLogo.png")));
        Assert.True(File.Exists(Path.Combine(repository, "src", "ColonyOptimizer.App", "Assets", "ColonyOptimizerLogo.ico")));
    }

    [Fact]
    public void visualisation_uses_elk_layered_layout_with_drag_and_spacing_controls()
    {
        var repository = FindWorkspaceDirectory();
        var source = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.App", "Assets", "Visualisation", "Sankey.html"));
        var xaml = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.App", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.App", "MainWindowViewModel.cs"));

        Assert.Contains("function enableNodeDrag", source, StringComparison.Ordinal);
        Assert.Contains("elk.bundled.js", source, StringComparison.Ordinal);
        Assert.Contains("new ELK()", source, StringComparison.Ordinal);
        Assert.Contains("'elk.algorithm': 'layered'", source, StringComparison.Ordinal);
        Assert.Contains("'elk.edgeRouting': 'POLYLINE'", source, StringComparison.Ordinal);
        Assert.Contains("'elk.spacing.nodeNode'", source, StringComparison.Ordinal);
        Assert.Contains("'elk.layered.spacing.nodeNodeBetweenLayers'", source, StringComparison.Ordinal);
        Assert.Contains("window.waitForGraph", source, StringComparison.Ordinal);
        Assert.Contains("return [sourcePoint, targetPoint];", source, StringComparison.Ordinal);
        Assert.DoesNotContain("manuallyPositioned", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'Processing'", source, StringComparison.Ordinal);
        Assert.Contains("function collapseItemNodes", source, StringComparison.Ordinal);
        Assert.Contains("retainItem(item.id, 'Output')", source, StringComparison.Ordinal);
        Assert.Contains("collapsedItemNodes", source, StringComparison.Ordinal);
        Assert.Contains("NodeSpacing", xaml, StringComparison.Ordinal);
        Assert.Contains("LayerSpacing", xaml, StringComparison.Ordinal);
        Assert.Contains("NodeLayoutDirection", xaml, StringComparison.Ordinal);
        Assert.Contains("jobBlocks", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void visual_smoke_test_writes_an_explicit_result_marker_when_requested()
    {
        var repository = FindWorkspaceDirectory();
        var source = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.App", "MainWindow.xaml.cs"));

        Assert.Contains("COLONY_OPTIMIZER_SMOKE_RESULT_PATH", source, StringComparison.Ordinal);
        Assert.Contains("WriteVisualisationSmokeResult(succeeded)", source, StringComparison.Ordinal);
        Assert.Contains("MainTabs.SelectedIndex = 6", source, StringComparison.Ordinal);
        Assert.Contains("Environment.SpecialFolder.LocalApplicationData", source, StringComparison.Ordinal);
        Assert.Contains("MicrosoftEdgeWebView2RuntimeInstallerX64.exe", source, StringComparison.Ordinal);
    }

    [Fact]
    public void publishable_source_does_not_contain_machine_specific_personal_paths()
    {
        var repository = FindWorkspaceDirectory();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".md", ".props", ".ps1", ".slnx", ".svg", ".wixproj", ".wxs", ".xaml", ".yml", ".yaml"
        };
        var windowsUserPath = string.Concat("C:", '\\', "Users", '\\');
        var forwardSlashUserPath = "C:/" + "Users/";
        var userName = string.Concat("bro", "di");

        var files = Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => extensions.Contains(Path.GetExtension(path)));

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain(windowsUserPath, source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forwardSlashUserPath, source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(userName, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void release_pipeline_produces_installer_and_portable_assets()
    {
        var repository = FindWorkspaceDirectory();
        var readme = File.ReadAllText(Path.Combine(repository, "README.md"));
        var script = File.ReadAllText(Path.Combine(repository, "scripts", "Publish-Release.ps1"));
        var workflow = File.ReadAllText(Path.Combine(repository, ".github", "workflows", "release.yml"));
        var bundle = File.ReadAllText(Path.Combine(repository, "installer", "ColonyOptimizer.Setup", "Bundle.wxs"));

        Assert.Contains("-Setup.exe", readme, StringComparison.Ordinal);
        Assert.Contains("-win-x64.msi", readme, StringComparison.Ordinal);
        Assert.Contains("ColonyOptimizer.Installer.wixproj", script, StringComparison.Ordinal);
        Assert.Contains("ColonyOptimizer.Setup.wixproj", script, StringComparison.Ordinal);
        Assert.Contains("Get-WebView2RuntimeInstaller", script, StringComparison.Ordinal);
        Assert.Contains("MicrosoftEdgeWebView2RuntimeInstallerX64.exe", script, StringComparison.Ordinal);
        Assert.Contains("WebView2RuntimeInstallerPath", bundle, StringComparison.Ordinal);
        Assert.Contains("DetectCondition=\"WebView2RuntimeMachineVersion OR WebView2RuntimeUserVersion\"", bundle, StringComparison.Ordinal);
        Assert.Contains("artifacts\\*", workflow, StringComparison.Ordinal);
    }

    private static string FindWorkspaceDirectory()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "ColonyOptimizer.App", "MainWindow.xaml")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("The workspace MainWindow.xaml was not found.");
    }
}
