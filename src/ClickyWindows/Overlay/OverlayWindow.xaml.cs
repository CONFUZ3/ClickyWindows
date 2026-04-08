using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ClickyWindows.Interop;
using ClickyWindows.Screen;
using Serilog;

namespace ClickyWindows.Overlay;

public partial class OverlayWindow : Window
{
    public MonitorInfo Monitor { get; }

    public OverlayWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;

        // Position to cover this monitor
        Left = monitor.Bounds.Left;
        Top = monitor.Bounds.Top;
        Width = monitor.Bounds.Width;
        Height = monitor.Bounds.Height;

        // Initialize triangle shape
        InitializeTriangle();
    }

    private void InitializeTriangle()
    {
        // Blue cursor triangle: ~16x16 logical pixels, pointing up-left like a cursor
        CursorTriangle.Points = new PointCollection
        {
            new(0, 0),
            new(0, 16),
            new(11, 11)
        };
        CursorTriangle.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x80, 0xFF));
        CursorTriangle.RenderTransform = new RotateTransform(-35, 5.5, 8);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Log.Warning("OverlayWindow: HWND is zero on SourceInitialized");
            return;
        }

        // Apply transparent + click-through + no-taskbar extended styles
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        var newStyle = (IntPtr)((long)exStyle
            | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_NOACTIVATE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, newStyle);

        Log.Debug("OverlayWindow initialized for monitor {Index} at {Bounds}", Monitor.Index, Monitor.Bounds);
    }

    /// <summary>Move the cursor triangle to local overlay coords.</summary>
    public void SetTrianglePosition(double x, double y)
    {
        System.Windows.Controls.Canvas.SetLeft(CursorTriangle, x);
        System.Windows.Controls.Canvas.SetTop(CursorTriangle, y);
    }

    /// <summary>Show/hide waveform bars and update their heights.</summary>
    public void SetWaveform(bool visible, double[] levels)
    {
        WaveformPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;

        var bars = new[] { Bar1, Bar2, Bar3, Bar4, Bar5 };
        double maxBarHeight = 32;
        double minBarHeight = 4;

        for (int i = 0; i < bars.Length; i++)
        {
            double level = i < levels.Length ? levels[i] : 0;
            double height = minBarHeight + level * (maxBarHeight - minBarHeight);
            bars[i].Height = height;
            bars[i].Margin = new Thickness(1, (maxBarHeight - height) / 2, 1, (maxBarHeight - height) / 2);
        }

        // Position waveform near the cursor triangle
        var triangleX = System.Windows.Controls.Canvas.GetLeft(CursorTriangle);
        var triangleY = System.Windows.Controls.Canvas.GetTop(CursorTriangle);
        System.Windows.Controls.Canvas.SetLeft(WaveformPanel, triangleX + 20);
        System.Windows.Controls.Canvas.SetTop(WaveformPanel, triangleY - 16);
    }

    /// <summary>Show/hide processing spinner.</summary>
    public void SetSpinner(bool visible, double x, double y)
    {
        SpinnerCanvas.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        System.Windows.Controls.Canvas.SetLeft(SpinnerCanvas, x + 20);
        System.Windows.Controls.Canvas.SetTop(SpinnerCanvas, y - 16);
    }

    /// <summary>Advance spinner rotation by given degrees.</summary>
    public void AdvanceSpinner(double degrees)
    {
        SpinnerRotate.Angle = (SpinnerRotate.Angle + degrees) % 360;
    }

    /// <summary>Show speech bubble with text near the cursor.</summary>
    public void SetSpeechBubble(bool visible, string text, double x, double y)
    {
        SpeechBubble.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        SpeechText.Text = text;
        System.Windows.Controls.Canvas.SetLeft(SpeechBubble, x + 20);
        System.Windows.Controls.Canvas.SetTop(SpeechBubble, y - 30);
    }
}
