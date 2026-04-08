using System.IO;
using System.Text.Json;
using System.Windows;
using ClickyWindows.AI;
using ClickyWindows.Input;
using ClickyWindows.Overlay;
using ClickyWindows.Setup;
using ClickyWindows.Tray;
using Serilog;

namespace ClickyWindows;

public partial class App : System.Windows.Application
{
    private TrayIconManager? _tray;
    private OverlayManager? _overlayManager;
    private GlobalHotkeyService? _hotkeyService;
    private PushToTalkController? _pushToTalk;
    private AppSettings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SetupLogging();
        LoadSettings();

        Log.Information("ClickyWindows starting up");

        SetupGlobalExceptionHandlers();

        if (!CredentialStore.HasAllKeys())
        {
            Log.Warning("Missing API keys: {MissingKeys}", string.Join(", ", CredentialStore.GetMissingKeyNames()));
            var wizard = new SetupWizardWindow();
            if (wizard.ShowDialog() != true)
            {
                Shutdown(0);
                return;
            }
        }

        _overlayManager = new OverlayManager();
        _overlayManager.Initialize();

        _pushToTalk = new PushToTalkController(_overlayManager, _settings);

        _hotkeyService = new GlobalHotkeyService(_settings);
        _hotkeyService.HotkeyPressed += _pushToTalk.OnHotkeyPressed;
        _hotkeyService.HotkeyReleased += _pushToTalk.OnHotkeyReleased;
        _hotkeyService.Start();

        _tray = new TrayIconManager(_settings, _pushToTalk);
        _tray.QuitRequested += OnQuitRequested;
        _tray.Initialize();

        Log.Information("ClickyWindows started successfully");
    }

    private void LoadSettings()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                var json = File.ReadAllText(settingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load appsettings.json, using defaults");
            }
        }
    }

    private static void SetupLogging()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClickyWindows", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "clicky-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .WriteTo.Console()
            .CreateLogger();
    }

    private void SetupGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled dispatcher exception");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled domain exception");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warning(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private void OnQuitRequested()
    {
        Log.Information("Quit requested");
        _hotkeyService?.Stop();
        _pushToTalk?.Dispose();
        _overlayManager?.Dispose();
        _tray?.Dispose();
        Log.CloseAndFlush();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("ClickyWindows exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
