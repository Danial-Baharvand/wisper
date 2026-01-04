using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WisperFlow.Services;

namespace WisperFlow;

/// <summary>
/// Unified floating container window that hosts both the browser and transcript.
/// Features:
/// - Rounded corners (16px radius) on all sides
/// - Invisible resize handles on all four corners
/// - Floating "Open in Browser" button outside the window
/// - Transcript section that expands to push browser up
/// - No title bar or close button
/// </summary>
public partial class FloatingContainerWindow : Window
{
    private string _currentProvider = "ChatGPT";
    private Storyboard? _fadeInAnimation;
    private Storyboard? _fadeOutAnimation;
    private Storyboard? _transcriptExpandAnimation;
    private Storyboard? _cursorBlinkAnimation;
    private CoreWebView2Environment? _webViewEnvironment;
    
    // Track initialization state per provider
    private bool _chatGPTInitialized = false;
    private bool _geminiInitialized = false;
    private string _chatGPTUrl = "";
    private string _geminiUrl = "";
    
    // Track page load completion
    private bool _chatGPTPageReady = false;
    private bool _geminiPageReady = false;
    private TaskCompletionSource<bool>? _chatGPTPageLoadTcs;
    private TaskCompletionSource<bool>? _geminiPageLoadTcs;
    
    // Pre-initialization flag
    private bool _isPreInitializing = false;
    
    // Transcript state
    private DispatcherTimer? _matrixTimer;
    private string _committedText = "";
    private string _currentInterim = "";
    private string _targetText = "";
    private int _matrixIterations;
    private Random _random = new();
    private bool _isTranscriptVisible;
    private bool _isMatrixAnimating;
    
    private const string MatrixChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789@#$%&*+=<>?";
    
    // Resize tracking
    private bool _isResizing = false;
    private Point _resizeStartPoint;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private double _resizeStartLeft;
    private double _resizeStartTop;
    private string _resizeDirection = "";
    
    // Win32 constants for non-activating window
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // Provider colors
    private static readonly Dictionary<string, (Color Start, Color End)> ProviderColors = new()
    {
        ["ChatGPT"] = (Color.FromRgb(16, 163, 127), Color.FromRgb(26, 127, 90)),
        ["Gemini"] = (Color.FromRgb(66, 133, 244), Color.FromRgb(52, 103, 194))
    };

    public string CurrentProvider => _currentProvider;
    public string CurrentUrl => _currentProvider == "ChatGPT" ? _chatGPTUrl : _geminiUrl;
    public bool IsPreInitializing => _isPreInitializing;

    private Microsoft.Web.WebView2.Wpf.WebView2 ActiveWebView =>
        _currentProvider == "ChatGPT" ? ChatGPTWebView : GeminiWebView;

    /// <summary>
    /// Event fired when the window is closing (to notify DictationBar).
    /// </summary>
    public event EventHandler? BrowserClosing;

