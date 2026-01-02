using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;
using WisperFlow.Services;

namespace WisperFlow;

/// <summary>
/// Floating browser window for embedded AI chat providers (ChatGPT, Gemini).
/// Uses WebView2 with persistent profiles for session management.
/// </summary>
public partial class FloatingBrowserWindow : Window
{
    private string _currentProvider = "ChatGPT";
    private Storyboard? _fadeInAnimation;
    private Storyboard? _fadeOutAnimation;
    private CoreWebView2Environment? _webViewEnvironment;
    
    // Track initialization state per provider
    private bool _chatGPTInitialized = false;
    private bool _geminiInitialized = false;
    private string _chatGPTUrl = "";
    private string _geminiUrl = "";
    
    // Track page load completion (not just navigation start)
    private bool _chatGPTPageReady = false;
    private bool _geminiPageReady = false;
    private TaskCompletionSource<bool>? _chatGPTPageLoadTcs;
    private TaskCompletionSource<bool>? _geminiPageLoadTcs;
    
    // Flag to prevent OnLoaded from repositioning during pre-initialization
    private bool _isPreInitializing = false;
    
    /// <summary>
    /// Gets whether the window is currently in pre-initialization mode (off-screen).
    /// </summary>
    public bool IsPreInitializing => _isPreInitializing;

    // Colors for different providers
    private static readonly Dictionary<string, (Color Start, Color End)> ProviderColors = new()
    {
        ["ChatGPT"] = (Color.FromRgb(16, 163, 127), Color.FromRgb(26, 127, 90)),   // Green
        ["Gemini"] = (Color.FromRgb(66, 133, 244), Color.FromRgb(52, 103, 194))    // Blue
    };

    public string CurrentProvider => _currentProvider;
    public string CurrentUrl => _currentProvider == "ChatGPT" ? _chatGPTUrl : _geminiUrl;
    
    /// <summary>
    /// Gets the currently active WebView2 control.
    /// </summary>
    private Microsoft.Web.WebView2.Wpf.WebView2 ActiveWebView => 
        _currentProvider == "ChatGPT" ? ChatGPTWebView : GeminiWebView;

    /// <summary>
    /// Event fired when the window is closing (to notify DictationBar).
    /// </summary>
    public event EventHandler? BrowserClosing;

    public FloatingBrowserWindow()
    {
        InitializeComponent();
        
        _fadeInAnimation = (Storyboard)Resources["FadeInAnimation"];
        _fadeOutAnimation = (Storyboard)Resources["FadeOutAnimation"];
        
        // Start invisible for fade-in
        Opacity = 0;
        
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Don't reposition during pre-initialization (window is off-screen intentionally)
        if (_isPreInitializing) return;
        
        // Position above DictationBar (bottom-center of screen)
        PositionAboveDictationBar();
    }

    /// <summary>
    /// Positions the window centered horizontally, above where DictationBar sits.
    /// </summary>
    public void PositionAboveDictationBar()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;
        var workArea = SystemParameters.WorkArea;
        
        // Center horizontally
        Left = (screenWidth - Width) / 2;
        
