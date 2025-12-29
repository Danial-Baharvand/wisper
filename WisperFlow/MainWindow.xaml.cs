using System.Windows;

namespace WisperFlow;

/// <summary>
/// Hidden main window. WisperFlow runs from the system tray.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        // Hide immediately - we run from system tray
        Visibility = Visibility.Hidden;
        ShowInTaskbar = false;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Prevent closing via Alt+F4, etc. - use tray menu to quit
        e.Cancel = true;
        Hide();
    }
}

