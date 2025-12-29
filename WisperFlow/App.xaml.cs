using System.IO;
using System.Windows;
using Microsoft.Extensions.Logging;
using WisperFlow.Services;

namespace WisperFlow;

public partial class App : Application
{
    private TrayIconManager? _trayIconManager;
    private HotkeyManager? _hotkeyManager;
    private AudioRecorder? _audioRecorder;
    private TextInjector? _textInjector;
    private SettingsManager? _settingsManager;
    private OverlayWindow? _overlayWindow;
    private ModelManager? _modelManager;
    private ServiceFactory? _serviceFactory;
    private ILogger<App>? _logger;
    private ILoggerFactory? _loggerFactory;
    private DictationOrchestrator? _orchestrator;

    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WisperFlow", "wisperflow.log");

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var logDir = Path.GetDirectoryName(LogFilePath)!;
        Directory.CreateDirectory(logDir);

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new FileLoggerProvider(LogFilePath));
            builder.SetMinimumLevel(LogLevel.Debug);
        });
        _logger = _loggerFactory.CreateLogger<App>();
        _logger.LogInformation("=== WisperFlow starting up ===");
        _logger.LogInformation("Log file: {LogPath}", LogFilePath);

        try
        {
            InitializeServices();
            _logger.LogInformation("All services initialized successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize services");
            MessageBox.Show($"Failed to start WisperFlow: {ex.Message}", "Startup Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void InitializeServices()
    {
        _settingsManager = new SettingsManager(_loggerFactory!.CreateLogger<SettingsManager>());
        var settings = _settingsManager.LoadSettings();
        
        _modelManager = new ModelManager(_loggerFactory!.CreateLogger<ModelManager>());
        _serviceFactory = new ServiceFactory(_loggerFactory!, _modelManager);
        
        _hotkeyManager = new HotkeyManager(_loggerFactory!.CreateLogger<HotkeyManager>());
        _audioRecorder = new AudioRecorder(_loggerFactory!.CreateLogger<AudioRecorder>());
        _textInjector = new TextInjector(_loggerFactory!.CreateLogger<TextInjector>());
        _overlayWindow = new OverlayWindow();
        
        _orchestrator = new DictationOrchestrator(
            _hotkeyManager, 
            _audioRecorder, 
            _textInjector, 
            _overlayWindow, 
            _settingsManager,
            _serviceFactory,
            _loggerFactory!.CreateLogger<DictationOrchestrator>());
        
        _trayIconManager = new TrayIconManager(_settingsManager, _orchestrator,
            _loggerFactory!.CreateLogger<TrayIconManager>());
        
        _orchestrator.ApplySettings(settings);
        _hotkeyManager.RegisterHotkey(settings.HotkeyModifiers, settings.HotkeyKey);
        
        // Initialize services in background
        _ = _orchestrator.InitializeServicesAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("WisperFlow shutting down");
        _trayIconManager?.Dispose();
        _hotkeyManager?.Dispose();
        _audioRecorder?.Dispose();
        _overlayWindow?.Close();
        _loggerFactory?.Dispose();
        base.OnExit(e);
    }

    public void ShowSettings()
    {
        var settingsWindow = new SettingsWindow(_settingsManager!, _orchestrator!, _audioRecorder!, _modelManager!);
        settingsWindow.ShowDialog();
    }
}
