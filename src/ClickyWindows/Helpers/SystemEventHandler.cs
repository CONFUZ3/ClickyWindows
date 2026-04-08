using Microsoft.Win32;
using Serilog;

namespace ClickyWindows.Helpers;

/// <summary>
/// Subscribes to system events: sleep/wake, session lock/unlock.
/// </summary>
public class SystemEventHandler : IDisposable
{
    public event Action? SystemResumed;
    public event Action? SessionLocked;
    public event Action? SessionUnlocked;
    public event Action? DisplayChanged;

    public SystemEventHandler()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Log.Information("System resumed from sleep");
            SystemResumed?.Invoke();
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                Log.Information("Session locked");
                SessionLocked?.Invoke();
                break;
            case SessionSwitchReason.SessionUnlock:
                Log.Information("Session unlocked");
                SessionUnlocked?.Invoke();
                break;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Log.Information("Display settings changed");
        DisplayChanged?.Invoke();
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}
