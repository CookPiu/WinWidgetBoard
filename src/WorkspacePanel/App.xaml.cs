using Microsoft.UI.Xaml;
using System.Diagnostics;
using WinWidgetBoard.WorkspacePanel.Ipc;
using WinWidgetBoard.WorkspacePanel.Shell;

namespace WinWidgetBoard.WorkspacePanel;

public partial class App : Application, IAsyncDisposable, IDisposable
{
    private const int WindowInitializationFailedExitCode = 20;
    private const int BrokerSmokeTestFailedExitCode = 21;
    private readonly bool _isSmokeTest;
    private readonly bool _isBrokerSmokeTest;
    private readonly bool _isAcceptanceTest;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private static Mutex? _instanceMutex;
    private Window? _window;
    private CoreBrokerSession? _brokerSession;
    private int _disposed;

    public App()
    {
        StartupTrace.Initialize();
        StartupTrace.Mark("app-ctor");
        string[] arguments = Environment.GetCommandLineArgs();
        _isSmokeTest = arguments
            .Any(argument => string.Equals(argument, "--smoke-test", StringComparison.OrdinalIgnoreCase));
        _isBrokerSmokeTest = arguments
            .Any(argument => string.Equals(argument, "--broker-smoke-test", StringComparison.OrdinalIgnoreCase));
        _isAcceptanceTest = arguments
            .Any(argument => string.Equals(
                argument,
                WorkspacePanelInstanceIdentity.AcceptanceTestSwitch,
                StringComparison.OrdinalIgnoreCase));

        string instanceMutexName =
            WorkspacePanelInstanceIdentity.ResolveMutexName(arguments);
        _instanceMutex = new Mutex(true, instanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            Environment.Exit(0);
        }

        InitializeComponent();
        StartupTrace.Mark("app-ctor-done");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            StartupTrace.Mark("app-launched");
            if (!_isSmokeTest && !_isBrokerSmokeTest)
            {
                _brokerSession = new CoreBrokerSession();
            }

            StartupTrace.Mark("broker-session-created");
            _window = new MainWindow(
                _brokerSession?.Notes,
                _brokerSession?.Layout,
                keepOpenForAcceptance: _isAcceptanceTest,
                cardsClient: _brokerSession?.Cards,
                weatherSettingsClient: _brokerSession?.WeatherSettings,
                systemMonitorClient: _brokerSession?.SystemMonitor,
                tokenUsageClient: _brokerSession?.TokenUsage);

            if (_brokerSession is not null && _window is MainWindow createdMainWindow)
            {
                _brokerSession.Reconnected += createdMainWindow.HandleBrokerReconnected;
            }

            if (_isSmokeTest)
            {
                // The smoke path validates XAML loading and window construction without
                // entering the normal interactive lifetime. Application.Exit() is not
                // sufficient before activation on every unpackaged WinAppSDK runtime.
                Environment.Exit(0);
                return;
            }

            StartupTrace.Mark("window-constructed");
            _window.Activate();
            StartupTrace.Mark("window-activated");
            if (_window is MainWindow mainWindow)
            {
                mainWindow.FocusInitialElement();
                mainWindow.BeginOpeningMotion();
            }

            if (_isBrokerSmokeTest)
            {
                _brokerSession = new CoreBrokerSession();
                _ = RunBrokerSmokeTestAsync(_brokerSession, _lifetimeCancellation.Token);
                return;
            }

            _window.Closed += Window_Closed;
            _ = StartBrokerSessionAsync(
                _brokerSession!,
                (MainWindow)_window,
                enableAutomaticWeatherLocation: !_isAcceptanceTest,
                _lifetimeCancellation.Token);
        }
        catch (Exception exception)
        {
            string diagnostic = $"WorkspacePanel window initialization failed: {exception}";
            Debug.WriteLine(diagnostic);
            Console.Error.WriteLine(diagnostic);
            Environment.Exit(WindowInitializationFailedExitCode);
        }
    }

    private static async Task<bool> StartBrokerSessionAsync(
        CoreBrokerSession session,
        MainWindow? mainWindow,
        bool enableAutomaticWeatherLocation,
        CancellationToken cancellationToken)
    {
        try
        {
            bool started = await session.TryStartAsync(cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                mainWindow?.MarkNoteEditorUnavailable();
                mainWindow?.MarkLayoutUnavailable();
                return false;
            }

            bool visible = await session.ReportVisibilityAsync(
                true,
                cancellationToken).ConfigureAwait(false);
            if (mainWindow is not null)
            {
                await mainWindow.InitializeLayoutAsync(cancellationToken)
                    .ConfigureAwait(false);
                await mainWindow.InitializeNoteEditorAsync(cancellationToken)
                    .ConfigureAwait(false);
                await mainWindow.InitializeCardSubscriptionAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (enableAutomaticWeatherLocation)
                {
                    try
                    {
                        await mainWindow.RefreshAutomaticWeatherLocationAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                        when (exception is not OutOfMemoryException and
                            not StackOverflowException and
                            not AccessViolationException)
                    {
                        // Automatic location is an optional weather input. It must never make
                        // layout, notes or the panel lifetime fail, and diagnostics must not
                        // include coordinates or the provider's raw message.
                        Debug.WriteLine(
                            $"Automatic weather location unavailable: " +
                            exception.GetType().Name);
                    }
                }
            }

            return visible;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task RunBrokerSmokeTestAsync(
        CoreBrokerSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            bool started = await StartBrokerSessionAsync(
                    session,
                    null,
                    enableAutomaticWeatherLocation: false,
                    cancellationToken)
                .ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            Environment.Exit(started ? 0 : BrokerSmokeTestFailedExitCode);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"WorkspacePanel broker smoke failed: {exception}");
            Console.Error.WriteLine($"WorkspacePanel broker smoke failed: {exception.Message}");
            Environment.Exit(BrokerSmokeTestFailedExitCode);
        }
    }

    private async void Window_Closed(object sender, WindowEventArgs args)
    {
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_brokerSession is not null)
        {
            using var visibilityCancellation = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(250));
            try
            {
                await _brokerSession.ReportVisibilityAsync(
                    false,
                    visibilityCancellation.Token);
            }
            catch (OperationCanceledException) when (visibilityCancellation.IsCancellationRequested)
            {
            }
        }

        _lifetimeCancellation.Cancel();
        if (_window is MainWindow mainWindow)
        {
            _brokerSession?.Reconnected -= mainWindow.HandleBrokerReconnected;
            await mainWindow.DisposeAsync();
        }

        if (_brokerSession is not null)
        {
            await _brokerSession.DisposeAsync();
            _brokerSession = null;
        }

        _lifetimeCancellation.Dispose();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        GC.SuppressFinalize(this);
    }

    void IDisposable.Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
