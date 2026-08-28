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
        var iconConverter = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.App", "IconPathToImageConverter.cs"));
        Assert.Contains("DecodePixelWidth", iconConverter, StringComparison.Ordinal);
        Assert.Contains("ClearCache", iconConverter, StringComparison.Ordinal);
        Assert.Contains("TrapNameTemplate", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Shots/cycle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Title=\"Colony Optimiser\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AppVersion.DisplayWithPrefix", xaml, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(repository, "src", "ColonyOptimizer.App", "Assets", "ColonyOptimizerLogo.png")));
        Assert.True(File.Exists(Path.Combine(repository, "src", "ColonyOptimizer.App", "Assets", "ColonyOptimizerLogo.ico")));
    }

    [Fact]
    public void offers_a_colony_group_dropdown_for_linked_multiplayer_saves()
    {
        var repository = FindWorkspaceDirectory();
        var xaml = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.App", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.App", "MainWindowViewModel.cs"));

        Assert.Contains("ItemsSource=\"{Binding ColonyGroupOptions}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedColonyGroup}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("All colony groups (combined", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void visualisation_uses_elk_layered_layout_with_drag_and_spacing_controls()
    {
        var repository = FindWorkspaceDirectory();
        var source = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.App", "Assets", "Visualisation", "Sankey.html"));
        var xaml = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.App", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.App", "MainWindowViewModel.cs"));
        var acquisition = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.GameData", "GameDataAcquisition.cs"));

        Assert.Contains("function enableNodeDrag", source, StringComparison.Ordinal);
        Assert.Contains("function layoutSankey", source, StringComparison.Ordinal);
        Assert.Contains("nodesPerColumn", source, StringComparison.Ordinal);
        Assert.Contains("requiredHeight", source, StringComparison.Ordinal);
        Assert.Contains("data.nodes.length > 150 ? 8", source, StringComparison.Ordinal);
        Assert.Contains("data.nodes.length > 60 ? 12 : 24", source, StringComparison.Ordinal);
        Assert.Contains("let renderGeneration = 0", source, StringComparison.Ordinal);
        Assert.Contains("generation !== renderGeneration", source, StringComparison.Ordinal);
        Assert.Contains("requestAnimationFrame", source, StringComparison.Ordinal);
        Assert.Contains("incidentLinks", source, StringComparison.Ordinal);
        Assert.Contains("const incoming = new Map()", source, StringComparison.Ordinal);
        Assert.Contains("const outgoing = new Map()", source, StringComparison.Ordinal);
        Assert.Contains("window.setLayoutOptions", source, StringComparison.Ordinal);
        Assert.Contains("function resetGraphView", source, StringComparison.Ordinal);
        Assert.Contains("mode !== graphState.mode", source, StringComparison.Ordinal);
        Assert.Contains("window.resetGraphView = resetGraphView", source, StringComparison.Ordinal);
        Assert.Contains("renderSankey(collapsedGraph", source, StringComparison.Ordinal);
        Assert.Contains("function layoutSankeyLabels", source, StringComparison.Ordinal);
        Assert.DoesNotContain("mix-blend-mode: screen", source, StringComparison.Ordinal);
        Assert.Contains("elk.bundled.js", source, StringComparison.Ordinal);
        Assert.Contains("function createElk", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<script src=\"elk.bundled.js\"></script>", source, StringComparison.Ordinal);
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
        Assert.Contains("tooltip.append('strong').text", source, StringComparison.Ordinal);
        Assert.DoesNotContain("tooltip.html(", source, StringComparison.Ordinal);
        Assert.Contains("NodeSpacing", xaml, StringComparison.Ordinal);
        Assert.Contains("LayerSpacing", xaml, StringComparison.Ordinal);
        Assert.Contains("NodeLayoutDirection", xaml, StringComparison.Ordinal);
        Assert.Contains("Minimum=\"0\" Maximum=\"160\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Minimum=\"0\" Maximum=\"240\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsIndeterminate=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ResetVisualisationView_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("DebounceVisualisationLayoutUpdate", viewModel, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", viewModel, StringComparison.Ordinal);
        Assert.Contains("VisualisationLayoutJson", viewModel, StringComparison.Ordinal);
        Assert.Contains("isVisualisationRendering", viewModel, StringComparison.Ordinal);
        Assert.Contains("_isLoadingVisualisationSettings", viewModel, StringComparison.Ordinal);
        Assert.Contains("SaveVisualisationSettings();", viewModel, StringComparison.Ordinal);
        Assert.Contains("typeof(GameDataAcquisition).Assembly.GetName().Version", acquisition, StringComparison.Ordinal);
        Assert.DoesNotContain("ColonyOptimizer/0.1.0", acquisition, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(_userSettings.NodeSpacing ?? NodeSpacing, 0, 160)", viewModel, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(_userSettings.LayerSpacing ?? LayerSpacing, 0, 240)", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("VisualGraphNodes", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("VisualGraphLinks", viewModel, StringComparison.Ordinal);
        Assert.Contains("jobBlocks", viewModel, StringComparison.Ordinal);
        Assert.Contains("CropFarmLayouts", viewModel, StringComparison.Ordinal);
        Assert.Contains("TrapRows.ToList().ForEach(row => row.Count = 0)", viewModel, StringComparison.Ordinal);
        Assert.Contains("New blank plan", viewModel, StringComparison.Ordinal);
        Assert.Contains("AtomicTextFile.WriteAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("Targets.Count == 0 && GuardRows.All(row => row.Count == 0) && TrapRows.All(row => row.Count == 0)", viewModel, StringComparison.Ordinal);
        Assert.Contains("result.IsOptimal", viewModel, StringComparison.Ordinal);
        Assert.Contains("RecipeRowsView", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.VirtualizationMode", xaml, StringComparison.Ordinal);
        Assert.Contains("BulkObservableCollection", viewModel, StringComparison.Ordinal);
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
        Assert.Contains("MicrosoftEdgeWebview2Setup.exe", source, StringComparison.Ordinal);
        Assert.Contains("ResetVisualisationView_Click", source, StringComparison.Ordinal);
        Assert.Contains("WebMessageReceived", source, StringComparison.Ordinal);
        Assert.Contains("UpdateVisualisationLayoutAsync", source, StringComparison.Ordinal);
        Assert.Contains("MainTabs_SelectionChanged", source, StringComparison.Ordinal);
        Assert.Contains("EnsureVisualisationWebViewAsync", source, StringComparison.Ordinal);
        Assert.Contains("QueueVisualisationRender", source, StringComparison.Ordinal);
        Assert.Contains("TaskCompletionSource<bool>", source, StringComparison.Ordinal);
        Assert.Contains("renderCompletion.Task.WaitAsync", source, StringComparison.Ordinal);
        Assert.Contains("_viewModel.NodeSpacing = 0", source, StringComparison.Ordinal);
        Assert.Contains("_viewModel.LayerSpacing = 0", source, StringComparison.Ordinal);
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
        var package = File.ReadAllText(Path.Combine(repository, "installer", "ColonyOptimizer.Installer", "Package.wxs"));
        var theme = File.ReadAllText(Path.Combine(repository, "installer", "ColonyOptimizer.Setup", "Theme.xml"));
        var license = File.ReadAllText(Path.Combine(repository, "installer", "ColonyOptimizer.Setup", "License.rtf"));
        var planCleanup = File.ReadAllText(Path.Combine(repository, "installer", "ColonyOptimizer.Installer", "RemoveRecordedColonyPlans.ps1"));
        var releaseSmoke = File.ReadAllText(Path.Combine(repository, "scripts", "Test-ReleasePackages.ps1"));
        var releaseScript = File.ReadAllText(Path.Combine(repository, "scripts", "Publish-Release.ps1"));
        var planModels = File.ReadAllText(Path.Combine(repository, "src", "ColonyOptimizer.Core", "PlanModels.cs"));
        var releasing = File.ReadAllText(Path.Combine(repository, "docs", "RELEASING.md"));
        var changelog = File.ReadAllText(Path.Combine(repository, "CHANGELOG.md"));

        Assert.Contains("-Setup.exe", readme, StringComparison.Ordinal);
        Assert.Contains("-win-x64.msi", readme, StringComparison.Ordinal);
        Assert.Contains("ColonyOptimizer.Installer.wixproj", script, StringComparison.Ordinal);
        Assert.Contains("ColonyOptimizer.Setup.wixproj", script, StringComparison.Ordinal);
        Assert.Contains("Get-WebView2Bootstrapper", script, StringComparison.Ordinal);
        Assert.Contains("MicrosoftEdgeWebview2Setup.exe", script, StringComparison.Ordinal);
        Assert.Contains("LinkId=2124703", script, StringComparison.Ordinal);
        Assert.DoesNotContain("LinkId=2124701", script, StringComparison.Ordinal);
        Assert.DoesNotContain("<ExePackage", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("WebView2RuntimeInstallerPath", bundle, StringComparison.Ordinal);
        Assert.Contains("IconSourceFile", bundle, StringComparison.Ordinal);
        Assert.Contains("LogoFile", bundle, StringComparison.Ordinal);
        Assert.Contains("<Payload SourceFile=\"$(var.BrandIconPath)\" Name=\"ColonyOptimizerLogo.ico\" />", bundle, StringComparison.Ordinal);
        Assert.Contains("LicenseFile=\"License.rtf\"", bundle, StringComparison.Ordinal);
        Assert.Contains("SuppressOptionsUI=\"yes\"", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("MsiProperty Name=\"InstallFolder\"", bundle, StringComparison.Ordinal);
        Assert.Contains("RemoveColonyPlanFiles", bundle, StringComparison.Ordinal);
        Assert.Contains("MsiProperty Name=\"REMOVE_COLONY_PLAN_FILES\"", bundle, StringComparison.Ordinal);
        Assert.Contains("StandardDirectory Id=\"ProgramFiles64Folder\"", package, StringComparison.Ordinal);
        Assert.Contains("EulaRichedit", theme, StringComparison.Ordinal);
        Assert.Contains("IconFile=", theme, StringComparison.Ordinal);
        Assert.Contains("ColonyOptimizerLogo.ico", theme, StringComparison.Ordinal);
        Assert.DoesNotContain("..\\..\\src\\ColonyOptimizer.App", theme, StringComparison.Ordinal);
        Assert.Contains("Remove saved Colony Optimiser plan files", theme, StringComparison.Ordinal);
        Assert.Contains("MIT License", license, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted", license, StringComparison.Ordinal);
        Assert.Contains("a copy of this software", license, StringComparison.Ordinal);
        Assert.Contains("EXPRESS OR IMPLIED", license, StringComparison.Ordinal);
        Assert.DoesNotContain("a copy\\par", license, StringComparison.Ordinal);
        Assert.DoesNotContain("EXPRESS OR\\par", license, StringComparison.Ordinal);
        Assert.Contains("ARPPRODUCTICON", package, StringComparison.Ordinal);
        Assert.Contains("RemoveRecordedColonyPlans", package, StringComparison.Ordinal);
        Assert.Contains("REMOVE_COLONY_PLAN_FILES=1", package, StringComparison.Ordinal);
        Assert.Contains(".colonyplan", planCleanup, StringComparison.Ordinal);
        Assert.Contains("RecentPlans", planCleanup, StringComparison.Ordinal);
        Assert.Contains("artifacts\\*", workflow, StringComparison.Ordinal);
        Assert.Contains("git merge-base --is-ancestor", workflow, StringComparison.Ordinal);
        Assert.Contains("origin/main", workflow, StringComparison.Ordinal);
        Assert.Contains("Test-ReleasePackages.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("Test-SetupUserInterface", releaseSmoke, StringComparison.Ordinal);
        Assert.Contains("Test-SetupLayout", releaseSmoke, StringComparison.Ordinal);
        Assert.Contains("Test-MsiAdministrativeInstall", releaseSmoke, StringComparison.Ordinal);
        Assert.Contains("Expand-Archive", releaseSmoke, StringComparison.Ordinal);
        Assert.Contains("Get-PublishedRuntimePackPath", releaseScript, StringComparison.Ordinal);
        Assert.Contains("ColonyOptimizer.deps.json", releaseScript, StringComparison.Ordinal);
        Assert.Contains("runtimepack.$PackageId/", releaseScript, StringComparison.Ordinal);
        Assert.Contains("-t:Rebuild", releaseScript, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ChildItem -LiteralPath $runtimePackRoot", releaseScript, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentFormatVersion", planModels, StringComparison.Ordinal);
        Assert.Contains("CHANGELOG.md", releasing, StringComparison.Ordinal);
        Assert.Contains("Test-ReleasePackages.ps1", releasing, StringComparison.Ordinal);
        Assert.Contains("CHANGELOG.md", workflow, StringComparison.Ordinal);
        Assert.Contains("--notes-file", workflow, StringComparison.Ordinal);
        Assert.Contains("## Unreleased", changelog, StringComparison.Ordinal);
        Assert.Contains("## [1.0.5]", changelog, StringComparison.Ordinal);
        Assert.Contains("## [1.0.6] - 2026-08-28", changelog, StringComparison.Ordinal);
        Assert.Contains("$headingPattern", workflow, StringComparison.Ordinal);
        Assert.Contains("<Version>1.1.0</Version>", File.ReadAllText(Path.Combine(repository, "Directory.Build.props")), StringComparison.Ordinal);
        Assert.Contains("last-opened or recent plans", theme, StringComparison.Ordinal);
        Assert.Contains("last-opened plan or in the app's Recent plans list", File.ReadAllText(Path.Combine(repository, "README.md")), StringComparison.Ordinal);
        Assert.Contains("no parsed non-player producer", File.ReadAllText(Path.Combine(repository, "docs", "SOLVER_MODEL.md")), StringComparison.Ordinal);

        var icon = File.ReadAllBytes(Path.Combine(repository, "src", "ColonyOptimizer.App", "Assets", "ColonyOptimizerLogo.ico"));
        var imageCount = BitConverter.ToUInt16(icon, 4);
        var imageWidths = Enumerable.Range(0, imageCount)
            .Select(index => icon[6 + index * 16] == 0 ? 256 : icon[6 + index * 16])
            .ToArray();
        Assert.Contains(16, imageWidths);
        Assert.Contains(32, imageWidths);
        Assert.Contains(48, imageWidths);
        Assert.Contains(256, imageWidths);
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