        // Position above DictationBar (which is at the bottom)
        // DictationBar is approximately 100px from bottom
        Top = workArea.Bottom - Height - 120;
    }

    /// <summary>
    /// Initializes WebView2 with the specified provider's profile.
    /// Uses a shared profile for all providers to allow instant switching.
    /// Pre-initializes all providers in background for instant first-use.
    /// </summary>
    public async Task InitializeAsync(string provider)
    {
        _currentProvider = provider;
        UpdateProviderUI();
        UpdateWebViewVisibility();
        
        try
        {
            ShowLoading(true);
            
            // Create shared environment if not exists
            if (_webViewEnvironment == null)
            {
                var profilePath = BrowserProfileManager.GetProfilePath();
                _webViewEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: profilePath);
            }
            
            // Initialize the requested provider first (for immediate use)
            await InitializeProviderWebViewAsync(provider);
            
            // Pre-initialize other providers in background for instant switching
            _ = PreInitializeAllProvidersAsync(provider);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to initialize browser: {ex.Message}\n\nPlease ensure WebView2 Runtime is installed.",
                "Browser Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Pre-initializes all providers in the background for instant first-use.
    /// </summary>
    private async Task PreInitializeAllProvidersAsync(string excludeProvider)
    {
        var providers = new[] { "ChatGPT", "Gemini" };
        
        foreach (var provider in providers)
        {
            if (provider == excludeProvider) continue;
            
            try
            {
                await InitializeProviderWebViewAsync(provider);
            }
            catch
            {
                // Silently ignore - will retry on actual use
            }
        }
    }

    /// <summary>
    /// Initializes a specific provider's WebView2 if not already initialized.
    /// Waits for the page to fully load before returning.
    /// </summary>
    private async Task InitializeProviderWebViewAsync(string provider)
    {
        var webView = provider == "ChatGPT" ? ChatGPTWebView : GeminiWebView;
        var isInitialized = provider == "ChatGPT" ? _chatGPTInitialized : _geminiInitialized;
        
        if (isInitialized) 
        {
            ShowLoading(false);
            return;
        }
        
        await webView.EnsureCoreWebView2Async(_webViewEnvironment);
        
        // Configure WebView2
        webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
        
        // Handle navigation events
        webView.CoreWebView2.NavigationStarting += (s, e) => OnNavigationStarting(provider, e);
        webView.CoreWebView2.NavigationCompleted += (s, e) => OnNavigationCompleted(provider, e);
        webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        
        // Create TaskCompletionSource to wait for page load
        var tcs = new TaskCompletionSource<bool>();
        if (provider == "ChatGPT")
            _chatGPTPageLoadTcs = tcs;
        else
            _geminiPageLoadTcs = tcs;
        
        // Navigate to provider home
        var url = BrowserProfileManager.GetProviderUrl(provider);
        webView.CoreWebView2.Navigate(url);
        
        // Mark as initialized (WebView is ready, navigation started)
        if (provider == "ChatGPT")
        {
            _chatGPTInitialized = true;
            _chatGPTUrl = url;
        }
        else
        {
            _geminiInitialized = true;
            _geminiUrl = url;
        }
        
        // Wait for navigation to complete (with timeout)
        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(15000)); // 15 second timeout
        }
        catch { }
    }

    /// <summary>
    /// Navigates to the provider's home page.
    /// </summary>
    public void NavigateHome()
    {
        var webView = ActiveWebView;
        if (webView.CoreWebView2 != null)
        {
            var url = BrowserProfileManager.GetProviderUrl(_currentProvider);
            webView.CoreWebView2.Navigate(url);
        }
    }

    /// <summary>
    /// Sets pre-initialization mode to prevent automatic repositioning.
    /// </summary>
    public void SetPreInitMode(bool isPreInit)
    {
        _isPreInitializing = isPreInit;
    }

    /// <summary>
    /// Switches to a different provider instantly by toggling WebView visibility.
    /// Initializes the provider's WebView if not already done.
    /// </summary>
    public async Task SwitchProviderAsync(string provider)
    {
        if (_currentProvider == provider) return;
        
        _currentProvider = provider;
        UpdateProviderUI();
        UpdateWebViewVisibility();
        
        // Initialize if needed (lazy loading)
        var isInitialized = provider == "ChatGPT" ? _chatGPTInitialized : _geminiInitialized;
        if (!isInitialized)
        {
            await InitializeProviderWebViewAsync(provider);
        }
    }

    /// <summary>
    /// Updates WebView visibility based on current provider.
    /// </summary>
    private void UpdateWebViewVisibility()
    {
        ChatGPTWebView.Visibility = _currentProvider == "ChatGPT" ? Visibility.Visible : Visibility.Collapsed;
        GeminiWebView.Visibility = _currentProvider == "Gemini" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Submits a query to the current provider.
    /// Does NOT navigate away - keeps the current chat session.
    /// </summary>
    public async Task NavigateAndQueryAsync(string? query = null)
    {
        var webView = ActiveWebView;
        var isInitialized = _currentProvider == "ChatGPT" ? _chatGPTInitialized : _geminiInitialized;
        
        if (!isInitialized || webView.CoreWebView2 == null) return;
        
        // Safety check: if page is still loading (e.g., refresh), wait briefly
        var isPageReady = _currentProvider == "ChatGPT" ? _chatGPTPageReady : _geminiPageReady;
        if (!isPageReady)
        {
            var tcs = _currentProvider == "ChatGPT" ? _chatGPTPageLoadTcs : _geminiPageLoadTcs;
            if (tcs != null)
            {
                await Task.WhenAny(tcs.Task, Task.Delay(5000));
            }
        }
        
        // Submit the query
        if (!string.IsNullOrEmpty(query))
        {
            await SubmitQueryAsync(query);
        }
    }

    /// <summary>
    /// Submits a query using JavaScript DOM injection.
    /// This is more reliable than clipboard paste as it directly manipulates the page.
    /// </summary>
    public async Task SubmitQueryAsync(string query)
    {
        var webView = ActiveWebView;
        var isInitialized = _currentProvider == "ChatGPT" ? _chatGPTInitialized : _geminiInitialized;
        
        if (!isInitialized || webView.CoreWebView2 == null) return;
        
        // Escape the query for JavaScript string
        var escapedQuery = EscapeForJavaScript(query);
        
        // Get provider-specific injection script
        var script = GetInjectionScript(_currentProvider, escapedQuery);
        
        try
        {
            var result = await webView.CoreWebView2.ExecuteScriptAsync(script);
            
            // If injection returned false (element not found), fall back to clipboard paste
            if (result == "false")
            {
                await FallbackClipboardPaste(query);
            }
        }
        catch
        {
            // If script execution fails, fall back to clipboard paste
            await FallbackClipboardPaste(query);
        }
    }

    /// <summary>
    /// Gets the JavaScript injection script for the specified provider.
    /// </summary>
    private static string GetInjectionScript(string provider, string escapedQuery)
    {
        return provider.ToLowerInvariant() switch
        {
            "chatgpt" => $@"
                (function() {{
                    // Try multiple selectors for ChatGPT's input field
                    const textarea = document.querySelector('#prompt-textarea') 
                                  || document.querySelector('textarea[data-id=""root""]')
                                  || document.querySelector('div[contenteditable=""true""]')
                                  || document.querySelector('textarea');
                    
                    if (!textarea) return false;
                    
                    // Handle contenteditable div vs textarea
                    if (textarea.contentEditable === 'true') {{
                        textarea.innerHTML = `{escapedQuery}`;
                        textarea.dispatchEvent(new InputEvent('input', {{ bubbles: true, data: `{escapedQuery}` }}));
                    }} else {{
                        textarea.value = `{escapedQuery}`;
                        textarea.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    }}
                    
                    // Focus the input
                    textarea.focus();
                    
                    // Try to click the send button after a short delay
                    setTimeout(() => {{
                        const sendBtn = document.querySelector('button[data-testid=""send-button""]')
                                     || document.querySelector('button[aria-label*=""Send""]')
                                     || document.querySelector('form button[type=""submit""]');
                        if (sendBtn && !sendBtn.disabled) {{
                            sendBtn.click();
                        }}
                    }}, 100);
                    
                    return true;
                }})();
            ",
            "gemini" => $@"
                (function() {{
                    // Gemini uses a rich-textarea with contenteditable paragraph
                    // Try multiple approaches to find the input
                    let input = null;
                    let isContentEditable = false;
                    
                    // Method 1: Look for the main prompt input area (contenteditable paragraph)
                    input = document.querySelector('div.ql-editor.textarea');
                    if (input) {{ isContentEditable = true; }}
                    
                    // Method 2: Look for contenteditable with specific class
                    if (!input) {{
                        input = document.querySelector('p[data-placeholder]');
                        if (input && input.isContentEditable) {{ isContentEditable = true; }}
                    }}
                    
                    // Method 3: Look for any contenteditable in the input area
                    if (!input) {{
                        input = document.querySelector('.input-area [contenteditable=""true""]')
                             || document.querySelector('.text-input-field [contenteditable=""true""]');
                        if (input) {{ isContentEditable = true; }}
                    }}
                    
                    // Method 4: Generic contenteditable or textarea
                    if (!input) {{
                        input = document.querySelector('[contenteditable=""true""]') 
                             || document.querySelector('textarea');
                        if (input && input.contentEditable === 'true') {{ isContentEditable = true; }}
                    }}
                    
                    if (!input) {{
                        console.log('Gemini: No input element found');
                        return false;
                    }}
                    
                    // Set the content
                    if (isContentEditable) {{
                        // For Gemini, set innerText to preserve formatting
                        input.innerText = `{escapedQuery}`;
                        input.dispatchEvent(new InputEvent('input', {{ bubbles: true, inputType: 'insertText' }}));
                    }} else {{
                        input.value = `{escapedQuery}`;
                        input.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    }}
                    
                    // Focus the input
                    input.focus();
                    
                    // Try to click the send button after a short delay
                    setTimeout(() => {{
                        // Gemini's send button selectors
                        const sendBtn = document.querySelector('button.send-button')
                                     || document.querySelector('button[aria-label*=""Send""]')
                                     || document.querySelector('button[data-test-id=""send-button""]')
                                     || document.querySelector('.send-button-container button')
                                     || document.querySelector('button[mat-icon-button] mat-icon[data-mat-icon-name=""send""]')?.closest('button');
                        
                        if (sendBtn && !sendBtn.disabled) {{
                            sendBtn.click();
                        }} else {{
                            // If no button found or disabled, try pressing Enter
                            input.dispatchEvent(new KeyboardEvent('keydown', {{ key: 'Enter', code: 'Enter', bubbles: true }}));
                        }}
                    }}, 150);
                    
                    return true;
                }})();
            ",
            _ => $@"
                (function() {{
                    // Generic fallback: try to find any text input
                    const input = document.querySelector('textarea') 
                               || document.querySelector('input[type=""text""]')
                               || document.querySelector('[contenteditable=""true""]');
                    
                    if (!input) return false;
                    
                    if (input.contentEditable === 'true') {{
                        input.innerHTML = `{escapedQuery}`;
                    }} else {{
                        input.value = `{escapedQuery}`;
                    }}
                    input.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    input.focus();
                    
                    return true;
                }})();
            "
        };
    }

    /// <summary>
    /// Escapes a string for safe inclusion in a JavaScript template literal.
    /// </summary>
    private static string EscapeForJavaScript(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        
        return text
            .Replace("\\", "\\\\")  // Escape backslashes first
            .Replace("`", "\\`")    // Escape template literal backticks
            .Replace("$", "\\$")    // Escape dollar signs (template literal interpolation)
            .Replace("\r\n", "\\n") // Normalize line endings
            .Replace("\r", "\\n")
            .Replace("\n", "\\n");
    }

    /// <summary>
    /// Fallback to clipboard paste if JavaScript injection fails.
    /// </summary>
    private async Task FallbackClipboardPaste(string query)
    {
        try
        {
            Clipboard.SetText(query);
        }
        catch
        {
            return; // Clipboard might be locked
        }
        
        ActiveWebView.Focus();
        await Task.Delay(300);
        SendCtrlV();
        await Task.Delay(200);
        SendEnter();
    }

    /// <summary>
    /// Shows the window with fade-in animation.
    /// </summary>
    public void ShowWithAnimation()
    {
        // Ensure we're starting from the right state
        Opacity = 0;
        
        PositionAboveDictationBar();
        Show();
        
        // Ensure window is brought to foreground and activated
        Activate();
        Topmost = true;
        
        _fadeInAnimation?.Begin(this);
    }

    /// <summary>
    /// Hides the window with fade-out animation.
    /// </summary>
    public async Task HideWithAnimationAsync()
    {
        if (_fadeOutAnimation != null)
        {
            _fadeOutAnimation.Begin(this);
            await Task.Delay(200); // Wait for animation
        }
        Hide();
    }

    private void UpdateProviderUI()
    {
        ProviderLabel.Text = _currentProvider;
        
        if (ProviderColors.TryGetValue(_currentProvider, out var colors))
        {
            var brush = new LinearGradientBrush(colors.Start, colors.End, 45);
            ProviderIcon.Fill = brush;
        }
    }

    private void ShowLoading(bool show)
    {
        LoadingText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavigationStarting(string provider, CoreWebView2NavigationStartingEventArgs e)
    {
        // Update the URL for the specific provider and reset page ready state
        if (provider == "ChatGPT")
        {
            _chatGPTUrl = e.Uri;
            _chatGPTPageReady = false;
            _chatGPTPageLoadTcs = new TaskCompletionSource<bool>();
        }
        else
        {
            _geminiUrl = e.Uri;
            _geminiPageReady = false;
            _geminiPageLoadTcs = new TaskCompletionSource<bool>();
        }
        
        // Only show loading for current provider
        if (provider == _currentProvider)
            ShowLoading(true);
    }

    private void OnNavigationCompleted(string provider, CoreWebView2NavigationCompletedEventArgs e)
    {
        // Only hide loading for current provider
        if (provider == _currentProvider)
            ShowLoading(false);
        
        // Mark page as ready and signal any waiters
        if (provider == "ChatGPT")
        {
            _chatGPTPageReady = true;
            _chatGPTPageLoadTcs?.TrySetResult(e.IsSuccess);
        }
        else
        {
            _geminiPageReady = true;
            _geminiPageLoadTcs?.TrySetResult(e.IsSuccess);
        }
    }

    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // All popups open in a new popup window (critical for OAuth flows)
        if (_webViewEnvironment == null)
        {
            e.Handled = true;
            return;
        }

        // Get a deferral to handle this asynchronously
        var deferral = e.GetDeferral();
        
        try
        {
            // Create popup window with shared environment for cookie/session sharing
            var popupWindow = new BrowserPopupWindow(_webViewEnvironment);
            popupWindow.Show();
            
            // Wait for the popup's WebView2 to initialize
            await popupWindow.WebViewControl.EnsureCoreWebView2Async(_webViewEnvironment);
            
            // Direct the new window content to the popup's WebView2
            e.NewWindow = popupWindow.WebViewControl.CoreWebView2;
            e.Handled = true;
        }
        catch
        {
            e.Handled = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Double-click to reset size
            Width = 600;
            Height = 700;
            PositionAboveDictationBar();
        }
        else
        {
            DragMove();
        }
    }

    private void OpenExternalButton_Click(object sender, RoutedEventArgs e)
    {
        var currentUrl = CurrentUrl;
        if (!string.IsNullOrEmpty(currentUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = currentUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private async void SwitchAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will sign you out of all AI providers. Continue?",
            "Switch Account",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        
        if (result == MessageBoxResult.Yes)
        {
            // Clear shared profile and reinitialize both
            BrowserProfileManager.ClearProfile();
            _chatGPTInitialized = false;
            _geminiInitialized = false;
            _chatGPTPageReady = false;
            _geminiPageReady = false;
            await InitializeAsync(_currentProvider);
        }
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        BrowserClosing?.Invoke(this, EventArgs.Empty);
        await HideWithAnimationAsync();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Don't actually close, just hide
        e.Cancel = true;
        BrowserClosing?.Invoke(this, EventArgs.Empty);
        Hide();
    }

    #region Native Methods for Keyboard Input

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_RETURN = 0x0D;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int INPUT_KEYBOARD = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private static INPUT CreateKeyInput(ushort vk, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };
    }

    private static void SendCtrlV()
    {
        var inputs = new INPUT[4];
        inputs[0] = CreateKeyInput(VK_CONTROL, false);
        inputs[1] = CreateKeyInput(VK_V, false);
        inputs[2] = CreateKeyInput(VK_V, true);
        inputs[3] = CreateKeyInput(VK_CONTROL, true);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendEnter()
    {
        var inputs = new INPUT[2];
        inputs[0] = CreateKeyInput(VK_RETURN, false);
        inputs[1] = CreateKeyInput(VK_RETURN, true);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    #endregion
}
