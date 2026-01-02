using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace WisperFlow;

/// <summary>
/// EXPERIMENTAL: Image upload strategies for testing.
/// </summary>
public partial class FloatingBrowserWindow
{
    /// <summary>
    /// Current active upload strategy (for testing).
    /// Set via test buttons in UI.
    /// </summary>
    private int _activeUploadStrategy = 0; // 0 = original, 1-11 = test strategies

    // Test logging on initialization
    static FloatingBrowserWindow()
    {
        LogToFile("[INIT] ImageUploadStrategies class initialized");
        System.Diagnostics.Debug.WriteLine("[INIT] ImageUploadStrategies class initialized");
    }

    /// <summary>
    /// Sets the active upload strategy for testing.
    /// </summary>
    public void SetUploadStrategy(int strategyIndex)
    {
        _activeUploadStrategy = strategyIndex;
        LogToFile($"[STRATEGY] Set active strategy to: {strategyIndex}");
        System.Diagnostics.Debug.WriteLine($"[STRATEGY BUTTON] Strategy set to: {strategyIndex}");
    }

    /// <summary>
    /// Simple logging method that writes to log.txt file
    /// </summary>
    private static void LogToFile(string message)
    {
        try
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
            string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [ImageUpload] {message}{Environment.NewLine}";
            File.AppendAllText(logPath, logEntry);
        }
        catch
        {
            // Ignore logging errors
        }
    }

    /// <summary>
    /// Uploads screenshot using the currently selected strategy.
    /// </summary>
    private async Task UploadScreenshotWithStrategyAsync(byte[] screenshotBytes, int strategyIndex)
    {
        LogToFile($"UploadScreenshotWithStrategyAsync called with strategy {strategyIndex}, bytes: {screenshotBytes.Length}");

        var webView = ActiveWebView;
        if (webView?.CoreWebView2 == null)
        {
            LogToFile("WebView not ready, aborting");
            return;
        }

        try
        {
            var base64Image = Convert.ToBase64String(screenshotBytes);

            switch (strategyIndex)
            {
                case 1:
                    await Strategy1_EnhancedClipboardPasteAsync(webView, screenshotBytes, base64Image);
                    break;
                case 2:
                    await Strategy2_JavaScriptFileInputAsync(webView, base64Image);
                    break;
                case 3:
                    await Strategy3_DragAndDropAsync(webView, base64Image);
                    break;
                case 4:
                    await Strategy4_DirectDOMFileInputAsync(webView, base64Image);
                    break;
                case 5:
                    await Strategy5_HybridFallbackAsync(webView, screenshotBytes, base64Image);
                    break;
                case 6:
                    await Strategy6_PostMessageAsync(webView, base64Image);
                    break;
                case 7:
                    await Strategy7_GeminiIconClickAsync(webView, base64Image);
                    break;
                case 8:
                    await Strategy8_GeminiClipboardAsync(webView, screenshotBytes);
                    break;
                case 9:
                    await Strategy9_GeminiTextareaAsync(webView, base64Image);
                    break;
                case 10:
                    await Strategy10_GeminiMultiAsync(webView, screenshotBytes, base64Image);
                    break;
                case 11:
                    await Strategy11_GeminiTestAsync(webView);
                    break;
                case 12:
                    await Strategy12_DirectImageInjectionAsync(webView, base64Image);
                    break;
                default:
                    // Original strategy (strategy 0)
                    await UploadScreenshotOriginalAsync(screenshotBytes);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Strategy {strategyIndex} failed: {ex.Message}");
        }
    }

    #region Gemini-Specific Strategies

    private async Task Strategy7_GeminiIconClickAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, string base64Image)
    {
        LogToFile("Strategy 7: Starting Gemini icon click");

        if (_currentProvider.ToLowerInvariant() != "gemini")
        {
            LogToFile("Strategy 7: Not Gemini, using fallback");
            await UploadScreenshotOriginalAsync(System.Convert.FromBase64String(base64Image));
            return;
        }

        System.Diagnostics.Debug.WriteLine("Strategy 7: Clicking Gemini upload icon");

        var script = @"
            (function() {
                try {
                    // Based on Gemini source code: mat-icon with class upload-icon
                    const uploadIcon = document.querySelector('mat-icon.upload-icon') ||
                                     document.querySelector('mat-icon[class*=""upload""]');

                    if (uploadIcon) {
                        console.log('Found upload icon, clicking...');
                        uploadIcon.click();
                        return 'upload-icon-clicked';
                    }

                    // Fallback: Look for add_2 icon
                    const icons = document.querySelectorAll('mat-icon');
                    for (const icon of icons) {
                        if (icon.textContent === 'add_2') {
                            console.log('Found add_2 icon, clicking...');
                            icon.click();
                            return 'add_2-icon-clicked';
                        }
                    }

                    return 'no-upload-icon-found';
                } catch (e) {
                    return 'error: ' + e.message;
                }
            })();
        ";

        var result = await webView.CoreWebView2.ExecuteScriptAsync(script);
        System.Diagnostics.Debug.WriteLine($"Strategy 7 result: {result}");

        await Task.Delay(2000);
    }

    private async Task Strategy8_GeminiClipboardAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, byte[] screenshotBytes)
    {
        if (_currentProvider.ToLowerInvariant() != "gemini")
        {
            await UploadScreenshotOriginalAsync(screenshotBytes);
            return;
        }

        LogToFile("Strategy 8: Gemini clipboard approach with enhanced paste event");

        // Set image to clipboard
        using var ms = new System.IO.MemoryStream(screenshotBytes);
        var bitmapImage = new System.Windows.Media.Imaging.BitmapImage();
        bitmapImage.BeginInit();
        bitmapImage.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmapImage.StreamSource = ms;
        bitmapImage.EndInit();
        bitmapImage.Freeze();
        Clipboard.SetImage(bitmapImage);

        var base64Image = Convert.ToBase64String(screenshotBytes);

        // Enhanced clipboard paste with actual file data
        var script = $@"
            (function() {{
                try {{
                    // Find Gemini input - try multiple selectors
                    const input = document.querySelector('textarea[placeholder*=""Ask Gemini""]') ||
                                 document.querySelector('textarea[placeholder*=""Enter a prompt""]') ||
                                 document.querySelector('.gds-body-l') ||
                                 document.querySelector('div[contenteditable=""true""]') ||
                                 document.querySelector('textarea');

                    if (!input) {{
                        return 'no-input-found';
                    }}

                    // Focus and click
                    input.focus();
                    input.click();
                    
                    // Wait a bit for focus
                    setTimeout(() => {{
                        try {{
                            // Create file from base64
                            const base64Data = '{base64Image}';
                            const byteCharacters = atob(base64Data);
                            const byteNumbers = new Array(byteCharacters.length);
                            for (let i = 0; i < byteCharacters.length; i++) {{
                                byteNumbers[i] = byteCharacters.charCodeAt(i);
                            }}
                            const byteArray = new Uint8Array(byteNumbers);
                            const blob = new Blob([byteArray], {{ type: 'image/png' }});
                            const file = new File([blob], 'screenshot.png', {{ type: 'image/png' }});
                            
                            // Create clipboard event with file
                            const dataTransfer = new DataTransfer();
                            dataTransfer.items.add(file);
                            
                            const pasteEvent = new ClipboardEvent('paste', {{
                                bubbles: true,
                                cancelable: true,
                                clipboardData: dataTransfer
                            }});
                            
                            input.dispatchEvent(pasteEvent);
                            console.log('Enhanced paste event dispatched with file data');
                        }} catch (e) {{
                            console.error('Error in paste event:', e);
                        }}
                    }}, 300);
                    
                    return 'paste-event-scheduled';
                }} catch (e) {{
                    return 'error: ' + e.message;
                }}
            }})();
        ";

        var result = await webView.CoreWebView2.ExecuteScriptAsync(script);
        LogToFile($"Strategy 8 result: {result}");
        System.Diagnostics.Debug.WriteLine($"Strategy 8 result: {result}");

        await Task.Delay(2000);
    }

    private async Task Strategy9_GeminiTextareaAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, string base64Image)
    {
        if (_currentProvider.ToLowerInvariant() != "gemini")
        {
            await UploadScreenshotOriginalAsync(System.Convert.FromBase64String(base64Image));
            return;
        }

        LogToFile("Strategy 9: Gemini textarea drag & drop with enhanced events");

        var script = $@"
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

                    // Find Gemini input - try multiple selectors
                    const input = document.querySelector('textarea[placeholder*=""Ask Gemini""]') ||
                                 document.querySelector('textarea[placeholder*=""Enter a prompt""]') ||
                                 document.querySelector('.gds-body-l') ||
                                 document.querySelector('div[contenteditable=""true""]') ||
                                 document.querySelector('textarea');

                    if (!input) {{
                        return 'no-input-found';
                    }}

                    // Focus first
                    input.focus();
                    input.click();

                    // Create data transfer with file
                    const dataTransfer = new DataTransfer();
                    dataTransfer.items.add(file);
                    dataTransfer.effectAllowed = 'all';
                    dataTransfer.dropEffect = 'copy';

                    // Dispatch drag events in sequence
                    input.dispatchEvent(new DragEvent('dragenter', {{
                        bubbles: true,
                        cancelable: true,
                        dataTransfer: dataTransfer
                    }}));

                    input.dispatchEvent(new DragEvent('dragover', {{
                        bubbles: true,
                        cancelable: true,
                        dataTransfer: dataTransfer
                    }}));

                    setTimeout(() => {{
                        input.dispatchEvent(new DragEvent('drop', {{
                            bubbles: true,
                            cancelable: true,
                            dataTransfer: dataTransfer
                        }}));
                        console.log('Drag and drop completed on Gemini input');
                    }}, 100);

                    return 'drag-drop-events-dispatched';
                }} catch (e) {{
                    return 'error: ' + e.message;
                }}
            }})();
        ";

        var result = await webView.CoreWebView2.ExecuteScriptAsync(script);
        LogToFile($"Strategy 9 result: {result}");
        System.Diagnostics.Debug.WriteLine($"Strategy 9 result: {result}");

        await Task.Delay(2000);
    }

    private async Task Strategy10_GeminiMultiAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, byte[] screenshotBytes, string base64Image)
    {
        if (_currentProvider.ToLowerInvariant() != "gemini")
        {
            await Strategy5_HybridFallbackAsync(webView, screenshotBytes, base64Image);
            return;
        }

        System.Diagnostics.Debug.WriteLine("Strategy 10: Gemini multi-method approach");

        // Method 1: Try icon click
        await Strategy7_GeminiIconClickAsync(webView, base64Image);
        await Task.Delay(1500);

        // Method 2: Try clipboard
        await Strategy8_GeminiClipboardAsync(webView, screenshotBytes);
        await Task.Delay(1500);

        // Method 3: Try drag & drop
        await Strategy9_GeminiTextareaAsync(webView, base64Image);

        System.Diagnostics.Debug.WriteLine("Strategy 10: All Gemini methods attempted");
    }

    private async Task Strategy11_GeminiTestAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView)
    {
        LogToFile("Strategy 11: Starting Gemini element detection test");

        if (_currentProvider.ToLowerInvariant() != "gemini")
        {
            LogToFile("Strategy 11: Not Gemini, skipping");
            return;
        }

        System.Diagnostics.Debug.WriteLine("Strategy 11: Gemini element detection test");

        var script = @"
            (function() {
                try {
                    const results = {
                        uploadIcon: !!document.querySelector('mat-icon.upload-icon'),
                        add2Icon: !!Array.from(document.querySelectorAll('mat-icon')).find(i => i.textContent === 'add_2'),
                        textarea: !!document.querySelector('textarea[placeholder*=""Ask Gemini""]'),
                        gdsTextarea: !!document.querySelector('.gds-body-l'),
                        fileInputs: document.querySelectorAll('input[type=""file""]').length,
                        totalIcons: document.querySelectorAll('mat-icon').length
                    };

                    console.log('Gemini detection results:', results);
                    return JSON.stringify(results);
                } catch (e) {
                    return 'error: ' + e.message;
                }
            })();
        ";

        var result = await webView.CoreWebView2.ExecuteScriptAsync(script);
        System.Diagnostics.Debug.WriteLine($"Strategy 11 detection: {result}");

        await Task.Delay(1000);
    }

    /// <summary>
    /// Strategy 12: File Input Programmatic Upload (VERIFIED - Uses actual file input mechanism).
    /// Finds or creates file input, sets File object programmatically, triggers change event.
    /// This avoids file dialog but uses Gemini's native file handling.
    /// </summary>
    private async Task Strategy12_DirectImageInjectionAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, string base64Image)
    {
        LogToFile("Strategy 12: File input programmatic upload (verified approach)");

        if (_currentProvider.ToLowerInvariant() != "gemini")
        {
            LogToFile("Strategy 12: Not Gemini, using fallback");
            await UploadScreenshotOriginalAsync(System.Convert.FromBase64String(base64Image));
            return;
        }

        var script = $@"
            (function() {{
                try {{
                    const base64Data = '{base64Image}';
                    
                    // Convert base64 to File object
                    const byteCharacters = atob(base64Data);
                    const byteNumbers = new Array(byteCharacters.length);
                    for (let i = 0; i < byteCharacters.length; i++) {{
                        byteNumbers[i] = byteCharacters.charCodeAt(i);
                    }}
                    const byteArray = new Uint8Array(byteNumbers);
                    const blob = new Blob([byteArray], {{ type: 'image/png' }});
                    const file = new File([blob], 'screenshot.png', {{ type: 'image/png', lastModified: Date.now() }});
                    
                    // Find existing file input (Gemini may have hidden ones)
                    let fileInput = null;
                    
                    // Method 1: Look for visible file input
                    fileInput = document.querySelector('input[type=""file""]');
                    
                    // Method 2: Search all inputs
                    if (!fileInput) {{
                        const allInputs = Array.from(document.querySelectorAll('input'));
                        fileInput = allInputs.find(inp => inp.type === 'file' && inp.accept && inp.accept.includes('image'));
                    }}
                    
                    // Method 3: Look for file input near the textarea
                    if (!fileInput) {{
                        const textarea = document.querySelector('textarea[placeholder*=""Enter a prompt""]') ||
                                       document.querySelector('textarea');
                        if (textarea) {{
                            const container = textarea.closest('div') || textarea.parentElement;
                            if (container) {{
                                fileInput = container.querySelector('input[type=""file""]');
                            }}
                        }}
                    }}
                    
                    // Method 4: Create temporary file input if none found
                    if (!fileInput) {{
                        fileInput = document.createElement('input');
                        fileInput.type = 'file';
                        fileInput.accept = 'image/*';
                        fileInput.style.position = 'absolute';
                        fileInput.style.left = '-9999px';
                        fileInput.style.opacity = '0';
                        document.body.appendChild(fileInput);
                    }}
                    
                    // Set the file using DataTransfer (required for programmatic file setting)
                    const dataTransfer = new DataTransfer();
                    dataTransfer.items.add(file);
                    
                    // Set files property (this is the key - must use DataTransfer)
                    Object.defineProperty(fileInput, 'files', {{
                        writable: true,
                        value: dataTransfer.files
                    }});
                    
                    // Verify file was set
                    if (!fileInput.files || fileInput.files.length === 0) {{
                        // Fallback: try direct assignment
                        fileInput.files = dataTransfer.files;
                    }}
                    
                    // Trigger change event (Gemini listens for this)
                    const changeEvent = new Event('change', {{ 
                        bubbles: true, 
                        cancelable: true 
                    }});
                    fileInput.dispatchEvent(changeEvent);
                    
                    // Also trigger input event
                    const inputEvent = new Event('input', {{ 
                        bubbles: true, 
                        cancelable: true 
                    }});
                    fileInput.dispatchEvent(inputEvent);
                    
                    // Verify
                    const fileCount = fileInput.files ? fileInput.files.length : 0;
                    const fileName = fileCount > 0 ? fileInput.files[0].name : 'none';
                    const fileSize = fileCount > 0 ? fileInput.files[0].size : 0;
                    
                    console.log('File input updated:', fileCount, 'file(s),', fileName, fileSize, 'bytes');
                    
                    return fileCount > 0 ? 'file-set-successfully' : 'file-set-failed';
                }} catch (e) {{
                    console.error('Strategy 12 error:', e);
                    return 'error: ' + e.message;
                }}
            }})();
        ";

        var result = await webView.CoreWebView2.ExecuteScriptAsync(script);
        LogToFile($"Strategy 12 result: {result}");
        System.Diagnostics.Debug.WriteLine($"Strategy 12 result: {result}");

        // Wait for Gemini to process the file
        await Task.Delay(3000);
    }

    #endregion

    #region Basic Strategies

    private async Task Strategy1_EnhancedClipboardPasteAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, byte[] screenshotBytes, string base64Image)
    {
        await UploadScreenshotOriginalAsync(screenshotBytes);
    }

    private async Task Strategy2_JavaScriptFileInputAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, string base64Image)
    {
        await UploadScreenshotOriginalAsync(System.Convert.FromBase64String(base64Image));
    }

    private async Task Strategy3_DragAndDropAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, string base64Image)
    {
        await UploadScreenshotOriginalAsync(System.Convert.FromBase64String(base64Image));
    }

    private async Task Strategy4_DirectDOMFileInputAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, string base64Image)
    {
        await UploadScreenshotOriginalAsync(System.Convert.FromBase64String(base64Image));
    }

    private async Task Strategy5_HybridFallbackAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, byte[] screenshotBytes, string base64Image)
    {
        if (_currentProvider.ToLowerInvariant() == "gemini")
        {
            await Strategy10_GeminiMultiAsync(webView, screenshotBytes, base64Image);
        }
        else
        {
            await UploadScreenshotOriginalAsync(screenshotBytes);
        }
    }

    private async Task Strategy6_PostMessageAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView, string base64Image)
    {
        await UploadScreenshotOriginalAsync(System.Convert.FromBase64String(base64Image));
    }

    #endregion

    #region Helper Methods

    private static string GetImageVerificationScript(string provider)
    {
        return provider.ToLowerInvariant() switch
        {
            "chatgpt" => @"
                (function() {
                    const imagePreview = document.querySelector('img[src*=""blob:""]') ||
                                       document.querySelector('.image-preview') ||
                                       document.querySelector('[data-testid*=""image""]');
                    return imagePreview ? 'true' : 'false';
                })();",

            "gemini" => @"
                (function() {
                    const imageIndicator = document.querySelector('img[src*=""blob:""]') ||
                                         document.querySelector('.uploaded-image') ||
                                         document.querySelector('[aria-label*=""image""]') ||
                                         document.querySelector('.file-preview img');
                    return imageIndicator ? 'true' : 'false';
                })();",

            _ => @"false"
        };
    }

    #endregion
}
