using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WisperFlow;

/// <summary>
/// Fullscreen transparent overlay for selecting a screen region to capture.
/// Similar to Windows Snipping Tool - gray overlay with clear selection area.
/// </summary>
public partial class ScreenshotOverlayWindow : Window
{
    private bool _isSelecting = false;
    private System.Windows.Point _startPoint;
    private System.Windows.Point _endPoint;
    
    /// <summary>
    /// Gets the captured screenshot bytes (PNG format), or null if cancelled.
    /// </summary>
    public byte[]? CapturedImage { get; private set; }
    
    /// <summary>
    /// Gets whether a selection was made (not cancelled).
    /// </summary>
    public bool WasCaptured => CapturedImage != null;
    
    public ScreenshotOverlayWindow()
    {
        InitializeComponent();
        
        // Cover entire virtual screen (all monitors)
        var virtualScreen = SystemParameters.VirtualScreenWidth;
        var virtualScreenHeight = SystemParameters.VirtualScreenHeight;
        var virtualScreenLeft = SystemParameters.VirtualScreenLeft;
        var virtualScreenTop = SystemParameters.VirtualScreenTop;
        
        Left = virtualScreenLeft;
        Top = virtualScreenTop;
        Width = virtualScreen;
        Height = virtualScreenHeight;
        WindowState = WindowState.Normal; // Override XAML to use manual sizing
        
        // Initial state: Top mask covers everything
        MaskTop.Height = Height;
        MaskBottom.Height = 0;
        MaskLeft.Width = 0;
        MaskRight.Width = 0;
        
        // Wire up mouse events
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
    }
    
    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSelecting = true;
        _startPoint = e.GetPosition(this);
        _endPoint = _startPoint;
        
        // Hide instructions
        InstructionsText.Visibility = Visibility.Collapsed;
        
        // Show selection border
        SelectionBorder.Visibility = Visibility.Visible;
        UpdateSelectionVisuals();
        
        CaptureMouse();
    }
    
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting) return;
        
        _endPoint = e.GetPosition(this);
        UpdateSelectionVisuals();
    }
    
    // Note: async void is acceptable for event handlers
    private async void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting) return;
        
        ReleaseMouseCapture();
        _isSelecting = false;
        
        // Calculate final selection rectangle
        var rect = GetSelectionRect();
        
        // Only capture if selection is meaningful (at least 10x10 pixels)
        if (rect.Width >= 10 && rect.Height >= 10)
        {
            await CaptureRegionAsync(rect);
        }
        
        Close();
    }
    
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CapturedImage = null;
            Close();
        }
    }
    
    private void UpdateSelectionVisuals()
    {
        var rect = GetSelectionRect();
        
        // Update 4-rect mask to create the "hole"
        // Top covers full width above selection
        MaskTop.Height = Math.Max(0, rect.Top);
        
        // Bottom covers full width below selection
        MaskBottom.Height = Math.Max(0, ActualHeight - rect.Bottom);
        
        // Left covers area to the left of selection (between top and bottom)
        MaskLeft.Height = rect.Height;
        MaskLeft.Width = Math.Max(0, rect.Left);
        MaskLeft.Margin = new Thickness(0, rect.Top, 0, 0);
        
        // Right covers area to the right of selection (between top and bottom)
        MaskRight.Height = rect.Height;
        MaskRight.Width = Math.Max(0, ActualWidth - rect.Right);
        MaskRight.Margin = new Thickness(0, rect.Top, 0, 0);
        
        // Update selection border
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        SelectionBorder.Margin = new Thickness(rect.Left, rect.Top, 0, 0);
    }
    
    private Rect GetSelectionRect()
    {
        var x = Math.Min(_startPoint.X, _endPoint.X);
        var y = Math.Min(_startPoint.Y, _endPoint.Y);
        var width = Math.Abs(_endPoint.X - _startPoint.X);
        var height = Math.Abs(_endPoint.Y - _startPoint.Y);
        
        return new Rect(x, y, width, height);
    }
    
    private async Task CaptureRegionAsync(Rect selectionRect)
    {
        try
        {
            // Get DPI scaling factor to convert WPF logical units to physical screen pixels
            var source = PresentationSource.FromVisual(this);
            double dpiScaleX = 1.0;
            double dpiScaleY = 1.0;
            if (source?.CompositionTarget != null)
            {
                dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
                dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
            }
            
            // Convert WPF logical coordinates to physical screen coordinates
            var screenX = (int)((Left + selectionRect.X) * dpiScaleX);
            var screenY = (int)((Top + selectionRect.Y) * dpiScaleY);
            var width = (int)(selectionRect.Width * dpiScaleX);
            var height = (int)(selectionRect.Height * dpiScaleY);
            
            // Hide this window completely to capture what's underneath
            // Cannot use Visibility.Hidden for ShowDialog windows, so use Opacity = 0
            Opacity = 0;
            
            // Wait for render cycle to complete and window to actually become transparent
            // This is crucial: Thread.Sleep blocks rendering, Task.Delay allows it
            await Task.Delay(200);
            
            await Task.Run(() =>
            {
                using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(bitmap);
                
                graphics.CopyFromScreen(screenX, screenY, 0, 0, new System.Drawing.Size(width, height));
                
                // Convert to PNG bytes
                using var ms = new MemoryStream();
                bitmap.Save(ms, ImageFormat.Png);
                CapturedImage = ms.ToArray();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Screenshot capture failed: {ex.Message}");
            CapturedImage = null;
        }
    }
}
