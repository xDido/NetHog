using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace NetHog;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = "NetHog.SingleInstance";
    private const string ActivationPipeName = "NetHog.Activate";
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetHog",
        "crash.log");
    private Mutex? _instanceMutex;
    private CancellationTokenSource? _activationCancellation;

    internal static event EventHandler? ThemeChanged;

    public App()
    {
        // Configure the WinForms tray component before WPF creates any windows.
        System.Windows.Forms.Application.SetHighDpiMode(
            System.Windows.Forms.HighDpiMode.PerMonitorV2);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var ownsMutex = false;
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out ownsMutex);
        if (!ownsMutex)
        {
            NotifyExistingInstance();
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
        _activationCancellation = new CancellationTokenSource();
        _ = ListenForActivationAsync(_activationCancellation.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCancellation?.Cancel();
        _activationCancellation?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); }
        catch (ApplicationException) { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void NotifyExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ActivationPipeName, PipeDirection.Out);
            client.Connect(500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("activate");
        }
        catch
        {
            // The existing process may still be starting. It remains the owner
            // of the mutex and will receive the next launch if its pipe is ready.
        }
    }

    private async Task ListenForActivationAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (string.Equals(command, "activate", StringComparison.OrdinalIgnoreCase))
                {
                    await Dispatcher.InvokeAsync(() =>
                        (MainWindow as MainWindow)?.ActivateFromExternalLaunch());
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // A short-lived second launch can disconnect before sending its command.
            }
        }
    }

    internal static void ApplyTheme(bool darkMode)
    {
        if (Current is not App app) return;

        SetThemeResource(app, "CanvasColor", "CanvasBrush", "#F4F6F8", "#11171B", darkMode);
        SetThemeResource(app, "SurfaceColor", "SurfaceBrush", "#FFFFFF", "#1A2328", darkMode);
        SetThemeResource(app, "SidebarColor", "SidebarBrush", "#F8F9FA", "#151E22", darkMode);
        SetThemeResource(app, "InkColor", "InkBrush", "#18212B", "#E9F1EF", darkMode);
        SetThemeResource(app, "MutedColor", "MutedBrush", "#626D7A", "#9AABA8", darkMode);
        SetThemeResource(app, "LineColor", "LineBrush", "#E2E7EC", "#304046", darkMode);
        SetThemeResource(app, "AccentColor", "AccentBrush", "#126B5A", "#1F806A", darkMode);
        SetThemeResource(app, "AccentSoftColor", "AccentSoftBrush", "#E7F3EF", "#193A35", darkMode);
        SetThemeResource(app, "PrimaryButtonTextColor", "PrimaryButtonTextBrush", "#FFFFFF", "#FFFFFF", darkMode);
        SetThemeResource(app, "GoodColor", "GoodBrush", "#16745F", "#45B998", darkMode);
        SetThemeResource(app, "GoodSoftColor", "GoodSoftBrush", "#E9F4F0", "#173A31", darkMode);
        SetThemeResource(app, "WarningColor", "WarningBrush", "#946200", "#DDB45D", darkMode);
        SetThemeResource(app, "WarningSoftColor", "WarningSoftBrush", "#FFF4D6", "#3B3019", darkMode);
        SetThemeResource(app, "DangerColor", "DangerBrush", "#B33D43", "#E9787D", darkMode);
        SetThemeResource(app, "DangerSoftColor", "DangerSoftBrush", "#FBECEE", "#3A2228", darkMode);
        SetThemeResource(app, "HeaderSurfaceColor", "HeaderSurfaceBrush", "#FAFBFC", "#202B30", darkMode);
        SetThemeResource(app, "TableAlternateColor", "TableAlternateBrush", "#FBFCFC", "#1D282D", darkMode);
        SetThemeResource(app, "ButtonDisabledColor", "ButtonDisabledBrush", "#DCE6E2", "#2A3739", darkMode);
        SetThemeResource(app, "ButtonDisabledBorderColor", "ButtonDisabledBorderBrush", "#DCE6E2", "#2A3739", darkMode);
        SetThemeResource(app, "ButtonDisabledTextColor", "ButtonDisabledTextBrush", "#52635D", "#80928D", darkMode);
        SetThemeResource(app, "FocusColor", "FocusBrush", "#63B9A5", "#6ED3B7", darkMode);
        SetThemeResource(app, "NicknameSurfaceColor", "NicknameSurfaceBrush", "#F7FAF9", "#202D31", darkMode);
        SetThemeResource(app, "NicknameBorderColor", "NicknameBorderBrush", "#C9DAD5", "#3A5750", darkMode);
        SetThemeResource(app, "NicknameHoverBorderColor", "NicknameHoverBorderBrush", "#8DBCAF", "#5E9A8B", darkMode);
        SetThemeResource(app, "WarningTextColor", "WarningTextBrush", "#6C5317", "#F0CC78", darkMode);
        SetThemeResource(app, "DangerLineColor", "DangerLineBrush", "#F1D8DB", "#5E343A", darkMode);
        SetThemeResource(app, "InputUnitSurfaceColor", "InputUnitSurfaceBrush", "#F4F6F8", "#202B30", darkMode);
        SetThemeResource(app, "OverlayGlassColor", "OverlayGlassBrush", "#A6FFFFFF", "#CC1A2328", darkMode);
        SetThemeResource(app, "OverlayGlassLineColor", "OverlayGlassLineBrush", "#80FFFFFF", "#664B6268", darkMode);
        SetThemeResource(app, "OverlayGlassHighlightColor", "OverlayGlassHighlightBrush", "#55FFFFFF", "#26FFFFFF", darkMode);
        SetBrush(app, "DownloadBrush", darkMode ? "#5AC8A8" : "#126B5A");
        SetBrush(app, "UploadBrush", darkMode ? "#F0C66A" : "#946200");

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void SetThemeResource(
        App app,
        string colorKey,
        string brushKey,
        string lightValue,
        string darkValue,
        bool darkMode)
    {
        var color = ParseColor(darkMode ? darkValue : lightValue);
        app.Resources[colorKey] = color;
        SetBrush(app, brushKey, color);
    }

    private static void SetBrush(App app, string key, string value) =>
        SetBrush(app, key, ParseColor(value));

    private static void SetBrush(App app, string key, Color color)
    {
        if (app.Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }

        app.Resources[key] = new SolidColorBrush(color);
    }

    private static Color ParseColor(string value) =>
        (Color)ColorConverter.ConvertFromString(value);

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        e.Handled = true;
        MessageBox.Show(
            "NetHog handled an unexpected error and is still running. Details were saved to the NetHog app-data folder.",
            "NetHog recovered from an error",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) LogException(exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException(e.Exception);
        e.SetObserved();
    }

    internal static void LogException(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTimeOffset.Now:O}] {exception}\r\n\r\n");
        }
        catch
        {
            // Crash reporting must never cause a second failure.
        }
    }
}
