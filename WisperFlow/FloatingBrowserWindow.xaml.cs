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
    private bool _notionInitialized = false;
    private string _chatGPTUrl = "";
    private string _geminiUrl = "";
    private string _notionUrl = "";
    
    // Track page load completion (not just navigation start)
    private bool _chatGPTPageReady = false;
    private bool _geminiPageReady = false;
    private bool _notionPageReady = false;
    private bool _googleTasksPageReady = false;
    private TaskCompletionSource<bool>? _chatGPTPageLoadTcs;
    private TaskCompletionSource<bool>? _geminiPageLoadTcs;
    private TaskCompletionSource<bool>? _notionPageLoadTcs;
    private TaskCompletionSource<bool>? _googleTasksPageLoadTcs;
    
    // Notion OAuth state
    private bool _notionOAuthTriggered = false;
    
    // Google Tasks state
    private bool _googleTasksInitialized = false;
    private string _googleTasksUrl = "";
    private bool _googleTasksOAuthTriggered = false;
    
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
        ["Gemini"] = (Color.FromRgb(66, 133, 244), Color.FromRgb(52, 103, 194)),   // Blue
        ["Notion"] = (Color.FromRgb(0, 0, 0), Color.FromRgb(55, 55, 55)),          // Black
        ["GoogleTasks"] = (Color.FromRgb(66, 133, 244), Color.FromRgb(52, 103, 194))  // Google Blue
    };

    public string CurrentProvider => _currentProvider;
    public string CurrentUrl => _currentProvider switch
    {
        "ChatGPT" => _chatGPTUrl,
        "Gemini" => _geminiUrl,
        "Notion" => _notionUrl,
        "GoogleTasks" => _googleTasksUrl,
        _ => ""
    };
    
    /// <summary>
    /// Gets the currently active WebView2 control.
    /// </summary>
    private Microsoft.Web.WebView2.Wpf.WebView2 ActiveWebView => _currentProvider switch
    {
        "ChatGPT" => ChatGPTWebView,
        "Gemini" => GeminiWebView,
        "Notion" => NotionWebView,
        "GoogleTasks" => GoogleTasksWebView,
        _ => ChatGPTWebView
    };

    /// <summary>
    /// Event fired when the window is closing (to notify DictationBar).
    /// </summary>
    public event EventHandler? BrowserClosing;
    
    // Resize handling
    private bool _isResizing = false;
    private string _resizeDirection = "";
    private Point _resizeStartPoint;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private double _resizeStartLeft;
    private double _resizeStartTop;

    public FloatingBrowserWindow()
    {
        InitializeComponent();
        
        _fadeInAnimation = (Storyboard)Resources["FadeInAnimation"];
        _fadeOutAnimation = (Storyboard)Resources["FadeOutAnimation"];
        
        // Start invisible for fade-in
        Opacity = 0;
        
        Loaded += OnLoaded;
        
        // Handle resize mouse events
        MouseMove += Window_MouseMove;
        MouseLeftButtonUp += Window_MouseLeftButtonUp;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Don't reposition during pre-initialization (window is off-screen intentionally)
        if (_isPreInitializing) return;
        
        // Position above DictationBar (bottom-center of screen)
        PositionAboveDictationBar();
    }

    /// <summary>
    /// Positions the window above where DictationBar sits.
    /// </summary>
    /// <param name="dictationBarCenterX">Optional X coordinate of DictationBar center for horizontal alignment. If -1, center on screen.</param>
    public void PositionAboveDictationBar(double dictationBarCenterX = -1)
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var workArea = SystemParameters.WorkArea;
        
        // Calculate horizontal position
        if (dictationBarCenterX >= 0)
        {
            // Center the browser on the DictationBar's center X
            Left = dictationBarCenterX - (Width / 2);
        }
        else
        {
            // Default: center on screen
            Left = (screenWidth - Width) / 2;
        }
        
        // Clamp to screen bounds
        Left = Math.Max(0, Math.Min(screenWidth - Width, Left));
        
        // Position above DictationBar (which is at the bottom)
        // DictationBar is approximately 50px from bottom
        Top = workArea.Bottom - Height - 50;
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
        var webView = provider switch
        {
            "ChatGPT" => ChatGPTWebView,
            "Gemini" => GeminiWebView,
            "Notion" => NotionWebView,
            "GoogleTasks" => GoogleTasksWebView,
            _ => ChatGPTWebView
        };
        
        var isInitialized = provider switch
        {
            "ChatGPT" => _chatGPTInitialized,
            "Gemini" => _geminiInitialized,
            "Notion" => _notionInitialized,
            "GoogleTasks" => _googleTasksInitialized,
            _ => false
        };
        
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
        
        // Set mobile User-Agent for Notion and GoogleTasks to get mobile-friendly UI
        if (provider is "Notion" or "GoogleTasks")
        {
            webView.CoreWebView2.Settings.UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1";
        }
        
        // Handle navigation events
        webView.CoreWebView2.NavigationStarting += (s, e) => OnNavigationStarting(provider, e);
        webView.CoreWebView2.NavigationCompleted += (s, e) => OnNavigationCompleted(provider, e);
        webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        
        // Create TaskCompletionSource to wait for page load
        var tcs = new TaskCompletionSource<bool>();
        switch (provider)
        {
            case "ChatGPT": _chatGPTPageLoadTcs = tcs; break;
            case "Gemini": _geminiPageLoadTcs = tcs; break;
            case "Notion": _notionPageLoadTcs = tcs; break;
            case "GoogleTasks": _googleTasksPageLoadTcs = tcs; break;
        }
        
        // Navigate to provider home (Notion/GoogleTasks start at login)
        var url = provider switch
        {
            "Notion" => "https://www.notion.so/login",
            "GoogleTasks" => "https://tasks.google.com",
            _ => BrowserProfileManager.GetProviderUrl(provider)
        };
        webView.CoreWebView2.Navigate(url);
        
        // Mark as initialized
        switch (provider)
        {
            case "ChatGPT":
                _chatGPTInitialized = true;
                _chatGPTUrl = url;
                break;
            case "Gemini":
                _geminiInitialized = true;
                _geminiUrl = url;
                break;
            case "Notion":
                _notionInitialized = true;
                _notionUrl = url;
                _notionOAuthTriggered = false;
                break;
            case "GoogleTasks":
                _googleTasksInitialized = true;
                _googleTasksUrl = url;
                _googleTasksOAuthTriggered = false;
                break;
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
        NotionWebView.Visibility = _currentProvider == "Notion" ? Visibility.Visible : Visibility.Collapsed;
        GoogleTasksWebView.Visibility = _currentProvider == "GoogleTasks" ? Visibility.Visible : Visibility.Collapsed;
    }
    
    /// <summary>
    /// Navigates the current provider's WebView to the specified URL.
    /// Ensures WebView2 is initialized first.
    /// </summary>
    public async Task NavigateToUrlAsync(string url)
    {
        // Ensure WebView2 is initialized
        if (ActiveWebView.CoreWebView2 == null)
        {
            await ActiveWebView.EnsureCoreWebView2Async(_webViewEnvironment);
        }
        
        if (ActiveWebView?.CoreWebView2 != null)
        {
            ActiveWebView.CoreWebView2.Navigate(url);
        }
    }

    /// <summary>
    /// Submits a query to the current provider.
    /// Does NOT navigate away - keeps the current chat session.
    /// </summary>
    /// <param name="query">The query text to submit.</param>
    /// <param name="screenshotBytes">Optional PNG screenshot bytes to attach.</param>
    public async Task NavigateAndQueryAsync(string? query = null, byte[]? screenshotBytes = null)
    {
        System.Diagnostics.Debug.WriteLine($"[NavigateAndQueryAsync] Called with query='{query?.Substring(0, Math.Min(50, query?.Length ?? 0))}'... screenshotBytes={screenshotBytes?.Length ?? 0} bytes");

        var webView = ActiveWebView;
        var isInitialized = _currentProvider == "ChatGPT" ? _chatGPTInitialized : _geminiInitialized;

        System.Diagnostics.Debug.WriteLine($"[NavigateAndQueryAsync] Provider={_currentProvider}, Initialized={isInitialized}, WebViewReady={webView?.CoreWebView2 != null}");

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
        
        // Upload screenshot if provided
        if (screenshotBytes != null && screenshotBytes.Length > 0)
        {
            System.Diagnostics.Debug.WriteLine($"[NavigateAndQueryAsync] Uploading screenshot");
            System.Diagnostics.Debug.WriteLine($"[NavigateAndQueryAsync] Screenshot size: {screenshotBytes.Length} bytes");
            await UploadScreenshotAsync(screenshotBytes);
            System.Diagnostics.Debug.WriteLine($"[NavigateAndQueryAsync] Screenshot upload completed");
            
            // For Gemini: Wait for image upload to complete before submitting
            // Gemini shows .image-preview.loading while uploading, we must wait for it to finish
            if (_currentProvider.ToLowerInvariant() == "gemini")
            {
                System.Diagnostics.Debug.WriteLine("[NavigateAndQueryAsync] Waiting for Gemini image upload to complete...");
                await WaitForGeminiImageUploadAsync();
                System.Diagnostics.Debug.WriteLine("[NavigateAndQueryAsync] Gemini image upload ready");
            }
        }
        
        // Submit the query
        if (!string.IsNullOrEmpty(query))
        {
            await SubmitQueryAsync(query);
        }
    }
    
    /// <summary>
    /// Uploads a screenshot to the current AI provider's chat.
    /// Uses JavaScript injection: file input for ChatGPT, ClipboardEvent paste for Gemini.
    /// </summary>
    private async Task UploadScreenshotAsync(byte[] screenshotBytes)
    {
        var webView = ActiveWebView;
        if (webView?.CoreWebView2 == null)
        {
            System.Diagnostics.Debug.WriteLine("[UploadScreenshot] WebView not ready, aborting");
            return;
        }

        var provider = _currentProvider.ToLowerInvariant();
        var base64Image = Convert.ToBase64String(screenshotBytes);
        System.Diagnostics.Debug.WriteLine($"[UploadScreenshot] Starting for {provider}, {screenshotBytes.Length} bytes");

        string script;

        if (provider == "gemini")
        {
            // Gemini: Use ClipboardEvent paste on .ql-editor (verified working)
            script = $@"
                (function() {{
                    try {{
                        console.log('Gemini: Using ClipboardEvent paste');

                        // Find Gemini's Quill editor
                        const input = document.querySelector('.ql-editor') || 
                                      document.querySelector('rich-textarea') || 
                                      document.querySelector('[contenteditable=""true""]');

                        if (!input) {{
                            console.log('No Gemini input found');
                            return 'no-input-found';
                        }}

                        console.log('Found Gemini input:', input.tagName, input.className);
                        input.focus();
                        input.click();

                        // Convert base64 to File
                        const base64Data = '{base64Image}';
                        const byteCharacters = atob(base64Data);
                        const byteArray = new Uint8Array(byteCharacters.length);
                        for (let i = 0; i < byteCharacters.length; i++) {{
                            byteArray[i] = byteCharacters.charCodeAt(i);
                        }}
                        const blob = new Blob([byteArray], {{ type: 'image/png' }});
                        const file = new File([blob], 'screenshot.png', {{ type: 'image/png' }});

                        // Create DataTransfer and dispatch ClipboardEvent
                        const dataTransfer = new DataTransfer();
                        dataTransfer.items.add(file);

                        const pasteEvent = new ClipboardEvent('paste', {{
                            bubbles: true,
                            cancelable: true,
                            clipboardData: dataTransfer
                        }});

                        input.dispatchEvent(pasteEvent);
                        console.log('Gemini paste event dispatched');

                        return 'paste-dispatched';
                    }} catch (e) {{
                        console.error('Gemini upload error:', e);
                        return 'error: ' + e.message;
                    }}
                }})();
            ";
        }
        else
        {
            // ChatGPT: Use file input approach (verified working)
            script = $@"
                (function() {{
                    try {{
                        console.log('ChatGPT: Using file input');

                        // Convert base64 to File
                        const base64Data = '{base64Image}';
                        const byteCharacters = atob(base64Data);
                        const byteNumbers = new Array(byteCharacters.length);
                        for (let i = 0; i < byteCharacters.length; i++) {{
                            byteNumbers[i] = byteCharacters.charCodeAt(i);
                        }}
                        const byteArray = new Uint8Array(byteNumbers);
                        const blob = new Blob([byteArray], {{ type: 'image/png' }});
                        const file = new File([blob], 'screenshot.png', {{ type: 'image/png' }});

                        // Find ChatGPT file input
                        const fileInput = document.querySelector('input[type=""file""]')
                                       || document.querySelector('input[accept*=""image""]')
                                       || document.querySelector('input[data-testid*=""file""]');

                        if (fileInput) {{
                            console.log('ChatGPT: Found file input:', fileInput);

                            const dataTransfer = new DataTransfer();
                            dataTransfer.items.add(file);
                            fileInput.files = dataTransfer.files;

                            fileInput.dispatchEvent(new Event('change', {{ bubbles: true, cancelable: true }}));
                            fileInput.dispatchEvent(new Event('input', {{ bubbles: true }}));

                            return 'success';
                        }}

                        console.log('No file input found');
                        return 'no-input-found';
                    }} catch (e) {{
                        console.error('ChatGPT upload error:', e);
                        return 'error: ' + e.message;
                    }}
                }})();
            ";
        }

        var result = await webView.CoreWebView2.ExecuteScriptAsync(script);
        System.Diagnostics.Debug.WriteLine($"[UploadScreenshot] {provider} result: {result}");

        await Task.Delay(2000);
    }

    /// <summary>
    /// Simple logging method that writes to log.txt file
    /// </summary>
    private static void LogToFile(string message)
    {
        try
        {
            string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
            string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [ImageUpload] {message}{Environment.NewLine}";
            System.IO.File.AppendAllText(logPath, logEntry);
        }
        catch
        {
            // Ignore logging errors
        }
    }

    /// <summary>
    /// Waits for Gemini to finish processing/uploading an image.
    /// Gemini shows .image-preview.loading while uploading; we wait until it disappears.
    /// </summary>
    private async Task WaitForGeminiImageUploadAsync(int timeoutMs = 10000)
    {
        var webView = ActiveWebView;
        if (webView?.CoreWebView2 == null) return;

        var startTime = DateTime.UtcNow;
        var checkScript = @"
            (function() {
                // Check if there's an image preview that's still loading
                const loadingPreview = document.querySelector('.image-preview.loading');
                const anyPreview = document.querySelector('.image-preview');
                
                if (loadingPreview) {
                    return 'loading';
                } else if (anyPreview) {
                    return 'ready';
                } else {
                    return 'no-preview';
                }
            })();
        ";

        // First, wait a bit for the preview to appear
        await Task.Delay(500);

        // Then poll until loading is complete or timeout
        while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
        {
            try
            {
                var result = await webView.CoreWebView2.ExecuteScriptAsync(checkScript);
                var status = result.Trim('"');
                
                System.Diagnostics.Debug.WriteLine($"[WaitForGeminiImageUpload] Status: {status}");
                LogToFile($"[WaitForGeminiImageUpload] Status: {status}");

                if (status == "ready")
                {
                    // Image is fully uploaded
                    return;
                }
                else if (status == "no-preview")
                {
                    // No preview appeared, might not have uploaded - wait a bit more
                    if ((DateTime.UtcNow - startTime).TotalMilliseconds > 2000)
                    {
                        // If nothing appeared after 2 seconds, continue anyway
                        System.Diagnostics.Debug.WriteLine("[WaitForGeminiImageUpload] No preview found, continuing");
                        return;
                    }
                }
                // status == "loading" - continue waiting
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WaitForGeminiImageUpload] Error: {ex.Message}");
            }

            await Task.Delay(200);
        }

        System.Diagnostics.Debug.WriteLine("[WaitForGeminiImageUpload] Timeout reached");
        LogToFile("[WaitForGeminiImageUpload] Timeout - continuing anyway");
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
    /// <param name="dictationBarCenterX">Optional X coordinate of DictationBar center for horizontal alignment. If -1, center on screen.</param>
    public void ShowWithAnimation(double dictationBarCenterX = -1)
    {
        // Ensure we're starting from the right state
        Opacity = 0;
        
        PositionAboveDictationBar(dictationBarCenterX);
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

    private async void OnNavigationStarting(string provider, CoreWebView2NavigationStartingEventArgs e)
    {
        // Update the URL for the specific provider and reset page ready state
        switch (provider)
        {
            case "ChatGPT":
                _chatGPTUrl = e.Uri;
                _chatGPTPageReady = false;
                _chatGPTPageLoadTcs = new TaskCompletionSource<bool>();
                break;
            case "Gemini":
                _geminiUrl = e.Uri;
                _geminiPageReady = false;
                _geminiPageLoadTcs = new TaskCompletionSource<bool>();
                break;
            case "Notion":
                _notionUrl = e.Uri;
                _notionPageReady = false;
                _notionPageLoadTcs = new TaskCompletionSource<bool>();
                
                // Intercept OAuth callback redirect
                if (e.Uri.StartsWith("https://localhost/callback", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    await HandleNotionOAuthCallbackAsync(e.Uri);
                    return;
                }
                break;
            case "GoogleTasks":
                _googleTasksUrl = e.Uri;
                _googleTasksPageReady = false;
                _googleTasksPageLoadTcs = new TaskCompletionSource<bool>();
                
                // Intercept OAuth callback redirect
                if (e.Uri.StartsWith("https://localhost/callback", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    await HandleGoogleTasksOAuthCallbackAsync(e.Uri);
                    return;
                }
                break;
        }
        
        // Only show loading for current provider
        if (provider == _currentProvider)
            ShowLoading(true);
    }
    
    /// <summary>
    /// Handles Notion OAuth callback - extracts code and exchanges for token.
    /// </summary>
    private async Task HandleNotionOAuthCallbackAsync(string callbackUrl)
    {
        try
        {
            var notionProvider = Services.NoteProviders.NoteProviderRegistry.Get("Notion") 
                as Services.NoteProviders.NotionNoteProvider;
            
            if (notionProvider == null) return;
            
            var code = notionProvider.ExtractAuthCode(callbackUrl);
            if (string.IsNullOrEmpty(code)) return;
            
            // Exchange code for token
            var success = await notionProvider.ExchangeCodeAsync(code);
            
            // Navigate back to Notion dashboard
            if (success && NotionWebView?.CoreWebView2 != null)
            {
                NotionWebView.CoreWebView2.Navigate("https://www.notion.so");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Notion OAuth error: {ex.Message}");
        }
    }

    private void OnNavigationCompleted(string provider, CoreWebView2NavigationCompletedEventArgs e)
    {
        // Only hide loading for current provider
        if (provider == _currentProvider)
            ShowLoading(false);
        
        // Mark page as ready and signal any waiters
        switch (provider)
        {
            case "ChatGPT":
                _chatGPTPageReady = true;
                _chatGPTPageLoadTcs?.TrySetResult(e.IsSuccess);
                break;
            case "Gemini":
                _geminiPageReady = true;
                _geminiPageLoadTcs?.TrySetResult(e.IsSuccess);
                break;
            case "Notion":
                _notionPageReady = true;
                _notionPageLoadTcs?.TrySetResult(e.IsSuccess);
                
                // Check if user is on dashboard (logged in) and OAuth not yet triggered
                if (!_notionOAuthTriggered && IsNotionDashboard(_notionUrl))
                {
                    TriggerNotionOAuth();
                }
                break;
            case "GoogleTasks":
                _googleTasksPageReady = true;
                _googleTasksPageLoadTcs?.TrySetResult(e.IsSuccess);
                
                // Check if user is on dashboard (logged in) and OAuth not yet triggered
                if (!_googleTasksOAuthTriggered && IsGoogleTasksDashboard(_googleTasksUrl))
                {
                    TriggerGoogleTasksOAuth();
                }
                break;
        }
    }
    
    /// <summary>
    /// Checks if URL is Notion dashboard (user is logged in).
    /// </summary>
    private bool IsNotionDashboard(string url)
    {
        var notionProvider = Services.NoteProviders.NoteProviderRegistry.Get("Notion") 
            as Services.NoteProviders.NotionNoteProvider;
        return notionProvider?.IsDashboardUrl(url) ?? false;
    }
    
    /// <summary>
    /// Triggers the Notion OAuth authorization flow.
    /// </summary>
    private void TriggerNotionOAuth()
    {
        var notionProvider = Services.NoteProviders.NoteProviderRegistry.Get("Notion") 
            as Services.NoteProviders.NotionNoteProvider;
        
        if (notionProvider == null) return;
        
        // If already authenticated, no need to trigger OAuth
        if (notionProvider.IsAuthenticated)
        {
            _notionOAuthTriggered = true;
            return;
        }
        
        var authUrl = notionProvider.GetAuthorizationUrl();
        if (string.IsNullOrEmpty(authUrl)) 
        {
            // OAuth not configured - show message
            System.Diagnostics.Debug.WriteLine("Notion OAuth not configured. Please set Client ID and Secret.");
            return;
        }
        
        _notionOAuthTriggered = true;
        NotionWebView?.CoreWebView2?.Navigate(authUrl);
    }
    
    /// <summary>
    /// Handles Google Tasks OAuth callback - extracts code and exchanges for token.
    /// </summary>
    private async Task HandleGoogleTasksOAuthCallbackAsync(string callbackUrl)
    {
        try
        {
            var googleTasksProvider = Services.NoteProviders.NoteProviderRegistry.Get("GoogleTasks") 
                as Services.NoteProviders.GoogleTasksNoteProvider;
            
            if (googleTasksProvider == null) return;
            
            var code = googleTasksProvider.ExtractAuthCode(callbackUrl);
            if (string.IsNullOrEmpty(code)) return;
            
            // Exchange code for token
            var success = await googleTasksProvider.ExchangeCodeAsync(code);
            
            // Navigate back to Google Tasks
            if (success && GoogleTasksWebView?.CoreWebView2 != null)
            {
                GoogleTasksWebView.CoreWebView2.Navigate("https://tasks.google.com");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Google Tasks OAuth error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Checks if URL is Google Tasks dashboard (user is logged in).
    /// </summary>
    private bool IsGoogleTasksDashboard(string url)
    {
        var googleTasksProvider = Services.NoteProviders.NoteProviderRegistry.Get("GoogleTasks") 
            as Services.NoteProviders.GoogleTasksNoteProvider;
        return googleTasksProvider?.IsDashboardUrl(url) ?? false;
    }
    
    /// <summary>
    /// Triggers the Google Tasks OAuth authorization flow.
    /// </summary>
    private void TriggerGoogleTasksOAuth()
    {
        var googleTasksProvider = Services.NoteProviders.NoteProviderRegistry.Get("GoogleTasks") 
            as Services.NoteProviders.GoogleTasksNoteProvider;
        
        if (googleTasksProvider == null) return;
        
        // If already authenticated, no need to trigger OAuth
        if (googleTasksProvider.IsAuthenticated)
        {
            _googleTasksOAuthTriggered = true;
            return;
        }
        
        var authUrl = googleTasksProvider.GetAuthorizationUrl();
        if (string.IsNullOrEmpty(authUrl)) 
        {
            System.Diagnostics.Debug.WriteLine("Google Tasks OAuth not configured. Please set Client ID and Secret.");
            return;
        }
        
        _googleTasksOAuthTriggered = true;
        GoogleTasksWebView?.CoreWebView2?.Navigate(authUrl);
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
    
    private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            _isResizing = true;
            _resizeDirection = element.Name;
            _resizeStartPoint = PointToScreen(e.GetPosition(this));
            _resizeStartWidth = Width;
            _resizeStartHeight = Height;
            _resizeStartLeft = Left;
            _resizeStartTop = Top;
            element.CaptureMouse();
            e.Handled = true;
        }
    }
    
    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing) return;
        
        var currentPoint = PointToScreen(e.GetPosition(this));
        var deltaX = currentPoint.X - _resizeStartPoint.X;
        var deltaY = currentPoint.Y - _resizeStartPoint.Y;
        
        double newWidth = _resizeStartWidth;
        double newHeight = _resizeStartHeight;
        double newLeft = _resizeStartLeft;
        double newTop = _resizeStartTop;
        
        switch (_resizeDirection)
        {
            case "ResizeTopLeft":
                newWidth = Math.Max(MinWidth, _resizeStartWidth - deltaX);
                newHeight = Math.Max(MinHeight, _resizeStartHeight - deltaY);
                newLeft = _resizeStartLeft + (_resizeStartWidth - newWidth);
                newTop = _resizeStartTop + (_resizeStartHeight - newHeight);
                break;
            case "ResizeTopRight":
                newWidth = Math.Max(MinWidth, _resizeStartWidth + deltaX);
                newHeight = Math.Max(MinHeight, _resizeStartHeight - deltaY);
                newTop = _resizeStartTop + (_resizeStartHeight - newHeight);
                break;
            case "ResizeBottomLeft":
                newWidth = Math.Max(MinWidth, _resizeStartWidth - deltaX);
                newHeight = Math.Max(MinHeight, _resizeStartHeight + deltaY);
                newLeft = _resizeStartLeft + (_resizeStartWidth - newWidth);
                break;
            case "ResizeBottomRight":
                newWidth = Math.Max(MinWidth, _resizeStartWidth + deltaX);
                newHeight = Math.Max(MinHeight, _resizeStartHeight + deltaY);
                break;
            case "ResizeTop":
                newHeight = Math.Max(MinHeight, _resizeStartHeight - deltaY);
                newTop = _resizeStartTop + (_resizeStartHeight - newHeight);
                break;
            case "ResizeBottom":
                newHeight = Math.Max(MinHeight, _resizeStartHeight + deltaY);
                break;
            case "ResizeLeft":
                newWidth = Math.Max(MinWidth, _resizeStartWidth - deltaX);
                newLeft = _resizeStartLeft + (_resizeStartWidth - newWidth);
                break;
            case "ResizeRight":
                newWidth = Math.Max(MinWidth, _resizeStartWidth + deltaX);
                break;
        }
        
        Width = newWidth;
        Height = newHeight;
        Left = newLeft;
        Top = newTop;
    }
    
    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isResizing)
        {
            _isResizing = false;
            Mouse.Capture(null);
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
