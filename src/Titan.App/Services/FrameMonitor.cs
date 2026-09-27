using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace Titan.App.Services;

public sealed record FrameStats(double Seconds, int Frames, double AvgFps, double Low1Fps, int Stutters, float[] FrameTimes);

/// <summary>
/// Frame capture the way PresentMon does it: an ETW real-time session on the DXGI and D3D9 providers, timing the
/// interval between consecutive Present calls of the game. Needs admin. DirectX 9/10/11/12 titles; Vulkan/OpenGL
/// games that don't present through DXGI are not seen yet.
/// </summary>
public sealed class FrameMonitor : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42, D3D9PresentStart = 1;

    private readonly Func<int, bool> _isGame;
    private readonly Dictionary<int, (double Last, List<float> Times)> _byPid = [];
    private readonly object _gate = new();
    private TraceEventSession? _session;
    private DateTime _started;

    public FrameMonitor(Func<int, bool> isGame) => _isGame = isGame;

    public bool Running => _session is not null;

    public void Start()
    {
        _started = DateTime.UtcNow;
        _session = new TraceEventSession("TITAN-Frames") { StopOnDispose = true };
        _session.EnableProvider(Dxgi, TraceEventLevel.Informational, ulong.MaxValue);
        _session.EnableProvider(D3D9, TraceEventLevel.Informational, ulong.MaxValue);
        _session.Source.AllEvents += OnEvent;
        var s = _session;
        new Thread(() => { try { s.Source.Process(); } catch { } }) { IsBackground = true, Name = "TITAN frames" }.Start();
    }

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        if (!((e.ProviderGuid == Dxgi && id == DxgiPresentStart) || (e.ProviderGuid == D3D9 && id == D3D9PresentStart))) return;
        int pid = e.ProcessID;
        if (!_isGame(pid)) return;
        double ts = e.TimeStampRelativeMSec;
        lock (_gate)
        {
            if (_byPid.TryGetValue(pid, out var s))
            {
                double dt = ts - s.Last;
                if (dt > 0 && dt < 2000) s.Times.Add((float)dt);
                _byPid[pid] = (ts, s.Times);
            }
            else _byPid[pid] = (ts, []);
        }
    }

    /// <summary>Live FPS over the last ~second of the busiest process (for the overlay line).</summary>
    public double LiveFps()
    {
        lock (_gate)
        {
            var times = _byPid.Values.OrderByDescending(v => v.Times.Count).FirstOrDefault().Times;
            if (times is null || times.Count == 0) return 0;
            double sum = 0; int n = 0;
            for (int i = times.Count - 1; i >= 0 && sum < 1000; i--) { sum += times[i]; n++; }
            return sum > 0 ? n * 1000 / sum : 0;
        }
    }

    public FrameStats Stop()
    {
        _session?.Dispose();
        _session = null;
        List<float> times;
        lock (_gate) times = _byPid.Values.OrderByDescending(v => v.Times.Count).FirstOrDefault().Times ?? [];
        double seconds = (DateTime.UtcNow - _started).TotalSeconds;
        if (times.Count < 10) return new FrameStats(seconds, times.Count, 0, 0, 0, []);

        var sorted = times.OrderBy(t => t).ToArray();
        double mean = times.Average(t => (double)t);
        double p99 = sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(sorted.Length * .99) - 1)];
        int stutters = times.Count(t => t > 50);
        return new FrameStats(seconds, times.Count, 1000 / mean, 1000 / p99, stutters, Downsample(times, 480));
    }

    /// <summary>Keeps the worst frame of each bucket so spikes survive the downsampling.</summary>
    private static float[] Downsample(List<float> t, int points)
    {
        if (t.Count <= points) return [.. t];
        var result = new float[points];
        double step = (double)t.Count / points;
        for (int i = 0; i < points; i++)
        {
            int a = (int)(i * step), b = Math.Min(t.Count, (int)((i + 1) * step));
            float worst = 0;
            for (int j = a; j < b; j++) worst = Math.Max(worst, t[j]);
            result[i] = worst;
        }
        return result;
    }

    public void Dispose() => _session?.Dispose();
}

public sealed record SessionReport
{
    public required string Id { get; init; }
    public required string GameId { get; init; }
    public required string GameName { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required double Seconds { get; init; }
    public required bool Boosted { get; init; }
    public required double AvgFps { get; init; }
    public required double Low1Fps { get; init; }
    public required int Stutters { get; init; }
    public required float[] FrameTimes { get; init; }
    public double FreedGb { get; init; }
}

[JsonSerializable(typeof(List<SessionReport>))]
internal partial class ReportJson : JsonSerializerContext;

/// <summary>Sessions live in %AppData%\TITAN\reports.json (per user, readable, shareable).</summary>
public sealed class ReportStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TITAN", "reports.json");
    public List<SessionReport> All { get; }

    public ReportStore()
    {
        try { All = File.Exists(_path) ? JsonSerializer.Deserialize(File.ReadAllText(_path), ReportJson.Default.ListSessionReport) ?? [] : []; }
        catch { All = []; }
    }

    public void Add(SessionReport r)
    {
        All.Insert(0, r);
        if (All.Count > 200) All.RemoveRange(200, All.Count - 200);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(All, ReportJson.Default.ListSessionReport));
    }
}
