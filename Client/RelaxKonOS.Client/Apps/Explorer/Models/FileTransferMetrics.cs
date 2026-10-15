namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>Measures observed byte deltas; a new file, idle state or counter reset starts a new interval.</summary>
public sealed class FileTransferMetrics
{
    private readonly Queue<(double Time, long Bytes)> _samples = new();
    private readonly Queue<double> _history = new();
    private string? _key;
    private long _bytes;
    private double _time;
    private bool _initialized;
    public double BytesPerSecond { get; private set; }
    public bool IsReady => _samples.Count >= 2 && _samples.Last().Time - _samples.First().Time >= 1;
    public IReadOnlyList<double> History => _history.ToArray();
    public void Observe(long bytes, string? key, bool running, double time)
    {
        if (!running || !_initialized || key != _key || bytes < _bytes || time < _time)
        {
            _samples.Clear(); _history.Clear(); BytesPerSecond = 0;
            _initialized = running; _key = key; _bytes = bytes; _time = time;
            if (running) _samples.Enqueue((time, bytes));
            return;
        }
        if (time - _time < 0.5) return;
        _bytes = bytes; _time = time;
        _samples.Enqueue((time, bytes));
        while (_samples.Count > 2 && time - _samples.First().Time > 5) _samples.Dequeue();
        var first = _samples.First();
        BytesPerSecond = Math.Max(0, (bytes - first.Bytes) / Math.Max(0.001, time - first.Time));
        _history.Enqueue(BytesPerSecond);
        while (_history.Count > 60) _history.Dequeue();
    }
    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return $"{Math.Max(0, bytes):0.#} {units[unit]}";
    }
}
