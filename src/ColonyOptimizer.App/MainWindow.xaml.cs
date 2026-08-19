using System.Windows;
using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace ColonyOptimizer.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly bool _visualisationSmokeTest = AppRuntime.IsVisualSmokeTest;
    private bool _visualisationReady;
    private bool _visualisationSmokeTestStarted;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"Colony Optimiser {AppVersion.Display}";
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        if (_visualisationSmokeTest)
        {
            MainTabs.SelectedIndex = 6;
        }
        try
        {
            SankeyWebView.NavigationCompleted += SankeyWebViewOnNavigationCompleted;
            await SankeyWebView.EnsureCoreWebView2Async();
            SankeyWebView.Source = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Visualisation", "Sankey.html"));
        }
        catch (Exception exception)
        {
            FileLogger.Write(exception, "visualisation-webview");
        }
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.SankeyGraphJson))
        {
            _ = RenderVisualisationAsync();
        }
        else if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen))
        {
            // WebView2 is a native child window and otherwise renders above the WPF settings overlay.
            SankeyWebView.Visibility = _viewModel.IsSettingsOpen ? Visibility.Hidden : Visibility.Visible;
            if (!_viewModel.IsSettingsOpen)
            {
                _ = RenderVisualisationAsync();
            }
        }
    }

    private async void SankeyWebViewOnNavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        _visualisationReady = eventArgs.IsSuccess;
        await RenderVisualisationAsync();
        if (_visualisationSmokeTest && !_visualisationSmokeTestStarted)
        {
            _visualisationSmokeTestStarted = true;
            await RunVisualisationSmokeTestAsync();
        }
    }

    private async Task<bool> RenderVisualisationAsync()
    {
        if (!_visualisationReady || SankeyWebView.CoreWebView2 is null)
        {
            return false;
        }

        try
        {
            await SankeyWebView.CoreWebView2.ExecuteScriptAsync($"window.setGraph({_viewModel.SankeyGraphJson});");
            await SankeyWebView.CoreWebView2.ExecuteScriptAsync("window.waitForGraph ? window.waitForGraph() : Promise.resolve();");
            var response = await SankeyWebView.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.getGraphState ? window.getGraphState() : null);");
            var stateJson = JsonSerializer.Deserialize<string>(response);
            using var state = JsonDocument.Parse(stateJson ?? "null");
            var expectsElkLayout = _viewModel.SelectedVisualisationIndex == 1;
            var populated = state.RootElement.ValueKind == JsonValueKind.Object
                && state.RootElement.GetProperty("nodes").GetInt32() > 0
                && state.RootElement.GetProperty("links").GetInt32() > 0
                && state.RootElement.GetProperty("panEnabled").GetBoolean()
                && state.RootElement.GetProperty("wheelZoomEnabled").GetBoolean()
                && (!expectsElkLayout || state.RootElement.GetProperty("layoutEngine").GetString() == "elk-layered")
                && (!_visualisationSmokeTest || !expectsElkLayout || state.RootElement.GetProperty("collapsedItemNodes").GetInt32() > 0);
            if (_viewModel.HasVisualisationGraph && !populated)
            {
                throw new InvalidOperationException("The visualisation did not create the expected interactive graph.");
            }

            var navigationResponse = await SankeyWebView.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.verifyNavigation ? window.verifyNavigation() : null);");
            var navigationJson = JsonSerializer.Deserialize<string>(navigationResponse);
            using var navigation = JsonDocument.Parse(navigationJson ?? "null");
            var navigationVerified = navigation.RootElement.ValueKind == JsonValueKind.Object
                && navigation.RootElement.GetProperty("panWorked").GetBoolean()
                && navigation.RootElement.GetProperty("wheelWorked").GetBoolean();
            if (!navigationVerified)
            {
                throw new InvalidOperationException("The visualisation did not accept pan and wheel navigation input.");
            }

            return populated || !_viewModel.HasVisualisationGraph;
        }
        catch (Exception exception)
        {
            FileLogger.Write(exception, "visualisation-render");
            _viewModel.ReportVisualisationFailure();
            return false;
        }
    }

    private async Task RunVisualisationSmokeTestAsync()
    {
        var optimisationSucceeded = await _viewModel.RunVisualisationSmokeOptimisationAsync();
        _viewModel.SelectedVisualisationIndex = 0;
        var sankeySucceeded = await RenderVisualisationAsync();
        _viewModel.SelectedVisualisationIndex = 1;
        var nodeVisualiserSucceeded = await RenderVisualisationAsync();
        var succeeded = optimisationSucceeded && sankeySucceeded && nodeVisualiserSucceeded;
        WriteVisualisationSmokeResult(succeeded);
        Environment.ExitCode = succeeded ? 0 : 1;
        Close();
    }

    private static void WriteVisualisationSmokeResult(bool succeeded)
    {
        var path = Environment.GetEnvironmentVariable("COLONY_OPTIMIZER_SMOKE_RESULT_PATH")
            ?? Path.Combine(AppRuntime.VisualSmokeRoot, "result.json");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(new { Succeeded = succeeded, Timestamp = DateTimeOffset.UtcNow }));
        }
        catch (IOException)
        {
            // The smoke-test process still returns a nonzero exit code on failure.
        }
    }
}
