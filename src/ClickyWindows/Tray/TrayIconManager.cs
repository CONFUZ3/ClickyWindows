using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ClickyWindows.Input;
using Serilog;

namespace ClickyWindows.Tray;

/// <summary>
/// System tray icon with context menu (uses WinForms NotifyIcon).
/// </summary>
public class TrayIconManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly PushToTalkController _pushToTalk;
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _menu;

    public event Action? QuitRequested;

    public TrayIconManager(AppSettings settings, PushToTalkController pushToTalk)
    {
        _settings = settings;
        _pushToTalk = pushToTalk;
    }

    public void Initialize()
    {
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Clicky for Windows").Enabled = false;
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Proxy: " + ShortenUrl(_settings.ProxyUrl)).Enabled = false;
        _menu.Items.Add("Hotkey: Ctrl+Alt (hold to talk)").Enabled = false;
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open Log Folder", null, OnOpenLogFolder);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit", null, OnQuit);

        _notifyIcon = new NotifyIcon
        {
            Text = "Clicky for Windows — Hold Ctrl+Alt to talk",
            ContextMenuStrip = _menu,
            Visible = true
        };

        // Load icon
        var iconPath = Path.Combine(AppContext.BaseDirectory, "clicky-icon.ico");
        if (File.Exists(iconPath))
        {
            _notifyIcon.Icon = new Icon(iconPath);
        }
        else
        {
            // Generate a simple blue triangle icon as fallback
            _notifyIcon.Icon = CreateFallbackIcon();
        }

        _notifyIcon.DoubleClick += (_, _) => Log.Debug("Tray icon double-clicked");

        Log.Information("Tray icon initialized");
    }

    private static string ShortenUrl(string url)
    {
        if (url.Length <= 40) return url;
        return url[..20] + "..." + url[^15..];
    }

    private void OnOpenLogFolder(object? sender, EventArgs e)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClickyWindows", "logs");
        System.Diagnostics.Process.Start("explorer.exe", logDir);
    }

    private void OnQuit(object? sender, EventArgs e) => QuitRequested?.Invoke();

    private static Icon CreateFallbackIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);

        // Draw a simple blue triangle
        var trianglePoints = new[]
        {
            new Point(2, 14),
            new Point(14, 14),
            new Point(8, 2)
        };
        g.FillPolygon(new SolidBrush(Color.FromArgb(0x33, 0x80, 0xFF)), trianglePoints);

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        _notifyIcon?.Dispose();
        _menu?.Dispose();
    }
}
