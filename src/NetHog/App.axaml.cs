using System.IO;
using System.IO.Pipes;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace NetHog.Avalonia;

public partial class App : Application
{
    private const string InstanceMutexName = "NetHog.SingleInstance";
    private const string ActivationPipeName = "NetHog.Activate";
    private Mutex? _instanceMutex;
    private CancellationTokenSource? _activationCancellation;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public static void ApplyTheme(bool darkMode)
    {
        if (Current is not App app) return;
        // Semi.Avalonia and the DataGrid theme use Avalonia's theme variant
        // resources. Keep them in sync with NetHog's persisted appearance
        // setting so their foregrounds do not stay on the light palette.
        app.RequestedThemeVariant = darkMode ? ThemeVariant.Dark : ThemeVariant.Light;
        SetBrush(app, "CanvasBrush", darkMode ? "#11171B" : "#F4F6F8");
        SetBrush(app, "SurfaceBrush", darkMode ? "#1A2328" : "#FFFFFF");
        SetBrush(app, "SidebarBrush", darkMode ? "#151E22" : "#F8F9FA");
        SetBrush(app, "InkBrush", darkMode ? "#E9F1EF" : "#18212B");
        SetBrush(app, "MutedBrush", darkMode ? "#9AABA8" : "#626D7A");
        SetBrush(app, "LineBrush", darkMode ? "#304046" : "#E2E7EC");
        SetBrush(app, "AccentBrush", darkMode ? "#1F806A" : "#126B5A");
        SetBrush(app, "AccentSoftBrush", darkMode ? "#193A35" : "#E7F3EF");
        SetBrush(app, "AccentHoverBrush", darkMode ? "#203B36" : "#F0F8F6");
        SetBrush(app, "AccentPressedBrush", darkMode ? "#285247" : "#E7F3EF");
        SetBrush(app, "PrimaryHoverBrush", darkMode ? "#267F6D" : "#105F50");
        SetBrush(app, "PrimaryPressedBrush", darkMode ? "#1F6B5A" : "#0D584A");
        SetBrush(app, "GoodBrush", darkMode ? "#45B998" : "#16745F");
        SetBrush(app, "GoodSoftBrush", darkMode ? "#173A31" : "#E9F4F0");
        SetBrush(app, "WarningBrush", darkMode ? "#DDB45D" : "#946200");
        SetBrush(app, "WarningSoftBrush", darkMode ? "#3B3019" : "#FFF4D6");
        SetBrush(app, "WarningTextBrush", darkMode ? "#F0CC78" : "#6C5317");
        SetBrush(app, "DangerBrush", darkMode ? "#E9787D" : "#B33D43");
        SetBrush(app, "DangerSoftBrush", darkMode ? "#3A2228" : "#FBECEE");
        SetBrush(app, "DangerLineBrush", darkMode ? "#5E343A" : "#F1D8DB");
        SetBrush(app, "HeaderSurfaceBrush", darkMode ? "#202B30" : "#FAFBFC");
        SetBrush(app, "TableAlternateBrush", darkMode ? "#1D282D" : "#FBFCFC");
        SetBrush(app, "ButtonDisabledBrush", darkMode ? "#2A3739" : "#DCE6E2");
        SetBrush(app, "ButtonDisabledTextBrush", darkMode ? "#80928D" : "#52635D");
        SetBrush(app, "DisabledOptionBrush", darkMode ? "#A8B8B4" : "#66736F");
        SetBrush(app, "FocusBrush", darkMode ? "#6ED3B7" : "#63B9A5");
        SetBrush(app, "NicknameSurfaceBrush", darkMode ? "#202D31" : "#F7FAF9");
        SetBrush(app, "NicknameBorderBrush", darkMode ? "#3A5750" : "#C9DAD5");
        SetBrush(app, "NicknameHoverBorderBrush", darkMode ? "#5E9A8B" : "#8DBCAF");
        SetBrush(app, "InputUnitSurfaceBrush", darkMode ? "#202B30" : "#F4F6F8");
        SetBrush(app, "OverlayGlassBrush", darkMode ? "#1A2328" : "#F7FAF9");
        SetBrush(app, "OverlayGlassLineBrush", darkMode ? "#3A5750" : "#C9DAD5");
        SetBrush(app, "OverlayGlassHighlightBrush", darkMode ? "#253A35" : "#FFFFFF");
        SetBrush(app, "DownloadBrush", darkMode ? "#5AC8A8" : "#126B5A");
        SetBrush(app, "UploadBrush", darkMode ? "#79B8FF" : "#1976D2");
        SetBrush(app, "TitleBarBrush", darkMode ? "#11171B" : "#F4F6F8");
        SetBrush(app, "TitleBarTextBrush", darkMode ? "#E9F1EF" : "#18212B");
        SetBrush(app, "TitleBarBorderBrush", darkMode ? "#304046" : "#E2E7EC");
        SetBrush(app, "ButtonSolidForeground", "#FFFFFF");
        SetBrush(app, "ButtonSolidPrimaryBackground", darkMode ? "#1F806A" : "#126B5A");
        SetBrush(app, "ButtonSolidPrimaryPointeroverBackground", darkMode ? "#267F6D" : "#105F50");
        SetBrush(app, "ButtonSolidPrimaryPressedBackground", darkMode ? "#1F6B5A" : "#0D584A");
        SetBrush(app, "ButtonSolidPrimaryPressedForeground", "#FFFFFF");
        SetBrush(app, "ButtonSolidPrimaryBorderBrush", darkMode ? "#1F806A" : "#126B5A");
        SetBrush(app, "ButtonSolidPrimaryPointeroverBorderBrush", darkMode ? "#267F6D" : "#105F50");
        SetBrush(app, "ButtonSolidPrimaryPressedBorderBrush", darkMode ? "#1F6B5A" : "#0D584A");
        SetBrush(app, "ButtonSolidDisabledBackground", darkMode ? "#2A3739" : "#DCE6E2");
        SetBrush(app, "ButtonSolidDisabledForeground", darkMode ? "#80928D" : "#52635D");
        SetBrush(app, "ComboBoxDropDownBackground", darkMode ? "#1A2328" : "#FFFFFF");
        SetBrush(app, "ComboBoxDropDownBorderBrush", darkMode ? "#304046" : "#E2E7EC");
        SetBrush(app, "ComboBoxDropDownGlyphForeground", darkMode ? "#9AABA8" : "#626D7A");
        SetBrush(app, "ComboBoxDropDownGlyphForegroundFocused", darkMode ? "#6ED3B7" : "#126B5A");
        SetBrush(app, "ComboBoxItemBackground", darkMode ? "#1A2328" : "#FFFFFF");
        SetBrush(app, "ComboBoxItemBackgroundPointerOver", darkMode ? "#203B36" : "#F0F8F6");
        SetBrush(app, "ComboBoxItemBackgroundSelected", darkMode ? "#193A35" : "#E7F3EF");
        SetBrush(app, "ComboBoxItemBackgroundSelectedPointerOver", darkMode ? "#203B36" : "#E7F3EF");
        SetBrush(app, "ComboBoxItemBackgroundPressed", darkMode ? "#285247" : "#E7F3EF");
        SetBrush(app, "ComboBoxItemBackgroundFocused", darkMode ? "#203B36" : "#F0F8F6");
        SetBrush(app, "ComboBoxItemBackgroundDisabled", darkMode ? "#2A3739" : "#F4F6F8");
        SetBrush(app, "ComboBoxItemForeground", darkMode ? "#E9F1EF" : "#18212B");
        SetBrush(app, "ComboBoxItemForegroundPointerOver", darkMode ? "#6ED3B7" : "#126B5A");
        SetBrush(app, "ComboBoxItemForegroundSelected", darkMode ? "#6ED3B7" : "#126B5A");
        SetBrush(app, "ComboBoxItemForegroundSelectedPointerOver", darkMode ? "#6ED3B7" : "#126B5A");
        SetBrush(app, "ComboBoxItemForegroundPressed", darkMode ? "#6ED3B7" : "#126B5A");
        SetBrush(app, "ComboBoxItemForegroundFocused", darkMode ? "#E9F1EF" : "#18212B");
        SetBrush(app, "ComboBoxItemForegroundDisabled", darkMode ? "#80928D" : "#52635D");
        SetBrush(app, "ComboBoxItemBorderBrush", darkMode ? "#1A2328" : "#FFFFFF");
        SetBrush(app, "ComboBoxItemBorderBrushPointerOver", darkMode ? "#193A35" : "#E7F3EF");
        SetBrush(app, "ComboBoxItemBorderBrushPressed", darkMode ? "#193A35" : "#E7F3EF");
        SetBrush(app, "ComboBoxItemBorderBrushFocused", darkMode ? "#193A35" : "#E7F3EF");
        SetBrush(app, "ComboBoxItemBorderBrushSelected", darkMode ? "#193A35" : "#E7F3EF");
        SetBrush(app, "ComboBoxItemBorderBrushSelectedPointerOver", darkMode ? "#5E9A8B" : "#63B9A5");

        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                WindowsTitleBarTheme.Apply(window, darkMode);
            }
        }
    }

    private static void SetBrush(App app, string key, string value) =>
        app.Resources[key] = new SolidColorBrush(Color.Parse(value));

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var ownsMutex = false;
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out ownsMutex);
            if (!ownsMutex)
            {
                NotifyExistingInstance();
                desktop.Shutdown();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            desktop.MainWindow = new MainWindow();
            _activationCancellation = new CancellationTokenSource();
            desktop.Exit += (_, _) =>
            {
                _activationCancellation.Cancel();
                _activationCancellation.Dispose();
                try { _instanceMutex.ReleaseMutex(); }
                catch (ApplicationException) { }
                _instanceMutex.Dispose();
            };
            _ = ListenForActivationAsync(desktop, _activationCancellation.Token);
        }

        base.OnFrameworkInitializationCompleted();
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
            // The existing process may still be starting or may not expose a pipe host.
        }
    }

    private static async Task ListenForActivationAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        CancellationToken cancellationToken)
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
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        (desktop.MainWindow as MainWindow)?.ActivateFromExternalLaunch());
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
}
