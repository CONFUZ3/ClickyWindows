using System.Diagnostics;
using System.Runtime.InteropServices;
using ClickyWindows.Interop;
using Serilog;

namespace ClickyWindows.Input;

/// <summary>
/// WH_KEYBOARD_LL hook with minimal callback (set flag + return) and health check.
/// Default hotkey: Ctrl+Alt (press = start, release = stop).
/// Monitors hook health and auto-recovers if Windows silently removes it.
/// The hook MUST be registered on a thread with a message pump (the WPF UI thread).
/// </summary>
public class GlobalHotkeyService
{
    private readonly AppSettings _settings;
    private IntPtr _hookHandle = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _hookProc;

    // Minimal callback flag
    private volatile bool _altDown;
    private volatile bool _ctrlDown;
    private Thread? _processingThread;
    private CancellationTokenSource? _cts;

    // Hook health monitoring — only activates after first callback proves the hook worked
    private volatile bool _everReceivedCallback;
    private long _lastCallbackTicks;
    private int _reRegisterCount;
    private const int HealthCheckIntervalIterations = 500; // 500 * 10ms = 5s
    private static readonly long StaleThresholdTicks = Stopwatch.Frequency * 30; // 30 seconds

    public event Action? HotkeyPressed;
    public event Action? HotkeyReleased;

    public GlobalHotkeyService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>Must be called from the UI thread.</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        RegisterHook();

        _processingThread = new Thread(ProcessingLoop)
        {
            IsBackground = true,
            Name = "HotkeyProcessor"
        };
        _processingThread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        UnregisterHook();
    }

    /// <summary>Must be called from the UI thread (message pump required for WH_KEYBOARD_LL).</summary>
    private void RegisterHook()
    {
        UnregisterHook(); // clean up any previous hook

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
            Log.Information("Keyboard hook registered (attempt #{Count})", _reRegisterCount + 1);
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
        // Update heartbeat — must be first, before any other work
        _everReceivedCallback = true;
        Interlocked.Exchange(ref _lastCallbackTicks, Stopwatch.GetTimestamp());

        if (nCode >= 0)
        {
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
        int iteration = 0;

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

            // Periodic hook health check — only after we've confirmed the hook worked at least once
            iteration++;
            if (iteration % HealthCheckIntervalIterations == 0 && _everReceivedCallback)
            {
                CheckHookHealth();
            }

            Thread.Sleep(10); // 10ms polling is fine for key state
        }
    }

    private void CheckHookHealth()
    {
        var lastTicks = Interlocked.Read(ref _lastCallbackTicks);
        var elapsed = Stopwatch.GetTimestamp() - lastTicks;

        if (elapsed > StaleThresholdTicks)
        {
            _reRegisterCount++;
            Log.Warning(
                "Keyboard hook appears dead (no callbacks for {Sec:F1}s). Re-registering on UI thread... (attempt #{Count})",
                (double)elapsed / Stopwatch.Frequency,
                _reRegisterCount + 1);

            // Reset key state to avoid phantom keys
            _altDown = false;
            _ctrlDown = false;

            // WH_KEYBOARD_LL requires a message pump — must re-register on the UI thread
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(RegisterHook);
        }
    }
}
