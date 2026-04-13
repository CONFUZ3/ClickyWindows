using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using ClickyWindows.Interop;
using ClickyWindows.Screen;
using Serilog;

namespace ClickyWindows.Overlay;

/// <summary>
/// Manages one OverlayWindow per monitor. Handles cursor tracking via DispatcherTimer (NOT CompositionTarget.Rendering).
/// </summary>
public class OverlayManager : IDisposable
{
    private readonly List<OverlayWindow> _overlays = new();
    private readonly List<MonitorInfo> _monitors = new();
    private DispatcherTimer? _renderTimer;
    private readonly Stopwatch _stopwatch = new();

    private OverlayWindow? _activeOverlay; // the overlay containing the cursor
    private AppState _state = AppState.Idle;
    private double _cursorX, _cursorY; // physical cursor coords

    // --- Animation state ---

    private FlightPathAnimator? _flightAnimator;
    private FlightPhase _flightPhase = FlightPhase.None;

    // Position where the outbound flight landed — held during Speaking / hold phase.
    private System.Windows.Point _holdPosition;

    // Accumulates time at the target before triggering a return flight.
    private double _holdElapsed;

    // Cursor position when return flight began, in overlay-local logical coords.
    // Used to detect large mouse movements that should cancel the return early.
    private System.Windows.Point _returnStartCursorLocal;

    // How long (seconds) to hold at the target before flying back.
    private const double HoldDurationSeconds = 3.0;

    // Duration of the return flight to the cursor.
    private const double ReturnFlightDurationSeconds = 0.4;

    // If the mouse moves more than this many logical pixels during the return flight,
    // cancel the animation early and snap to cursor — the user has moved on.
    private const double MouseCancelThresholdLogical = 100.0;

    private double[] _waveformLevels = new double[5];
    private string _speechBubbleText = "";
    private bool _showSpeechBubble;

    public void Initialize()
    {
        _monitors.AddRange(MonitorEnumerator.GetMonitors());

        if (_monitors.Count == 0)
        {
            Log.Warning("No monitors enumerated — using fallback primary monitor");

            // SystemParameters returns logical (DIP) units, not physical pixels.
            // At 125% DPI, PrimaryScreenWidth is 1536 even though the physical
            // screen is 1920px wide. GetSystemMetrics(SM_CXSCREEN) always returns
            // physical pixels regardless of DPI scaling.
            int physicalWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            int physicalHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            double logicalWidth = SystemParameters.PrimaryScreenWidth;
            double logicalHeight = SystemParameters.PrimaryScreenHeight;

            // DpiScale = physical/logical, e.g. 1920/1536 = 1.25 at 125% DPI
            double dpiScale = logicalWidth > 0 ? physicalWidth / logicalWidth : 1.0;
            uint dpiValue = (uint)Math.Round(dpiScale * 96);

            Log.Warning("Fallback monitor: physical={W}x{H} logical={LW}x{LH} scale={S:F2}",
                physicalWidth, physicalHeight, logicalWidth, logicalHeight, dpiScale);

            _monitors.Add(new MonitorInfo
            {
                Bounds = new Rect(0, 0, logicalWidth, logicalHeight),
                PhysicalBounds = new System.Drawing.Rectangle(0, 0, physicalWidth, physicalHeight),
                DpiScale = dpiScale,
                DpiX = dpiValue,
                DpiY = dpiValue,
                IsPrimary = true,
                Index = 0
            });
        }

        foreach (var monitor in _monitors)
        {
            var overlay = new OverlayWindow(monitor);
            _overlays.Add(overlay);
            overlay.Show();
            Log.Information("Created overlay for monitor {Index} ({Bounds})", monitor.Index, monitor.Bounds);
        }

        _stopwatch.Start();

        // 16ms timer = ~62.5fps (NOT CompositionTarget.Rendering which throttles to ~50fps)
        _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        var elapsed = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();

        UpdateCursorPosition();
        UpdateFlightLifecycle(elapsed);
        RenderToOverlays();
    }

    private void UpdateCursorPosition()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return;
        _cursorX = pt.X;
        _cursorY = pt.Y;

