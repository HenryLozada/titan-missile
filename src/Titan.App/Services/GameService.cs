using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;
using Titan.Engine.Games;
using Titan.Engine.Processes;

namespace Titan.App.Services;

public sealed record LibraryState(List<GameEntry> Games, List<string> Hidden, DateTimeOffset? ScannedAt);

public sealed record AppSettings
{
    public bool Sound { get; set; } = true;
    public bool MinimizeOnAutoBoost { get; set; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(LibraryState))]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppJson : JsonSerializerContext;

public static class AppData
{
    public static string Dir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TITAN");

    public static AppSettings LoadSettings()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(Dir, "settings.json")), AppJson.Default.AppSettings) ?? new(); }
        catch { return new(); }
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "settings.json"), JsonSerializer.Serialize(s, AppJson.Default.AppSettings));
    }
}

/// <summary>
/// The library (cached, rescannable), the process watcher that drives auto-boost, launching, and measured sessions.
/// </summary>
public sealed class GameService : IDisposable
{
    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _watch;
    private readonly string _path = Path.Combine(AppData.Dir, "games.json");
    private readonly Dictionary<int, string?> _paths = [];
    private LibraryState _state;
    private bool _polling;
    private string? _pendingBoost;          // launched from TITAN: boost it even if AUTO is off
    private string? _pendingMeasure;        // "measure without boost" armed for this game

    public IReadOnlyList<GameEntry> Games => _state.Games.Where(g => !_state.Hidden.Contains(g.Id)).ToList();
    public DateTimeOffset? ScannedAt => _state.ScannedAt;
    public bool Scanning { get; private set; }

    /// <summary>Currently running library game, if any.</summary>
    public (GameEntry Game, int Pid)? Running { get; private set; }

    public event Action? LibraryChanged;
    public event Action<string>? ScanProgress;
    /// <summary>(game, pid, boost?, measure-only?)</summary>
    public event Action<GameEntry, int, bool, bool>? GameStarted;
    public event Action<GameEntry>? GameExited;

    public GameService(DispatcherQueue ui)
    {
        _ui = ui;
        try { _state = JsonSerializer.Deserialize(File.ReadAllText(_path), AppJson.Default.LibraryState) ?? new([], [], null); }
        catch { _state = new([], [], null); }
        _watch = ui.CreateTimer();
        _watch.Interval = TimeSpan.FromSeconds(2);
        _watch.Tick += (_, _) => Poll();
        _watch.Start();
        if (_state.ScannedAt is null) _ = RescanAsync();
    }

    public async Task RescanAsync()
    {
        if (Scanning) return;
        Scanning = true;
        LibraryChanged?.Invoke();
        var progress = new Progress<string>(m => ScanProgress?.Invoke(m));
        var found = await Task.Run(() => GameLibrary.Scan(progress));
        var auto = _state.Games.Where(g => g.AutoBoost).Select(g => g.Id).ToHashSet();
        var manual = _state.Games.Where(g => g.Source == GameSource.Manual).ToList();
        var merged = found.Select(g => g with { AutoBoost = auto.Contains(g.Id) }).Concat(manual).ToList();
        _state = _state with { Games = merged, ScannedAt = DateTimeOffset.Now };
        Save();
        Scanning = false;
        LibraryChanged?.Invoke();
    }

    public void SetAuto(GameEntry game, bool on)
    {
        var g = _state.Games.FirstOrDefault(x => x.Id == game.Id);
        if (g is null) return;
        g.AutoBoost = on;
        Save();
    }

    public void Hide(GameEntry game)
    {
        _state.Hidden.Add(game.Id);
        Save();
        LibraryChanged?.Invoke();
    }

    public void AddManual(string exe)
    {
        string dir = Path.GetDirectoryName(exe)!;
        var entry = new GameEntry
        {
            Id = "manual:" + exe.ToLowerInvariant(), Name = Path.GetFileNameWithoutExtension(exe), Source = GameSource.Manual,
            InstallDir = dir, Exe = exe, AutoBoost = true,
        };
        _state.Games.RemoveAll(g => g.Id == entry.Id);
        _state.Games.Add(entry);
        Save();
        LibraryChanged?.Invoke();
    }

    /// <summary>Starts the game through its launcher (or directly) and boosts it as soon as it shows up.</summary>
    public void Launch(GameEntry g)
    {
        _pendingBoost = g.Id;
        var psi = g.LaunchUri is { } uri
            ? new ProcessStartInfo(uri) { UseShellExecute = true }
            : new ProcessStartInfo(g.Exe ?? throw new InvalidOperationException("No encuentro el ejecutable.")) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(g.Exe), Arguments = g.LaunchArgs ?? "" };
        Process.Start(psi);
    }

    /// <summary>Measure the next (or current) session of this game without boosting, for the comparison.</summary>
    public void MeasureWithoutBoost(GameEntry g)
    {
        _pendingMeasure = g.Id;
        if (Running is { } r && r.Game.Id == g.Id) GameStarted?.Invoke(r.Game, r.Pid, false, true);
    }

    public bool IsGameProcess(int pid)
    {
        if (Running is not { } r) return false;
        string? path;
        lock (_paths) _paths.TryGetValue(pid, out path);
        return path is not null && path.StartsWith(r.Game.InstallDir, StringComparison.OrdinalIgnoreCase);
    }

    private void Poll()
    {
        if (_polling) return;
        _polling = true;
        var games = Games.Where(g => g.InstallDir.Length > 3).ToList();
        Task.Run(() =>
        {
            var procs = ProcessOps.Snapshot();
            var alive = procs.Select(p => p.Pid).ToHashSet();
            lock (_paths)
            {
                foreach (var dead in _paths.Keys.Where(k => !alive.Contains(k)).ToList()) _paths.Remove(dead);
                foreach (var p in procs)
                    if (p.SessionId != 0 && !_paths.ContainsKey(p.Pid)) _paths[p.Pid] = ProcessOps.ImagePath(p.Pid);
            }
            (GameEntry, int)? hit = null;
            foreach (var p in procs)
            {
                string? path;
                lock (_paths) _paths.TryGetValue(p.Pid, out path);
                if (path is null) continue;
                var g = games.FirstOrDefault(x => path.StartsWith(x.InstallDir.Replace('/', '\\').TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                                                 && !path.Contains("Riot Client", StringComparison.OrdinalIgnoreCase));
                if (g is not null) { hit = (g, p.Pid); if (string.Equals(path, g.Exe, StringComparison.OrdinalIgnoreCase)) break; }
            }
            _ui.TryEnqueue(() => { Apply(hit); _polling = false; });
        });
    }

    private void Apply((GameEntry Game, int Pid)? hit)
    {
        if (hit is { } h && Running is null)
        {
            Running = h;
            bool boost = h.Game.AutoBoost || _pendingBoost == h.Game.Id;
            bool measureOnly = !boost && _pendingMeasure == h.Game.Id;
            _pendingBoost = null;
            if (measureOnly) _pendingMeasure = null;
            GameStarted?.Invoke(h.Game, h.Pid, boost, measureOnly);
        }
        else if (hit is null && Running is { } r)
        {
            Running = null;
            GameExited?.Invoke(r.Game);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(AppData.Dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_state, AppJson.Default.LibraryState));
    }

    public void Dispose() => _watch.Stop();
}
