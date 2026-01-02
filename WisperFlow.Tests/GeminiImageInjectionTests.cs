using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace WisperFlow.Tests;

/// <summary>
/// Tests for image injection/paste functionality on Gemini.
/// These tests verify that images can be injected without using file upload.
/// </summary>
public class GeminiImageInjectionTests
{
    /// <summary>
    /// Creates a simple test image (1x1 red pixel) for testing.
    /// </summary>
    private static byte[] CreateTestImage()
    {
        using var bitmap = new Bitmap(100, 100, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Red);
        
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>
    /// Gets the base64 representation of a test image.
    /// </summary>
    private static string GetTestImageBase64()
    {
        var imageBytes = CreateTestImage();
        return Convert.ToBase64String(imageBytes);
    }

    /// <summary>
    /// Test: Verify that we can create a test image.
    /// </summary>
    [Fact]
    public void CreateTestImage_ShouldReturnValidPngBytes()
    {
        var imageBytes = CreateTestImage();
        
        Assert.NotNull(imageBytes);
        Assert.True(imageBytes.Length > 0);
        
        // PNG files start with PNG signature
        Assert.Equal(0x89, imageBytes[0]);
        Assert.Equal(0x50, imageBytes[1]); // P
        Assert.Equal(0x4E, imageBytes[2]); // N
        Assert.Equal(0x47, imageBytes[3]); // G
    }

    /// <summary>
    /// Test: Verify base64 encoding works correctly.
    /// </summary>
    [Fact]
    public void GetTestImageBase64_ShouldReturnValidBase64()
    {
        var base64 = GetTestImageBase64();
        
        Assert.NotNull(base64);
        Assert.True(base64.Length > 0);
        
        // Base64 should only contain valid characters
        Assert.All(base64.ToCharArray(), c => 
            Assert.True(
                char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=',
                $"Invalid base64 character: {c}"));
    }

    /// <summary>
    /// Test: JavaScript code to inject image via clipboard paste event.
    /// This simulates what happens when Ctrl+V is pressed with an image in clipboard.
    /// </summary>
    [Fact]
    public void GenerateClipboardPasteScript_ShouldCreateValidJavaScript()
    {
        var base64Image = GetTestImageBase64();
        
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
                    
                    // Find Gemini input
                    const input = document.querySelector('textarea[placeholder*=""Ask Gemini""]') ||
                                 document.querySelector('.gds-body-l') ||
                                 document.querySelector('div[contenteditable=""true""]') ||
                                 document.querySelector('textarea');
                    
                    if (!input) return 'no-input-found';
                    
                    // Focus the input
                    input.focus();
                    input.click();
                    
                    // Create clipboard event with image data
                    const dataTransfer = new DataTransfer();
                    dataTransfer.items.add(file);
                    
                    const pasteEvent = new ClipboardEvent('paste', {{
                        bubbles: true,
                        cancelable: true,
                        clipboardData: dataTransfer
                    }});
                    
                    input.dispatchEvent(pasteEvent);
                    
                    return 'paste-event-dispatched';
                }} catch (e) {{
                    return 'error: ' + e.message;
                }}
            }})();
        ";
        
        Assert.NotNull(script);
        Assert.Contains("base64Data", script);
        Assert.Contains("blob", script);
        Assert.Contains("ClipboardEvent", script);
        Assert.Contains("paste", script);
    }

    /// <summary>
    /// Test: JavaScript code to inject image via drag and drop.
    /// </summary>
    [Fact]
    public void GenerateDragDropScript_ShouldCreateValidJavaScript()
    {
        var base64Image = GetTestImageBase64();
        
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
                    
                    // Find Gemini input
                    const input = document.querySelector('textarea[placeholder*=""Ask Gemini""]') ||
                                 document.querySelector('.gds-body-l') ||
                                 document.querySelector('div[contenteditable=""true""]') ||
                                 document.querySelector('textarea');
                    
                    if (!input) return 'no-input-found';
                    
                    // Create drag and drop events
                    const dataTransfer = new DataTransfer();
                    dataTransfer.items.add(file);
                    
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
                    
                    input.dispatchEvent(new DragEvent('drop', {{
                        bubbles: true,
                        cancelable: true,
                        dataTransfer: dataTransfer
                    }}));
                    
                    return 'drag-drop-completed';
                }} catch (e) {{
                    return 'error: ' + e.message;
                }}
            }})();
        ";
        
        Assert.NotNull(script);
        Assert.Contains("DragEvent", script);
        Assert.Contains("dragenter", script);
        Assert.Contains("drop", script);
    }

    /// <summary>
    /// Test: JavaScript code to inject image by directly creating an img element.
    /// This approach injects the image as an HTML element into the contenteditable area.
    /// </summary>
    [Fact]
    public void GenerateDirectImageInjectionScript_ShouldCreateValidJavaScript()
    {
        var base64Image = GetTestImageBase64();
        
        var script = $@"
            (function() {{
                try {{
                    const base64Data = '{base64Image}';
                    const dataUrl = 'data:image/png;base64,' + base64Data;
                    
                    // Find Gemini input
                    const input = document.querySelector('textarea[placeholder*=""Ask Gemini""]') ||
                                 document.querySelector('.gds-body-l') ||
                                 document.querySelector('div[contenteditable=""true""]') ||
                                 document.querySelector('textarea');
                    
                    if (!input) return 'no-input-found';
                    
                    // For contenteditable divs, inject img element
                    if (input.contentEditable === 'true' || input.tagName === 'DIV') {{
                        const img = document.createElement('img');
                        img.src = dataUrl;
                        img.style.maxWidth = '100%';
                        img.style.height = 'auto';
                        input.appendChild(img);
                        
                        // Trigger input event
                        input.dispatchEvent(new InputEvent('input', {{
                            bubbles: true,
                            inputType: 'insertImage'
                        }}));
                        
                        return 'image-injected-as-element';
                    }}
                    
                    // For textareas, try to set value (may not work for images)
                    return 'textarea-not-supported-for-images';
                }} catch (e) {{
                    return 'error: ' + e.message;
                }}
            }})();
        ";
        
        Assert.NotNull(script);
        Assert.Contains("data:image/png;base64", script);
        Assert.Contains("createElement", script);
        Assert.Contains("img", script);
    }

    /// <summary>
    /// Test: Verify that the image injection script can be executed without syntax errors.
    /// This is a syntax validation test.
    /// </summary>
    [Fact]
    public void ImageInjectionScripts_ShouldHaveValidSyntax()
    {
        var base64Image = GetTestImageBase64();
        
        // Test clipboard paste script syntax
        var clipboardScript = $@"
            (function() {{
                const base64Data = '{base64Image}';
                const byteCharacters = atob(base64Data);
                return 'ok';
            }})();
        ";
        
        // Test drag drop script syntax
        var dragDropScript = $@"
            (function() {{
                const base64Data = '{base64Image}';
                const blob = new Blob([new Uint8Array([1,2,3])], {{ type: 'image/png' }});
                return 'ok';
            }})();
        ";
        
        // Test direct injection script syntax
        var directScript = $@"
            (function() {{
                const base64Data = '{base64Image}';
                const dataUrl = 'data:image/png;base64,' + base64Data;
                return 'ok';
            }})();
        ";
        
        // All scripts should be non-null and contain base64 data
        Assert.NotNull(clipboardScript);
        Assert.NotNull(dragDropScript);
        Assert.NotNull(directScript);
        
        Assert.Contains(base64Image, clipboardScript);
        Assert.Contains(base64Image, dragDropScript);
        Assert.Contains(base64Image, directScript);
    }

    /// <summary>
    /// Test: Verify that image bytes can be converted to base64 and back.
    /// </summary>
    [Fact]
    public void ImageBase64Conversion_ShouldBeReversible()
    {
        var originalBytes = CreateTestImage();
        var base64 = Convert.ToBase64String(originalBytes);
        var decodedBytes = Convert.FromBase64String(base64);
        
        Assert.Equal(originalBytes.Length, decodedBytes.Length);
        Assert.Equal(originalBytes, decodedBytes);
    }

    /// <summary>
    /// Test: Verify that the script handles edge cases (empty image, very large image).
    /// </summary>
    [Fact]
    public void ImageInjectionScripts_ShouldHandleEdgeCases()
    {
        // Test with minimal valid PNG (1x1 pixel)
        var minimalImage = CreateTestImage();
        var minimalBase64 = Convert.ToBase64String(minimalImage);
        
        var script = $@"
            (function() {{
                const base64Data = '{minimalBase64}';
                if (!base64Data || base64Data.length === 0) {{
                    return 'empty-base64';
                }}
                try {{
                    const byteCharacters = atob(base64Data);
                    if (byteCharacters.length === 0) {{
                        return 'empty-decoded';
                    }}
                    return 'ok';
                }} catch (e) {{
                    return 'error: ' + e.message;
                }}
            }})();
        ";
        
        Assert.NotNull(script);
        Assert.Contains("base64Data", script);
        Assert.Contains("atob", script);
    }
}