    public FloatingContainerWindow()
    {
        InitializeComponent();
        
        _fadeInAnimation = (Storyboard)Resources["FadeInAnimation"];
        _fadeOutAnimation = (Storyboard)Resources["FadeOutAnimation"];
        _transcriptExpandAnimation = (Storyboard)Resources["TranscriptExpandAnimation"];
        _cursorBlinkAnimation = (Storyboard)Resources["CursorBlinkAnimation"];
        
        // Start invisible for fade-in
        Opacity = 0;
        
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        
        // Hook up resize mouse events at window level
        MouseMove += OnWindowMouseMove;
        MouseLeftButtonUp += OnWindowMouseLeftButtonUp;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isPreInitializing) return;
        PositionAboveDictationBar();
        UpdateBrowserClipGeometry();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateBrowserClipGeometry();
    }

    private void UpdateBrowserClipGeometry()
    {
        if (BrowserBorder.ActualWidth > 0 && BrowserBorder.ActualHeight > 0)
        {
            BrowserClipGeometry.Rect = new Rect(0, 0, BrowserBorder.ActualWidth, BrowserBorder.ActualHeight);
        }
    }

    /// <summary>
    /// Positions the window centered horizontally, lower on screen (similar to FloatingTranscriptWindow position).
    /// </summary>
    public void PositionAboveDictationBar()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var workArea = SystemParameters.WorkArea;
        
        // Center horizontally
        Left = (screenWidth - Width) / 2;
        
        // Position lower on screen - above DictationBar with more room for transcript
        Top = workArea.Bottom - Height - 180;
    }

    #region WebView2 Initialization

    /// <summary>
    /// Initializes WebView2 with the specified provider's profile.
    /// </summary>
    public async Task InitializeAsync(string provider)
    {
        _currentProvider = provider;
        UpdateWebViewVisibility();
        
        try
        {
            ShowLoading(true);
            
            if (_webViewEnvironment == null)
            {
                var profilePath = BrowserProfileManager.GetProfilePath();
                _webViewEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: profilePath);
            }
            
            await InitializeProviderWebViewAsync(provider);
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
            catch { }
        }
    }

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
        
        webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        webView.CoreWebView2.Settings.IsZoomControlEnabled = true;
        
        webView.CoreWebView2.NavigationStarting += (s, e) => OnNavigationStarting(provider, e);
        webView.CoreWebView2.NavigationCompleted += (s, e) => OnNavigationCompleted(provider, e);
        webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        
        var tcs = new TaskCompletionSource<bool>();
        if (provider == "ChatGPT")
            _chatGPTPageLoadTcs = tcs;
        else
            _geminiPageLoadTcs = tcs;
        
        var url = BrowserProfileManager.GetProviderUrl(provider);
        webView.CoreWebView2.Navigate(url);
        
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
        
        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(15000));
        }
        catch { }
    }

    public void NavigateHome()
    {
        var webView = ActiveWebView;
        if (webView.CoreWebView2 != null)
        {
            var url = BrowserProfileManager.GetProviderUrl(_currentProvider);
            webView.CoreWebView2.Navigate(url);
        }
    }

    public void SetPreInitMode(bool isPreInit)
    {
        _isPreInitializing = isPreInit;
    }

    public async Task SwitchProviderAsync(string provider)
    {
        if (_currentProvider == provider) return;
        
        _currentProvider = provider;
        UpdateWebViewVisibility();
        
        var isInitialized = provider == "ChatGPT" ? _chatGPTInitialized : _geminiInitialized;
        if (!isInitialized)
        {
            await InitializeProviderWebViewAsync(provider);
        }
    }

    private void UpdateWebViewVisibility()
    {
        ChatGPTWebView.Visibility = _currentProvider == "ChatGPT" ? Visibility.Visible : Visibility.Collapsed;
        GeminiWebView.Visibility = _currentProvider == "Gemini" ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    #region Query and Screenshot Methods

    public async Task NavigateAndQueryAsync(string? query = null, byte[]? screenshotBytes = null)
    {
        var webView = ActiveWebView;
        var isInitialized = _currentProvider == "ChatGPT" ? _chatGPTInitialized : _geminiInitialized;

        if (!isInitialized || webView.CoreWebView2 == null) return;
        
        var isPageReady = _currentProvider == "ChatGPT" ? _chatGPTPageReady : _geminiPageReady;
        if (!isPageReady)
        {
            var tcs = _currentProvider == "ChatGPT" ? _chatGPTPageLoadTcs : _geminiPageLoadTcs;
            if (tcs != null)
            {
                await Task.WhenAny(tcs.Task, Task.Delay(5000));
            }
        }
        
        if (screenshotBytes != null && screenshotBytes.Length > 0)
        {
            await UploadScreenshotAsync(screenshotBytes);
            
            if (_currentProvider.ToLowerInvariant() == "gemini")
            {
                await WaitForGeminiImageUploadAsync();
            }
        }
        
        if (!string.IsNullOrEmpty(query))
        {
            await SubmitQueryAsync(query);
        }
    }

    private async Task UploadScreenshotAsync(byte[] screenshotBytes)
    {
        var webView = ActiveWebView;
        if (webView?.CoreWebView2 == null) return;

        var provider = _currentProvider.ToLowerInvariant();
        var base64Image = Convert.ToBase64String(screenshotBytes);

        string script = provider == "gemini"
            ? GetGeminiUploadScript(base64Image)
            : GetChatGPTUploadScript(base64Image);

        await webView.CoreWebView2.ExecuteScriptAsync(script);
        await Task.Delay(2000);
    }

    private static string GetGeminiUploadScript(string base64Image) => $@"
        (function() {{
            try {{
                const input = document.querySelector('.ql-editor') || 
                              document.querySelector('rich-textarea') || 
                              document.querySelector('[contenteditable=""true""]');
                if (!input) return 'no-input-found';
                input.focus();
                input.click();
                const base64Data = '{base64Image}';
                const byteCharacters = atob(base64Data);
                const byteArray = new Uint8Array(byteCharacters.length);
                for (let i = 0; i < byteCharacters.length; i++) {{
                    byteArray[i] = byteCharacters.charCodeAt(i);
                }}
                const blob = new Blob([byteArray], {{ type: 'image/png' }});
                const file = new File([blob], 'screenshot.png', {{ type: 'image/png' }});
                const dataTransfer = new DataTransfer();
                dataTransfer.items.add(file);
                const pasteEvent = new ClipboardEvent('paste', {{
                    bubbles: true,
                    cancelable: true,
                    clipboardData: dataTransfer
                }});
                input.dispatchEvent(pasteEvent);
                return 'paste-dispatched';
            }} catch (e) {{
                return 'error: ' + e.message;
            }}
        }})();
    ";

    private static string GetChatGPTUploadScript(string base64Image) => $@"
        (function() {{
            try {{
                const base64Data = '{base64Image}';
                const byteCharacters = atob(base64Data);
                const byteNumbers = new Array(byteCharacters.length);
                for (let i = 0; i < byteCharacters.length; i++) {{
                    byteNumbers[i] = byteCharacters.charCodeAt(i);
                }}
                const byteArray = new Uint8Array(byteNumbers);
                const blob = new Blob([byteArray], {{ type: 'image/png' }});
                const file = new File([blob], 'screenshot.png', {{ type: 'image/png' }});
                const fileInput = document.querySelector('input[type=""file""]')
                               || document.querySelector('input[accept*=""image""]');
                if (fileInput) {{
                    const dataTransfer = new DataTransfer();
                    dataTransfer.items.add(file);
                    fileInput.files = dataTransfer.files;
                    fileInput.dispatchEvent(new Event('change', {{ bubbles: true, cancelable: true }}));
                    return 'success';
                }}
                return 'no-input-found';
            }} catch (e) {{
                return 'error: ' + e.message;
            }}
        }})();
    ";

    private async Task WaitForGeminiImageUploadAsync(int timeoutMs = 10000)
    {
        var webView = ActiveWebView;
        if (webView?.CoreWebView2 == null) return;

        var startTime = DateTime.UtcNow;
        var checkScript = @"
            (function() {
                const loadingPreview = document.querySelector('.image-preview.loading');
                const anyPreview = document.querySelector('.image-preview');
                if (loadingPreview) return 'loading';
                else if (anyPreview) return 'ready';
                else return 'no-preview';
            })();
        ";

        await Task.Delay(500);

        while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
        {
            try
            {
                var result = await webView.CoreWebView2.ExecuteScriptAsync(checkScript);
                var status = result.Trim('"');
                
                if (status == "ready") return;
                if (status == "no-preview" && (DateTime.UtcNow - startTime).TotalMilliseconds > 2000) return;
            }
            catch { }

            await Task.Delay(200);
        }
    }

    public async Task SubmitQueryAsync(string query)
    {
        var webView = ActiveWebView;
        var isInitialized = _currentProvider == "ChatGPT" ? _chatGPTInitialized : _geminiInitialized;
        
        if (!isInitialized || webView.CoreWebView2 == null) return;
        
        var escapedQuery = EscapeForJavaScript(query);
        var script = GetInjectionScript(_currentProvider, escapedQuery);
        
        try
        {
            var result = await webView.CoreWebView2.ExecuteScriptAsync(script);
            if (result == "false")
            {
                await FallbackClipboardPaste(query);
            }
        }
        catch
        {
            await FallbackClipboardPaste(query);
        }
    }

    private static string GetInjectionScript(string provider, string escapedQuery)
    {
        return provider.ToLowerInvariant() switch
        {
            "chatgpt" => $@"
                (function() {{
                    const textarea = document.querySelector('#prompt-textarea') 
                                  || document.querySelector('textarea[data-id=""root""]')
                                  || document.querySelector('div[contenteditable=""true""]')
                                  || document.querySelector('textarea');
                    if (!textarea) return false;
                    if (textarea.contentEditable === 'true') {{
                        textarea.innerHTML = `{escapedQuery}`;
                        textarea.dispatchEvent(new InputEvent('input', {{ bubbles: true, data: `{escapedQuery}` }}));
                    }} else {{
                        textarea.value = `{escapedQuery}`;
                        textarea.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    }}
                    textarea.focus();
                    setTimeout(() => {{
                        const sendBtn = document.querySelector('button[data-testid=""send-button""]')
                                     || document.querySelector('button[aria-label*=""Send""]')
                                     || document.querySelector('form button[type=""submit""]');
                        if (sendBtn && !sendBtn.disabled) sendBtn.click();
                    }}, 100);
                    return true;
                }})();
            ",
            "gemini" => $@"
                (function() {{
                    let input = document.querySelector('div.ql-editor.textarea')
                             || document.querySelector('p[data-placeholder]')
                             || document.querySelector('.input-area [contenteditable=""true""]')
                             || document.querySelector('[contenteditable=""true""]');
                    if (!input) return false;
                    input.innerText = `{escapedQuery}`;
                    input.dispatchEvent(new InputEvent('input', {{ bubbles: true, inputType: 'insertText' }}));
                    input.focus();
                    setTimeout(() => {{
                        const sendBtn = document.querySelector('button.send-button')
                                     || document.querySelector('button[aria-label*=""Send""]');
                        if (sendBtn && !sendBtn.disabled) sendBtn.click();
                        else input.dispatchEvent(new KeyboardEvent('keydown', {{ key: 'Enter', code: 'Enter', bubbles: true }}));
                    }}, 150);
                    return true;
                }})();
            ",
            _ => "false"
        };
    }

    private static string EscapeForJavaScript(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text
            .Replace("\\", "\\\\")
            .Replace("`", "\\`")
            .Replace("$", "\\$")
            .Replace("\r\n", "\\n")
            .Replace("\r", "\\n")
            .Replace("\n", "\\n");
    }

    private async Task FallbackClipboardPaste(string query)
    {
        try { Clipboard.SetText(query); }
        catch { return; }
        
        ActiveWebView.Focus();
        await Task.Delay(300);
        SendCtrlV();
        await Task.Delay(200);
        SendEnter();
    }

    #endregion

    #region Transcript Methods

    /// <summary>
    /// Updates the transcript with interim or final results.
    /// </summary>
    public void UpdateTranscript(string text, bool isFinal)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        
        Dispatcher.Invoke(() =>
        {
            if (isFinal)
            {
                if (!string.IsNullOrEmpty(_committedText) && !_committedText.EndsWith(" "))
                    _committedText += " ";
                _committedText += text.Trim();
                _currentInterim = "";
                TranscriptText.Text = _committedText;
            }
            else
            {
                _currentInterim = text.Trim();
                var displayText = _committedText;
                if (!string.IsNullOrEmpty(displayText) && !displayText.EndsWith(" "))
                    displayText += " ";
                displayText += _currentInterim;
                TranscriptText.Text = displayText;
            }
            
            TranscriptScroller.ScrollToEnd();
            
            if (!_isTranscriptVisible)
            {
                ShowTranscript();
            }
            
            // Adjust position based on transcript height
            AdjustPositionForTranscript();
            
            StatusText.Text = isFinal ? "Ready" : "Listening...";
            LiveDot.Fill = new SolidColorBrush(
                isFinal 
                    ? Color.FromRgb(0x64, 0xff, 0xda)
                    : Color.FromRgb(0x00, 0xd4, 0xaa));
        });
    }

    private void ShowTranscript()
    {
        TranscriptBorder.Visibility = Visibility.Visible;
        TypingCursor.Visibility = Visibility.Visible;
        _isTranscriptVisible = true;
        _cursorBlinkAnimation?.Begin();
        _transcriptExpandAnimation?.Begin();
    }

    public void HideTranscript()
    {
        Dispatcher.Invoke(() =>
        {
            _cursorBlinkAnimation?.Stop();
            TypingCursor.Visibility = Visibility.Collapsed;
            TranscriptBorder.Visibility = Visibility.Collapsed;
            _isTranscriptVisible = false;
        });
    }

    /// <summary>
    /// Smoothly adjusts the window position upward when transcript expands.
    /// </summary>
    private void AdjustPositionForTranscript()
    {
        // Calculate required height based on transcript content
        TranscriptBorder.UpdateLayout();
        var transcriptHeight = TranscriptBorder.ActualHeight;
        
        if (transcriptHeight > 0)
        {
            var workArea = SystemParameters.WorkArea;
            var desiredBottom = workArea.Bottom - 180; // Space for dictation bar
            var currentBottom = Top + Height + transcriptHeight + 8; // 8 = margin
            
            if (currentBottom > desiredBottom)
            {
                var adjustment = currentBottom - desiredBottom;
                
                var animation = new DoubleAnimation(Top, Top - adjustment, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                BeginAnimation(TopProperty, animation);
            }
        }
    }

    public void ResetTranscript()
    {
        Dispatcher.Invoke(() =>
        {
            _matrixTimer?.Stop();
            _matrixTimer = null;
            _isMatrixAnimating = false;
            _committedText = "";
            _currentInterim = "";
            TranscriptText.Text = "";
            StatusText.Text = "Listening...";
            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(0xe6, 0xf1, 0xff));
            TypingCursor.Visibility = Visibility.Visible;
        });
    }

    public void StartMatrixEffect()
    {
        var displayText = _committedText + (_currentInterim.Length > 0 ? " " + _currentInterim : "");
        if (_isMatrixAnimating || string.IsNullOrEmpty(displayText.Trim())) return;
        
        Dispatcher.Invoke(() =>
        {
            _isMatrixAnimating = true;
            _targetText = displayText.Trim();
            _matrixIterations = 0;
            
            _cursorBlinkAnimation?.Stop();
            TypingCursor.Visibility = Visibility.Collapsed;
            
            StatusText.Text = "Processing...";
            LiveDot.Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xd4, 0xaa));
            
            _matrixTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _matrixTimer.Tick += MatrixTimer_Tick;
            _matrixTimer.Start();
        });
    }

    private void MatrixTimer_Tick(object? sender, EventArgs e)
    {
        _matrixIterations++;
        
        var chars = _targetText.ToCharArray();
        int scrambleCount = Math.Max(1, chars.Length / 3);
        
        for (int i = 0; i < scrambleCount; i++)
        {
            int pos = _random.Next(chars.Length);
            if (!char.IsWhiteSpace(chars[pos]))
            {
                chars[pos] = MatrixChars[_random.Next(MatrixChars.Length)];
            }
        }
        
        TranscriptText.Text = new string(chars);
        
        var greenIntensity = Math.Min(255, 100 + _matrixIterations * 15);
        TranscriptText.Foreground = new SolidColorBrush(
            Color.FromRgb(0, (byte)greenIntensity, (byte)(greenIntensity / 2)));
        
        if (_matrixIterations >= 15)
        {
            _matrixTimer?.Stop();
            _matrixTimer = null;
            _isMatrixAnimating = false;
            HideTranscript();
        }
    }

    #endregion

    #region Window Events

    private void ShowLoading(bool show)
    {
        LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavigationStarting(string provider, CoreWebView2NavigationStartingEventArgs e)
    {
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
        
        if (provider == _currentProvider)
            ShowLoading(true);
    }

    private void OnNavigationCompleted(string provider, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (provider == _currentProvider)
            ShowLoading(false);
        
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
        if (_webViewEnvironment == null)
        {
            e.Handled = true;
            return;
        }

        var deferral = e.GetDeferral();
        
        try
        {
            var popupWindow = new BrowserPopupWindow(_webViewEnvironment);
            popupWindow.Show();
            await popupWindow.WebViewControl.EnsureCoreWebView2Async(_webViewEnvironment);
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

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
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

    /// <summary>
    /// Signs out from the current provider.
    /// </summary>
    public async Task SignOutAsync()
    {
        BrowserProfileManager.ClearProfile();
        _chatGPTInitialized = false;
        _geminiInitialized = false;
        _chatGPTPageReady = false;
        _geminiPageReady = false;
        await InitializeAsync(_currentProvider);
    }

    public void ShowWithAnimation()
    {
        Opacity = 0;
        PositionAboveDictationBar();
        Show();
        Activate();
        Topmost = true;
        _fadeInAnimation?.Begin(this);
    }

    public async Task HideWithAnimationAsync()
    {
        if (_fadeOutAnimation != null)
        {
            _fadeOutAnimation.Begin(this);
            await Task.Delay(200);
        }
        Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        BrowserClosing?.Invoke(this, EventArgs.Empty);
        Hide();
    }

    #endregion

    #region Resize Handling

    private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Rectangle handle)
        {
            _isResizing = true;
            _resizeStartPoint = e.GetPosition(this);
            _resizeStartWidth = Width;
            _resizeStartHeight = Height;
            _resizeStartLeft = Left;
            _resizeStartTop = Top;
            _resizeDirection = handle.Name;
            
            handle.CaptureMouse();
            e.Handled = true;
        }
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing) return;
        
        var currentPoint = e.GetPosition(this);
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
        }
        
        Width = newWidth;
        Height = newHeight;
        Left = newLeft;
        Top = newTop;
    }

    private void OnWindowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isResizing)
        {
            _isResizing = false;
            Mouse.Capture(null);
        }
    }

    #endregion

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
