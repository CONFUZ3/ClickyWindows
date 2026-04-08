namespace ClickyWindows.Overlay;

/// <summary>
/// Cubic Bezier arc animation (De Casteljau) from current cursor position to a POINT target.
/// </summary>
public class FlightPathAnimator
{
    private readonly System.Windows.Point _start;
    private readonly System.Windows.Point _end;
    private readonly System.Windows.Point _control1;
    private readonly System.Windows.Point _control2;
    private readonly double _duration;

    private double _elapsed;
    private bool _isFlying;

    public bool IsFlying => _isFlying;
    public System.Windows.Point CurrentPosition { get; private set; }

    public FlightPathAnimator(System.Windows.Point start, System.Windows.Point end, double duration = 0.4)
    {
        _start = start;
        _end = end;
        _duration = duration;
        CurrentPosition = start;

        // Build an arc: control points create a swooping Bezier curve
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double perpX = -dy * 0.25;
        double perpY = dx * 0.25;

        _control1 = new System.Windows.Point(start.X + dx * 0.25 + perpX, start.Y + dy * 0.25 + perpY);
        _control2 = new System.Windows.Point(start.X + dx * 0.75 + perpX, start.Y + dy * 0.75 + perpY);
    }

    public void Start()
    {
        _elapsed = 0;
        _isFlying = true;
        CurrentPosition = _start;
    }

    public void Update(double deltaSeconds)
    {
        if (!_isFlying) return;

        _elapsed += deltaSeconds;
        double t = Math.Min(_elapsed / _duration, 1.0);

        // Smooth ease-in-out
        t = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

        CurrentPosition = CubicBezier(_start, _control1, _control2, _end, t);

        if (_elapsed >= _duration)
            _isFlying = false;
    }

    public void Cancel() => _isFlying = false;

    private static System.Windows.Point CubicBezier(
        System.Windows.Point p0, System.Windows.Point p1,
        System.Windows.Point p2, System.Windows.Point p3, double t)
    {
        double u = 1 - t;
        double x = u * u * u * p0.X
                 + 3 * u * u * t * p1.X
                 + 3 * u * t * t * p2.X
                 + t * t * t * p3.X;
        double y = u * u * u * p0.Y
                 + 3 * u * u * t * p1.Y
                 + 3 * u * t * t * p2.Y
                 + t * t * t * p3.Y;
        return new System.Windows.Point(x, y);
    }
}
