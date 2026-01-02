using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace WisperFlow;

/// <summary>
/// Popup window for OAuth sign-in flows.
/// Shares the same WebView2 environment as the parent browser for cookie/session sharing.
/// </summary>
public partial class BrowserPopupWindow : Window
{
    private readonly CoreWebView2Environment _environment;
    private bool _isInitialized = false;

    public BrowserPopupWindow(CoreWebView2Environment environment)
    {
        InitializeComponent();
        _environment = environment;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        if (_isInitialized) return;
        
        try
        {
            // Use the same environment as the parent window for session sharing
            await PopupWebView.EnsureCoreWebView2Async(_environment);
            
            // Configure for popup behavior
            PopupWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            PopupWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            
            // Close window when popup closes itself (e.g., after OAuth completes)
            PopupWebView.CoreWebView2.WindowCloseRequested += (s, args) =>
            {
                Close();
            };
            
            _isInitialized = true;
        }
        catch
        {
            Close();
        }
    }

    /// <summary>
    /// Gets the CoreWebView2 instance for directing popup content.
    /// </summary>
    public async Task<CoreWebView2?> GetCoreWebView2Async()
    {
        if (!_isInitialized)
        {
            await InitializeAsync();
        }
        return PopupWebView.CoreWebView2;
    }

    /// <summary>
    /// Gets the WebView2 control for setting as the new window target.
    /// </summary>
    public Microsoft.Web.WebView2.Wpf.WebView2 WebViewControl => PopupWebView;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
