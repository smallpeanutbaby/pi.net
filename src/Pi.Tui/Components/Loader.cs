namespace Pi.Tui.Components;

/// <summary>Options for the <see cref="Loader"/> spinner indicator (port of <c>LoaderIndicatorOptions</c>).</summary>
public sealed class LoaderIndicatorOptions
{
    /// <summary>Animation frames. Use an empty array to hide the indicator.</summary>
    public string[]? Frames { get; init; }

    /// <summary>Frame interval in milliseconds for animated indicators.</summary>
    public int? IntervalMs { get; init; }
}

/// <summary>
/// Loader component that updates with an optional spinning animation (port of <c>components/loader.ts</c>).
/// </summary>
/// <remarks>
/// Deviation from TS (T13): the <c>setInterval</c> animation timer becomes a
/// <see cref="System.Threading.Timer"/>, so frame advancement and <c>setText</c> can run on a thread-pool
/// thread while the render timer reads the same state on another. The tick body is guarded by a lock, and
/// <see cref="ITui.RequestRender"/> is itself thread-safe.
/// </remarks>
public class Loader : Text
{
    private static readonly string[] DefaultFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private const int DefaultIntervalMs = 80;

    private readonly object _gate = new();
    private readonly ITui? _ui;
    private readonly Func<string, string> _spinnerColorFn;
    private readonly Func<string, string> _messageColorFn;

    private string[] _frames = [.. DefaultFrames];
    private int _intervalMs = DefaultIntervalMs;
    private int _currentFrame;
    private Timer? _intervalId;
    private bool _renderIndicatorVerbatim;
    private string _message;

    public Loader(
        ITui ui,
        Func<string, string> spinnerColorFn,
        Func<string, string> messageColorFn,
        string message = "Loading...",
        LoaderIndicatorOptions? indicator = null)
        : base("", 1, 0)
    {
        _ui = ui;
        _spinnerColorFn = spinnerColorFn;
        _messageColorFn = messageColorFn;
        _message = message;
        SetIndicator(indicator);
    }

    public override string[] Render(int width)
    {
        var lines = base.Render(width);
        var result = new string[lines.Length + 1];
        result[0] = "";
        Array.Copy(lines, 0, result, 1, lines.Length);
        return result;
    }

    public void Start()
    {
        UpdateDisplay();
        RestartAnimation();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _intervalId?.Dispose();
            _intervalId = null;
        }
    }

    public void SetMessage(string message)
    {
        _message = message;
        UpdateDisplay();
    }

    public override void Invalidate()
    {
        base.Invalidate();
        UpdateDisplay();
    }

    public void SetIndicator(LoaderIndicatorOptions? indicator = null)
    {
        // Assign the frame table under the gate: a tick already dispatched by the previous timer
        // takes the same lock and must see either the old frames or the new ones, never a half-applied
        // pair (frames swapped to [] while the tick still divides by their length, see T35).
        lock (_gate)
        {
            _renderIndicatorVerbatim = indicator is not null;
            _frames = indicator?.Frames is { } frames ? [.. frames] : [.. DefaultFrames];
            _intervalMs = indicator?.IntervalMs is > 0 ? indicator.IntervalMs.Value : DefaultIntervalMs;
            _currentFrame = 0;
        }
        Start();
    }

    private void RestartAnimation()
    {
        Stop();
        lock (_gate)
        {
            if (_frames.Length <= 1)
            {
                return;
            }

            _intervalId = new Timer(
                _ =>
                {
                    // The whole tick body — frame advancement *and* UpdateDisplay — must run under
                    // the gate: Stop() takes the same lock, so a callback that slipped past the
                    // dispose could otherwise call the color functions after the theme was torn
                    // down (the tick half outside the lock was a real race).
                    lock (_gate)
                    {
                        // SetIndicator may have swapped the frames since this tick was scheduled; an
                        // empty table hides the indicator and has nothing to advance (`% 0` would throw
                        // DivideByZeroException on the timer thread and kill the process).
                        var frames = _frames;
                        if (frames.Length == 0)
                        {
                            return;
                        }
                        _currentFrame = (_currentFrame + 1) % frames.Length;
                        UpdateDisplay();
                    }
                },
                null,
                _intervalMs,
                _intervalMs);
        }
    }

    protected string GetRenderedIndicator()
    {
        string frame;
        bool verbatim;
        lock (_gate)
        {
            frame = _currentFrame < _frames.Length ? _frames[_currentFrame] : "";
            verbatim = _renderIndicatorVerbatim;
        }
        return verbatim ? frame : _spinnerColorFn(frame);
    }

    private void UpdateDisplay()
    {
        var renderedFrame = GetRenderedIndicator();
        var indicator = renderedFrame.Length > 0 ? $"{renderedFrame} " : "";
        SetText($"{indicator}{_messageColorFn(_message)}");
        _ui?.RequestRender();
    }
}