        // Determine which monitor contains the cursor
        var activeMonitor = FindMonitorForPhysicalPoint(pt.X, pt.Y);
        if (activeMonitor != null)
        {
            var idx = _monitors.IndexOf(activeMonitor);
            _activeOverlay = idx >= 0 && idx < _overlays.Count ? _overlays[idx] : _overlays[0];
        }
        else if (_overlays.Count > 0)
        {
            _activeOverlay = _overlays[0];
        }
    }

    private MonitorInfo? FindMonitorForPhysicalPoint(int x, int y)
    {
        foreach (var m in _monitors)
        {
            if (m.PhysicalBounds.Contains(x, y))
                return m;
        }
        return _monitors.Count > 0 ? _monitors[0] : null;
    }

    private void UpdateFlightLifecycle(double deltaSeconds)
    {
        switch (_flightPhase)
        {
            case FlightPhase.FlyingToTarget:
                _flightAnimator!.Update(deltaSeconds);
                if (!_flightAnimator.IsFlying)
                {
                    // Outbound flight landed. Save position and start holding.
                    _holdPosition = _flightAnimator.EndPosition;
                    _holdElapsed = 0;
                    _flightPhase = FlightPhase.HoldingAtTarget;
                    Log.Debug("Flight landed at ({X:F1},{Y:F1}), holding", _holdPosition.X, _holdPosition.Y);
                }
                break;

            case FlightPhase.HoldingAtTarget:
                // Only count down the hold timer once we're back in Idle (Speaking has ended).
                // While the AI is still speaking, the triangle stays planted at the target indefinitely.
                if (_state == AppState.Idle)
                {
                    _holdElapsed += deltaSeconds;
                    if (_holdElapsed >= HoldDurationSeconds)
                        StartReturnFlight();
                }
                break;

            case FlightPhase.ReturningToCursor:
                _flightAnimator!.Update(deltaSeconds);

                // Cancel the return early if the user has moved the mouse significantly —
                // they've moved on and the triangle should just follow the cursor again.
                var monitor = _activeOverlay?.Monitor ?? (_monitors.Count > 0 ? _monitors[0] : null);
                if (monitor != null)
                {
                    double currentLocalX = (_cursorX - monitor.PhysicalBounds.Left) / monitor.DpiScale;
                    double currentLocalY = (_cursorY - monitor.PhysicalBounds.Top) / monitor.DpiScale;
                    double dx = currentLocalX - _returnStartCursorLocal.X;
                    double dy = currentLocalY - _returnStartCursorLocal.Y;
                    double distanceMoved = Math.Sqrt(dx * dx + dy * dy);
                    if (distanceMoved > MouseCancelThresholdLogical)
                    {
                        Log.Debug("Return flight cancelled — mouse moved {D:F0}px", distanceMoved);
                        ClearFlightAnimator();
                        break;
                    }
                }

                if (!_flightAnimator.IsFlying)
                {
                    Log.Debug("Return flight complete, resuming cursor following");
                    ClearFlightAnimator();
                }
                break;
        }
    }

    private void StartReturnFlight()
    {
        // Build the return flight from the held position back to the current cursor position.
        var monitor = _activeOverlay?.Monitor ?? (_monitors.Count > 0 ? _monitors[0] : null);
        if (monitor == null)
        {
            ClearFlightAnimator();
            return;
        }

        double cursorLocalX = (_cursorX - monitor.PhysicalBounds.Left) / monitor.DpiScale;
        double cursorLocalY = (_cursorY - monitor.PhysicalBounds.Top) / monitor.DpiScale;
        _returnStartCursorLocal = new System.Windows.Point(cursorLocalX, cursorLocalY);

        _flightAnimator = new FlightPathAnimator(
            _holdPosition,
            new System.Windows.Point(cursorLocalX, cursorLocalY),
            ReturnFlightDurationSeconds);
        _flightAnimator.Start();
        _flightPhase = FlightPhase.ReturningToCursor;

        Log.Debug("Return flight starting from ({HX:F1},{HY:F1}) to cursor ({CX:F1},{CY:F1})",
            _holdPosition.X, _holdPosition.Y, cursorLocalX, cursorLocalY);
    }

    private void ClearFlightAnimator()
    {
        _flightAnimator = null;
        _flightPhase = FlightPhase.None;
    }

    private void RenderToOverlays()
    {
        if (_activeOverlay == null) return;

        var monitor = _activeOverlay.Monitor;

        // Convert physical cursor to overlay-local logical coords
        double localX = (_cursorX - monitor.PhysicalBounds.Left) / monitor.DpiScale;
        double localY = (_cursorY - monitor.PhysicalBounds.Top) / monitor.DpiScale;

        // While a flight lifecycle is active, override the triangle position.
        if (_flightPhase != FlightPhase.None)
        {
            if (_flightPhase == FlightPhase.HoldingAtTarget)
            {
                // No animator is running during the hold — use the saved landing position.
                localX = _holdPosition.X;
                localY = _holdPosition.Y;
            }
            else if (_flightAnimator != null)
            {
                // Use the in-progress animation position (outbound or return flight).
                var pos = _flightAnimator.CurrentPosition;
                localX = pos.X;
                localY = pos.Y;
            }
        }

        _activeOverlay.SetTrianglePosition(localX, localY);

        switch (_state)
        {
            case AppState.Recording:
                _activeOverlay.SetWaveform(true, _waveformLevels);
                _activeOverlay.SetSpinner(false, 0, 0);
                break;

            case AppState.Processing:
                _activeOverlay.SetWaveform(false, Array.Empty<double>());
                _activeOverlay.SetSpinner(true, localX, localY);
                _activeOverlay.AdvanceSpinner(6.0); // ~6° per frame at 60fps
                break;

            case AppState.Speaking:
                _activeOverlay.SetWaveform(false, Array.Empty<double>());
                _activeOverlay.SetSpinner(false, 0, 0);
                if (_showSpeechBubble)
                    _activeOverlay.SetSpeechBubble(true, _speechBubbleText, localX, localY);
                break;

            default: // Idle
                _activeOverlay.SetWaveform(false, Array.Empty<double>());
                _activeOverlay.SetSpinner(false, 0, 0);
                _activeOverlay.SetSpeechBubble(false, "", 0, 0);
                break;
        }

        // Hide waveform/spinner/bubble on all non-active overlays
        foreach (var overlay in _overlays)
        {
            if (overlay == _activeOverlay) continue;
            overlay.SetWaveform(false, Array.Empty<double>());
            overlay.SetSpinner(false, 0, 0);
            overlay.SetSpeechBubble(false, "", 0, 0);
        }
    }

    public void SetState(AppState state)
    {
        _state = state;

        // When a new recording starts, immediately cancel any active flight so the triangle
        // snaps back to cursor tracking — the user has moved on to a new interaction.
        if (state == AppState.Recording && _flightPhase != FlightPhase.None)
        {
            Log.Debug("Recording started — cancelling active flight (phase: {Phase})", _flightPhase);
            ClearFlightAnimator();
        }

        // When transitioning back to Idle after Speaking, the hold timer in UpdateFlightLifecycle
        // will now start counting — no explicit action needed here. The lifecycle finishes itself.
    }

    public void SetWaveformLevels(double[] levels)
    {
        _waveformLevels = levels;
    }

    public void SetSpeechBubble(string text)
    {
        _speechBubbleText = text;
        _showSpeechBubble = !string.IsNullOrEmpty(text);
    }

    public void StartFlightTo(double targetScreenX, double targetScreenY, MonitorInfo targetMonitor)
    {
        // Convert physical screen coords to overlay-local logical coords
        double localX = (targetScreenX - targetMonitor.PhysicalBounds.Left) / targetMonitor.DpiScale;
        double localY = (targetScreenY - targetMonitor.PhysicalBounds.Top) / targetMonitor.DpiScale;

        double startLocalX = (_cursorX - targetMonitor.PhysicalBounds.Left) / targetMonitor.DpiScale;
        double startLocalY = (_cursorY - targetMonitor.PhysicalBounds.Top) / targetMonitor.DpiScale;

        _activeOverlay = _overlays.Find(o => o.Monitor == targetMonitor) ?? _overlays[0];
        _flightAnimator = new FlightPathAnimator(
            new System.Windows.Point(startLocalX, startLocalY),
            new System.Windows.Point(localX, localY),
            duration: 0.4);
        _flightAnimator.Start();
        _flightPhase = FlightPhase.FlyingToTarget;
        _holdElapsed = 0;
    }

    public List<MonitorInfo> GetMonitors() => _monitors;

    public void Dispose()
    {
        _renderTimer?.Stop();
        foreach (var overlay in _overlays)
            overlay.Close();
        _overlays.Clear();
    }
}

/// <summary>
/// Phases of the triangle pointing lifecycle.
/// None = following cursor normally.
/// FlyingToTarget = outbound Bezier animation toward the pointed element.
/// HoldingAtTarget = triangle planted at target while AI is speaking (and briefly after).
/// ReturningToCursor = return Bezier animation back to current cursor position.
/// </summary>
public enum FlightPhase
{
    None,
    FlyingToTarget,
    HoldingAtTarget,
    ReturningToCursor
}

public enum AppState
{
    Idle,
    Recording,
    Processing,
    Speaking
}
