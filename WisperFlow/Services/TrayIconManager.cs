using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Extensions.Logging;

namespace WisperFlow.Services;

/// <summary>
/// Manages the system tray icon and context menu.
/// </summary>
public class TrayIconManager : IDisposable
{
    private readonly SettingsManager _settingsManager;
    private readonly DictationOrchestrator _orchestrator;
    private readonly ILogger<TrayIconManager> _logger;
    private TaskbarIcon? _trayIcon;
    private MenuItem? _enableMenuItem;
    private bool _disposed;

    public TrayIconManager(
        SettingsManager settingsManager,
        DictationOrchestrator orchestrator,
        ILogger<TrayIconManager> logger)
    {
        _settingsManager = settingsManager;
        _orchestrator = orchestrator;
        _logger = logger;

        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "WisperFlow - Press Ctrl+Win to dictate",
                Visibility = Visibility.Visible
            };

            // Create icon from embedded resource or generate one
            _trayIcon.Icon = CreateDefaultIcon();

            // Create context menu
            var contextMenu = new ContextMenu();

            // Status item (non-clickable)
            var statusItem = new MenuItem
            {
                Header = "WisperFlow",
                IsEnabled = false,
                FontWeight = FontWeights.Bold
            };
            contextMenu.Items.Add(statusItem);
            contextMenu.Items.Add(new Separator());

            // Enable/Disable toggle
            _enableMenuItem = new MenuItem
            {
                Header = _settingsManager.CurrentSettings.HotkeyEnabled ? "✓ Enabled" : "Disabled",
                IsCheckable = false
            };
            _enableMenuItem.Click += OnToggleEnabled;
            contextMenu.Items.Add(_enableMenuItem);

            contextMenu.Items.Add(new Separator());

            // Settings
            var settingsItem = new MenuItem { Header = "Settings..." };
            settingsItem.Click += OnSettingsClick;
            contextMenu.Items.Add(settingsItem);

            contextMenu.Items.Add(new Separator());

            // Quit
            var quitItem = new MenuItem { Header = "Quit" };
            quitItem.Click += OnQuitClick;
            contextMenu.Items.Add(quitItem);

            _trayIcon.ContextMenu = contextMenu;

            // Double-click opens settings
            _trayIcon.TrayMouseDoubleClick += (s, e) => OnSettingsClick(s, e);

            _logger.LogInformation("Tray icon initialized");
        });
    }

    private Icon CreateDefaultIcon()
    {
        // Create a simple microphone-style icon programmatically
        using var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        
        // Background (transparent)
        g.Clear(Color.Transparent);
        
        // Draw a simple microphone shape
        using var brush = new SolidBrush(Color.FromArgb(0, 212, 170)); // Accent color
        using var pen = new Pen(brush, 2);

        // Microphone head (rounded rectangle)
        g.FillEllipse(brush, 10, 4, 12, 16);
        
        // Microphone stand
        g.DrawArc(pen, 8, 14, 16, 12, 0, 180);
        g.DrawLine(pen, 16, 20, 16, 28);
        
        // Base
        g.DrawLine(pen, 10, 28, 22, 28);

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private void OnToggleEnabled(object sender, RoutedEventArgs e)
    {
        var newState = !_settingsManager.CurrentSettings.HotkeyEnabled;
        _settingsManager.UpdateSetting(s => s.HotkeyEnabled = newState);
        
        if (_enableMenuItem != null)
        {
            _enableMenuItem.Header = newState ? "✓ Enabled" : "Disabled";
        }

        _orchestrator.SetEnabled(newState);
        
        UpdateTooltip(newState);
        _logger.LogInformation("Hotkey {State}", newState ? "enabled" : "disabled");
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        ((App)Application.Current).ShowSettings();
    }

    private void OnQuitClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Quit requested from tray menu");
        Application.Current.Shutdown();
    }

    private void UpdateTooltip(bool enabled)
    {
        if (_trayIcon != null)
        {
            _trayIcon.ToolTipText = enabled
                ? "WisperFlow - Press Ctrl+Win to dictate"
                : "WisperFlow - Disabled";
        }
    }

    /// <summary>
    /// Shows a balloon notification.
    /// </summary>
    public void ShowNotification(string title, string message, BalloonIcon icon = BalloonIcon.Info)
    {
        _trayIcon?.ShowBalloonTip(title, message, icon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        });

        GC.SuppressFinalize(this);
    }
}

