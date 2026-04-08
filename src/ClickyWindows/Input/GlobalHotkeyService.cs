using System.Diagnostics;
using System.Runtime.InteropServices;
using ClickyWindows.Interop;
using Serilog;

namespace ClickyWindows.Input;

/// <summary>
/// WH_KEYBOARD_LL hook with minimal callback (set flag + return) and health check.
/// Default hotkey: Ctrl+Alt (press = start, release = stop).
/// Per plan: callback has 300ms OS timeout; silently removed after ~10 violations.
/// </summary>
public class GlobalHotkeyService
{
    private readonly AppSettings _settings;
    private IntPtr _hookHandle = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _hookProc;

    // Minimal callback flag
    private volatile bool _altDown;
    private volatile bool _ctrlDown;
    private DateTime _lastCallbackTime = DateTime.UtcNow;

    private Thread? _processingThread;
    private CancellationTokenSource? _cts;
    private readonly System.Threading.Timer _healthTimer;

    public event Action? HotkeyPressed;
    public event Action? HotkeyReleased;

    public GlobalHotkeyService(AppSettings settings)
    {
        _settings = settings;
        _healthTimer = new System.Threading.Timer(OnHealthCheck, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        RegisterHook();
        _healthTimer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        _processingThread = new Thread(ProcessingLoop)
        {
            IsBackground = true,
            Name = "HotkeyProcessor"
        };
        _processingThread.Start();
    }

    public void Stop()
    {
        _healthTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _cts?.Cancel();
        UnregisterHook();
    }

    private void RegisterHook()
    {
        // Keep a GC-rooted reference to the delegate
        _hookProc = LowLevelKeyboardProc;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _hookProc,
            NativeMethods.GetModuleHandle(module.ModuleName),
            0);

        if (_hookHandle == IntPtr.Zero)
            Log.Error("Failed to register keyboard hook (error: {Error})", Marshal.GetLastWin32Error());
        else
            Log.Information("Keyboard hook registered");
    }

    private void UnregisterHook()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            Log.Information("Keyboard hook unregistered");
        }
    }

    /// <summary>
    /// CRITICAL: This callback must be minimal (300ms OS timeout).
    /// Only set flags — all real work is done on the processing thread.
    /// </summary>
    private IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            _lastCallbackTime = DateTime.UtcNow;

            var msg = wParam.ToInt32();
            var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

            bool isDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;
            bool isUp = msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP;

            if (kb.vkCode == NativeMethods.VK_LMENU || kb.vkCode == NativeMethods.VK_RMENU || kb.vkCode == NativeMethods.VK_MENU)
            {
                if (isDown) _altDown = true;
                if (isUp) _altDown = false;
            }
            if (kb.vkCode == NativeMethods.VK_LCONTROL || kb.vkCode == NativeMethods.VK_RCONTROL || kb.vkCode == NativeMethods.VK_CONTROL)
            {
                if (isDown) _ctrlDown = true;
                if (isUp) _ctrlDown = false;
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void ProcessingLoop()
    {
        bool wasActive = false;
        while (_cts?.IsCancellationRequested == false)
        {
            bool isActive = _ctrlDown && _altDown;

            if (isActive && !wasActive)
            {
                wasActive = true;
                HotkeyPressed?.Invoke();
                Log.Debug("Hotkey pressed (Ctrl+Alt)");
            }
            else if (!isActive && wasActive)
            {
                wasActive = false;
                HotkeyReleased?.Invoke();
                Log.Debug("Hotkey released (Ctrl+Alt)");
            }

            Thread.Sleep(10); // 10ms polling is fine for key state
        }
    }

    private void OnHealthCheck(object? state)
    {
        // If no callbacks received for 5s, the hook was silently removed — re-register.
        // MUST dispatch to UI thread: WH_KEYBOARD_LL requires a message pump on the
        // registering thread. Thread pool threads have no message pump, so hooks
        // registered there never deliver callbacks and immediately die.
        if ((DateTime.UtcNow - _lastCallbackTime).TotalSeconds > 5)
        {
            Log.Warning("Keyboard hook appears dead (no callbacks for 5s) — re-registering");
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                UnregisterHook();
                RegisterHook();
            });
        }
    }
}
