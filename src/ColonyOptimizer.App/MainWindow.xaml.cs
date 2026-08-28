using System.Windows;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ColonyOptimizer.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly bool _visualisationSmokeTest = AppRuntime.IsVisualSmokeTest;
    private bool _visualisationReady;
    private bool _visualisationSmokeTestStarted;
    private Task? _visualisationInitialisationTask;
    private Task? _visualisationQueueTask;
    private TaskCompletionSource<bool>? _visualisationRenderCompletion;
    private bool _renderQueued;
    private bool _layoutUpdateQueued;

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
    }

    private async void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs))
        {
            return;
        }

        if (ReferenceEquals(MainTabs.SelectedItem, VisualisationTab))
        {
            _viewModel.SetVisualisationActive(true);
            await EnsureVisualisationWebViewAsync();
            QueueVisualisationRender();
            return;
        }

        _viewModel.SetVisualisationActive(false);
    }

    private Task EnsureVisualisationWebViewAsync()
    {
        _visualisationInitialisationTask ??= InitialiseVisualisationWebViewAsync();
        return _visualisationInitialisationTask;
    }

    private async Task InitialiseVisualisationWebViewAsync()
    {
        SankeyWebView.NavigationCompleted += SankeyWebViewOnNavigationCompleted;
        try
        {
            await InitialiseVisualisationCoreAsync();
        }
        catch (Exception exception)
        {
            FileLogger.Write(exception, "visualisation-webview");
            _viewModel.ReportVisualisationRuntimeUnavailable();

            if (await InstallBundledWebView2BootstrapperAsync())
            {
                try
                {
                    await InitialiseVisualisationCoreAsync();
                    _viewModel.ReportVisualisationRuntimeInstalled();
                    return;
                }
                catch (Exception retryException)
                {
                    FileLogger.Write(retryException, "visualisation-webview-retry");
                }
            }

            _viewModel.ReportVisualisationRuntimeUnavailable();
        }
    }

    private async Task InitialiseVisualisationCoreAsync()
    {
        // The default WebView2 profile is created beside the executable. That location is
        // read-only for a normal user when the MSI or Setup EXE installs to Program Files.
        var userDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ColonyOptimizer",
            "WebView2");
        Directory.CreateDirectory(userDataDirectory);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataDirectory);
        await SankeyWebView.EnsureCoreWebView2Async(environment);
        SankeyWebView.CoreWebView2.WebMessageReceived -= SankeyWebViewOnWebMessageReceived;
        SankeyWebView.CoreWebView2.WebMessageReceived += SankeyWebViewOnWebMessageReceived;
        SankeyWebView.Source = new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Visualisation", "Sankey.html"));
    }

    private async Task<bool> InstallBundledWebView2BootstrapperAsync()
    {
        var installerPath = Path.Combine(
            AppContext.BaseDirectory,
            "Dependencies",
            "MicrosoftEdgeWebview2Setup.exe");
        if (!File.Exists(installerPath))
        {
            return false;
        }

        try
        {
            _viewModel.ReportVisualisationRuntimeInstallationStarted();
            using var installer = Process.Start(new ProcessStartInfo(installerPath, "/silent /install")
            {
                UseShellExecute = true,
            });
            if (installer is null)
            {
                return false;
            }

            await installer.WaitForExitAsync();
            return installer.ExitCode is 0 or 3010;
        }
        catch (Exception exception)
        {
            FileLogger.Write(exception, "visualisation-runtime-install");
            return false;
        }
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.SankeyGraphJson))
        {
            QueueVisualisationRender();
        }
        else if (eventArgs.PropertyName == nameof(MainWindowViewModel.VisualisationLayoutJson))
        {
            QueueVisualisationLayoutUpdate();
        }
        else if (eventArgs.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen))
        {
            // WebView2 is a native child window and otherwise renders above the WPF settings overlay.
            SankeyWebView.Visibility = _viewModel.IsSettingsOpen ? Visibility.Hidden : Visibility.Visible;
            if (!_viewModel.IsSettingsOpen && IsVisualisationTabActive)
            {
                QueueVisualisationRender();
            }
        }
    }

    private bool IsVisualisationTabActive => ReferenceEquals(MainTabs.SelectedItem, VisualisationTab)
        && !_viewModel.IsSettingsOpen;

    private void QueueVisualisationRender()
    {
        if (_visualisationSmokeTest)
        {
            return;
        }

        _renderQueued = true;
        StartVisualisationQueue();
    }

    private void QueueVisualisationLayoutUpdate()
    {
        if (_visualisationSmokeTest || _viewModel.SelectedVisualisationIndex != 1)
        {
            return;
        }

        _layoutUpdateQueued = true;
        StartVisualisationQueue();
    }

    private void StartVisualisationQueue()
    {
        if (_visualisationQueueTask is { IsCompleted: false })
        {
            return;
        }

        _visualisationQueueTask = ProcessVisualisationQueueAsync();
    }

    private async Task ProcessVisualisationQueueAsync()
    {
        while (IsVisualisationTabActive && _visualisationReady && SankeyWebView.CoreWebView2 is not null)
        {
            if (_renderQueued)
            {
                _renderQueued = false;
                _layoutUpdateQueued = false;
                await RenderVisualisationAsync();
                continue;
            }

            if (_layoutUpdateQueued)
            {
                _layoutUpdateQueued = false;
                await UpdateVisualisationLayoutAsync();
                continue;
            }

            return;
        }
    }

    private async void ResetVisualisationView_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (!_visualisationReady || SankeyWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await SankeyWebView.CoreWebView2.ExecuteScriptAsync("window.resetGraphView && window.resetGraphView();");
        }
        catch (Exception exception)
        {
            FileLogger.Write(exception, "visualisation-reset-view");
        }
    }

    private void SankeyWebViewOnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            using var message = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            if (message.RootElement.ValueKind != JsonValueKind.Object
                || !message.RootElement.TryGetProperty("type", out var type))
            {
                return;
            }

            switch (type.GetString())
            {
                case "visualisation-render-complete":
                    _viewModel.IsVisualisationRendering = false;
                    _visualisationRenderCompletion?.TrySetResult(true);
                    break;
                case "visualisation-render-failed":
                    _viewModel.IsVisualisationRendering = false;
                    _visualisationRenderCompletion?.TrySetResult(false);
                    _viewModel.ReportVisualisationFailure();
                    break;
            }
        }
        catch (JsonException exception)
        {
            FileLogger.Write(exception, "visualisation-message");
        }
    }

    private async void SankeyWebViewOnNavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        _visualisationReady = eventArgs.IsSuccess;
        if (_visualisationSmokeTest && !_visualisationSmokeTestStarted)
        {
            _visualisationSmokeTestStarted = true;
            await RunVisualisationSmokeTestAsync();
            return;
        }

        QueueVisualisationRender();
    }

    private async Task<bool> RenderVisualisationAsync(int? expectedModeOverride = null)
    {
        if (!_visualisationReady || SankeyWebView.CoreWebView2 is null)
        {
            return false;
        }

        _viewModel.IsVisualisationRendering = true;
        var expectedMode = expectedModeOverride ?? _viewModel.SelectedVisualisationIndex;
        var renderCompletion = _visualisationSmokeTest
            ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        if (renderCompletion is not null)
        {
            _visualisationRenderCompletion = renderCompletion;
        }
        try
        {
            await SankeyWebView.CoreWebView2.ExecuteScriptAsync($"window.setGraph({_viewModel.SankeyGraphJson});");
            if (renderCompletion is null)
            {
                return true;
            }

            if (!await renderCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30)))
            {
                throw new InvalidOperationException("The visualisation reported a render failure.");
            }

            var response = await SankeyWebView.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.getGraphState ? window.getGraphState() : null);");
            var stateJson = JsonSerializer.Deserialize<string>(response);
            using var state = JsonDocument.Parse(stateJson ?? "null");
            var renderedMode = state.RootElement.GetProperty("mode").GetInt32();
            var expectsElkLayout = expectedMode == 1;
            var populated = state.RootElement.ValueKind == JsonValueKind.Object
                && state.RootElement.GetProperty("nodes").GetInt32() > 0
                && state.RootElement.GetProperty("links").GetInt32() > 0
                && state.RootElement.GetProperty("panEnabled").GetBoolean()
                && state.RootElement.GetProperty("wheelZoomEnabled").GetBoolean()
                && renderedMode == expectedMode
                && (!expectsElkLayout || state.RootElement.GetProperty("layoutEngine").GetString() == "elk-layered")
                && (!_visualisationSmokeTest || !expectsElkLayout || state.RootElement.GetProperty("collapsedItemNodes").GetInt32() > 0);
            if (_viewModel.HasVisualisationGraph && !populated)
            {
                throw new InvalidOperationException($"The visualisation did not create the expected interactive graph. State: {state.RootElement.GetRawText()}");
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

            _viewModel.IsVisualisationRendering = false;
            return populated || !_viewModel.HasVisualisationGraph;
        }
        catch (Exception exception)
        {
            _viewModel.IsVisualisationRendering = false;
            FileLogger.Write(exception, "visualisation-render");
            _viewModel.ReportVisualisationFailure();
            return false;
        }
        finally
        {
            if (ReferenceEquals(_visualisationRenderCompletion, renderCompletion))
            {
                _visualisationRenderCompletion = null;
            }
        }
    }

    private async Task<bool> UpdateVisualisationLayoutAsync()
    {
        if (!_visualisationReady || SankeyWebView.CoreWebView2 is null || !_viewModel.HasVisualisationGraph)
        {
            return false;
        }

        _viewModel.IsVisualisationRendering = true;
        try
        {
            await SankeyWebView.CoreWebView2.ExecuteScriptAsync($"window.setLayoutOptions({_viewModel.VisualisationLayoutJson});");
            return true;
        }
        catch (Exception exception)
        {
            _viewModel.IsVisualisationRendering = false;
            FileLogger.Write(exception, "visualisation-layout");
            _viewModel.ReportVisualisationFailure();
            return false;
        }
    }

    private async Task RunVisualisationSmokeTestAsync()
    {
        var optimisationSucceeded = await _viewModel.RunVisualisationSmokeOptimisationAsync();
        _viewModel.NodeSpacing = 0;
        _viewModel.LayerSpacing = 0;
        _viewModel.SelectedVisualisationIndex = 0;
        var sankeySucceeded = await RenderVisualisationAsync(expectedModeOverride: 0);
        _viewModel.SelectedVisualisationIndex = 1;
        var nodeVisualiserSucceeded = await RenderVisualisationAsync(expectedModeOverride: 1);
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
