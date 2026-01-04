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
        
        // Set up full screen rectangle for the overlay
        FullScreenRect.Rect = new Rect(0, 0, Width, Height);
        
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
    
    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting) return;
        
        ReleaseMouseCapture();
        _isSelecting = false;
        
        // Calculate final selection rectangle
        var rect = GetSelectionRect();
        
        // Only capture if selection is meaningful (at least 10x10 pixels)
        if (rect.Width >= 10 && rect.Height >= 10)
        {
            CaptureRegion(rect);
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
        
        // Update the "hole" in the overlay
        SelectionRect.Rect = rect;
        
        // Update selection border
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
        SelectionBorder.Margin = new Thickness(rect.X, rect.Y, 0, 0);
    }
    
    private Rect GetSelectionRect()
    {
        var x = Math.Min(_startPoint.X, _endPoint.X);
        var y = Math.Min(_startPoint.Y, _endPoint.Y);
        var width = Math.Abs(_endPoint.X - _startPoint.X);
        var height = Math.Abs(_endPoint.Y - _startPoint.Y);
        
        return new Rect(x, y, width, height);
    }
    
    private void CaptureRegion(Rect selectionRect)
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
            
            // Hide this window briefly to capture what's underneath
            Opacity = 0;
            
            // Small delay to ensure window is hidden
            System.Threading.Thread.Sleep(50);
            
            using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            
            graphics.CopyFromScreen(screenX, screenY, 0, 0, new System.Drawing.Size(width, height));
            
            // Convert to PNG bytes
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            CapturedImage = ms.ToArray();
        }
        catch
        {
            CapturedImage = null;
        }
    }
}
