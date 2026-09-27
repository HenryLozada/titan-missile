using Titan.Engine.Games;
using Titan.Engine.Memory;
using Titan.Engine.Native;
using Titan.Engine.Processes;
using Titan.Engine.Session;
using Titan.Engine.SystemTweaks;

Console.OutputEncoding = System.Text.Encoding.UTF8;

string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
string[] rest = args.Skip(1).ToArray();
bool Has(string flag) => rest.Contains(flag, StringComparer.OrdinalIgnoreCase);
string? Value(string flag)
{
    int i = Array.FindIndex(rest, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
    return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
}

try
{
    return cmd switch
    {
        "status" => Status(),
        "topology" => Topology(),
        "plan" => Plan(),
        "purge" => RequireAdmin() ?? Purge(),
        "boost" => RequireAdmin() ?? await Boost(),
        "restore" => RequireAdmin() ?? Restore(),
        "guard" => RequireAdmin() ?? await Guard(),
        "games" => Games(),
        _ => Help(),
    };
}
catch (Exception ex)
{
    Ui.Error(ex.Message);
    return 1;
}

int? RequireAdmin()
{
    if (Privileges.IsElevated) return null;
    Ui.Error("Este comando necesita una terminal como Administrador.");
    return 5;
}

int Help()
{
    Console.WriteLine("""
    TITAN — motor de boost

      titan status                 Memoria real (Libre / Standby / Modificada), plan de energía, sesión abierta
      titan topology               Núcleos, P-cores / V-Cache y qué haría TITAN con un juego
      titan plan [--game X]        Vista previa: qué procesos se congelan / moderan / protegen
      titan purge [ops]            Limpieza estilo RAMMap (admin). Por defecto: Standby + Standby prioridad 0
                                     --all  --ws  --sys  --modified  --standby  --lowpri
      titan boost [--game X]       Activa el BOOST (admin). X = nombre (cs2.exe) o PID
                                     --hold  mantiene el boost + guardián de Standby hasta ENTER y restaura
                                     --no-power --no-services --no-freeze --no-throttle --no-mmcss --no-purge
      titan restore                Deshace la sesión abierta (también la que dejó un cierre inesperado)
      titan guard                  Guardián de Standby estilo ISLC hasta ENTER (admin)
      titan games                  Juegos instalados en todas las unidades (launchers + locales)
    """);
    return 0;
}

int Games()
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var games = GameLibrary.Scan(new SyncProgress<string>(m => Ui.Dim("  · " + m)));
    foreach (var g in games.GroupBy(g => g.Source).OrderBy(g => g.Key))
    {
        Ui.Title($"{g.Key} ({g.Count()})");
        foreach (var e in g) Ui.Row(e.Name.Length > 26 ? e.Name[..25] + "…" : e.Name, $"{e.InstallDir}  →  {(e.Exe is null ? "?" : Path.GetFileName(e.Exe))}");
    }
    Ui.Dim($"\n{games.Count} juegos en {sw.Elapsed.TotalSeconds:0.0} s");
    return 0;
}

int Status()
{
    var m = MemoryLists.Read();
    Ui.Title("Memoria");
    Ui.Row("Total", Ui.Bytes(m.TotalBytes));
    Ui.Row("En uso", Ui.Bytes(m.InUseBytes));
    if (m.Detailed)
    {
        Ui.Row("Libre (real)", Ui.Bytes(m.FreeBytes), accent: true);
        Ui.Row("Standby (caché)", $"{Ui.Bytes(m.StandbyBytes)}   prioridad 0: {Ui.Bytes(m.StandbyLowPriorityBytes)}");
        Ui.Row("Modificada", Ui.Bytes(m.ModifiedBytes));
    }
    else
    {
        Ui.Row("Disponible", Ui.Bytes(m.AvailableBytes));
        Ui.Dim("  (Desglose Libre/Standby solo como Administrador)");
    }

    Ui.Title("Sistema");
    var plan = PowerPlans.GetActive();
    Ui.Row("Plan de energía", $"{PowerPlans.FriendlyName(plan) ?? "?"}  {plan}");
    Ui.Row("Administrador", Privileges.IsElevated ? "sí" : "no");
    var journal = new Journal();
    Ui.Row("Sesión de boost", journal.HasOpenSession ? "ABIERTA — usa `titan restore`" : "ninguna", accent: journal.HasOpenSession);
    return 0;
}

int Topology()
{
    var t = CpuTopology.Read();
    Ui.Title("CPU");
    Ui.Row("Hilos lógicos", t.CpuSets.Count.ToString());
    Ui.Row("Núcleos", t.CpuSets.Select(c => (c.Group, c.CoreIndex)).Distinct().Count().ToString());
    Ui.Row("Híbrida", t.IsHybrid ? "sí" : "no");
    foreach (var cls in t.CpuSets.GroupBy(c => c.EfficiencyClass).OrderByDescending(g => g.Key))
        Ui.Row($"  Clase {cls.Key}", $"{cls.Count()} hilos");
    foreach (var c in t.L3Caches)
        Ui.Row("  L3", $"{c.SizeBytes / (1024 * 1024)} MB  (grupo {c.Group}, máscara 0x{c.Mask:X})");
    var plan = t.PlanForGame();
    Ui.Title("Estrategia para el juego");
    Console.WriteLine("  " + plan.Reason);
    return 0;
}

int Plan()
{
    var procs = ProcessOps.Snapshot();
    int? game = ResolveGame(procs);
    var tree = ProcessPolicy.GameTree(procs, game);
    var policy = new ProcessPolicy();
    var groups = procs.GroupBy(p => policy.Classify(p, Environment.ProcessId, tree))
                      .ToDictionary(g => g.Key, g => g.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                                                        .Select(n => n.Count() > 1 ? $"{n.Key} ×{n.Count()}" : n.Key)
                                                        .OrderBy(n => n).ToList());
    foreach (var (action, label) in new[] { (ProcessAction.Freeze, "Se congelan"), (ProcessAction.Throttle, "Se moderan (BelowNormal + EcoQoS)") })
    {
        Ui.Title(label);
        foreach (var n in groups.GetValueOrDefault(action) ?? []) Console.WriteLine("  " + n);
    }
    if (game is int g) Ui.Row("Árbol del juego protegido", string.Join(", ", tree.Select(pid => procs.First(p => p.Pid == pid).Name).Distinct()));
    return 0;
}

int Purge()
{
    var ops = PurgeOps.None;
    if (Has("--all")) ops = PurgeOps.All;
    if (Has("--ws")) ops |= PurgeOps.WorkingSets;
    if (Has("--sys")) ops |= PurgeOps.SystemWorkingSet;
    if (Has("--modified")) ops |= PurgeOps.ModifiedList;
    if (Has("--standby")) ops |= PurgeOps.Standby;
    if (Has("--lowpri")) ops |= PurgeOps.StandbyLowPriority;
    if (ops == PurgeOps.None) ops = PurgeOps.Boost;

    var r = MemoryPurger.Execute(ops);
    PrintPurge(r);
    return r.Steps.All(s => s.Ok) ? 0 : 2;
}

async Task<int> Boost()
{
    var procs = ProcessOps.Snapshot();
    var engine = new BoostEngine();
    if (engine.Journal.HasOpenSession)
    {
        Ui.Error("Hay una sesión de boost abierta. Ejecuta `titan restore` primero.");
        return 3;
    }

    var options = new BoostOptions
    {
        GamePid = ResolveGame(procs),
        PowerPlan = !Has("--no-power"),
        PauseServices = !Has("--no-services"),
        FreezeBackground = !Has("--no-freeze"),
        ThrottleBackground = !Has("--no-throttle"),
        MmcssTweaks = !Has("--no-mmcss"),
        Purge = Has("--no-purge") ? PurgeOps.None : PurgeOps.Boost,
    };

    var report = engine.Engage(options, new SyncProgress<BoostProgress>(p => Ui.Step(p.Step, p.Total, p.Label)));

    Ui.Title($"BOOST activo en {report.Elapsed.TotalMilliseconds:0} ms");
    if (report.GameName is not null) Ui.Row("Juego", report.GameName, accent: true);
    if (report.PowerPlan is Guid plan) Ui.Row("Plan de energía", PowerPlans.FriendlyName(plan) ?? plan.ToString());
    if (report.CorePlan is not null) Ui.Row("Núcleos", report.CorePlan.Reason);
    Ui.Row("Servicios en pausa", report.ServicesPaused.Count == 0 ? "—" : string.Join(", ", report.ServicesPaused));
    Ui.Row("Congelados", report.Frozen.Count == 0 ? "—" : string.Join(", ", report.Frozen));
    Ui.Row("Moderados", report.Throttled.Count == 0 ? "—" : string.Join(", ", report.Throttled));
    if (report.Purge is not null) PrintPurge(report.Purge);
    foreach (var w in report.Warnings) Ui.Warn(w);

    if (!Has("--hold"))
    {
        Ui.Dim("\nEl boost queda activo. Ejecuta `titan restore` para deshacerlo.");
        return 0;
    }

    Ui.Dim("\nGuardián de Standby activo. Pulsa ENTER para terminar y restaurar…");
    await RunGuardUntilEnter();
    return Restore();
}

int Restore()
{
    var engine = new BoostEngine();
    if (!engine.Journal.HasOpenSession)
    {
        Console.WriteLine("No hay ninguna sesión de boost abierta.");
        return 0;
    }
    var r = engine.Restore();
    Ui.Title($"Restaurado en {r.Elapsed.TotalMilliseconds:0} ms");
    Ui.Row("Cambios deshechos", r.Reverted.ToString(), accent: true);
    foreach (var f in r.Failures) Ui.Warn(f);
    return r.Failures.Count == 0 ? 0 : 4;
}

async Task<int> Guard()
{
    Ui.Dim("Guardián de Standby activo (purga si Libre < 1 GB y Standby > 1 GB). ENTER para salir…");
    await RunGuardUntilEnter();
    return 0;
}

async Task RunGuardUntilEnter()
{
    var guard = new StandbyGuard();
    guard.Purged += r => Ui.Dim($"  [{DateTime.Now:HH:mm:ss}] Standby purgada: +{Ui.Bytes((ulong)Math.Max(0, r.FreedBytes))} libres");
    using var cts = new CancellationTokenSource();
    var run = guard.RunAsync(cts.Token);
    await Task.Run(Console.ReadLine);
    cts.Cancel();
    await run;
}

int? ResolveGame(IReadOnlyList<ProcessEntry> procs)
{
    var g = Value("--game");
    if (g is null) return null;
    if (int.TryParse(g, out int pid)) return pid;
    string name = g.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? g : g + ".exe";
    var match = procs.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
    if (match.Count == 0) throw new InvalidOperationException($"No encuentro el proceso {name}.");
    // The game's main process is the one whose parent isn't another copy of itself.
    return match.FirstOrDefault(p => !match.Any(m => m.Pid == p.ParentPid))?.Pid ?? match[0].Pid;
}

void PrintPurge(PurgeResult r)
{
    Ui.Title("Memoria");
    Ui.Row("Libre antes → después", $"{Ui.Bytes(r.Before.FreeBytes)} → {Ui.Bytes(r.After.FreeBytes)}");
    Ui.Row("Standby antes → después", $"{Ui.Bytes(r.Before.StandbyBytes)} → {Ui.Bytes(r.After.StandbyBytes)}");
    Ui.Row("Liberado", (r.FreedBytes >= 0 ? "+" : "") + Ui.Bytes((ulong)Math.Abs(r.FreedBytes)), accent: true);
    foreach (var s in r.Steps.Where(s => !s.Ok)) Ui.Warn($"{s.Op}: {s.Error}");
}

sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

static class Ui
{
    const string Accent = "\u001b[38;2;61;255;176m", Dim_ = "\u001b[38;2;138;138;147m", Red = "\u001b[38;2;255;90;95m",
                 Yellow = "\u001b[38;2;255;200;87m", Bold = "\u001b[1m", Reset = "\u001b[0m";

    public static void Title(string t) => Console.WriteLine($"\n{Bold}{t}{Reset}");
    public static void Row(string k, string v, bool accent = false) =>
        Console.WriteLine($"  {Dim_}{k,-26}{Reset}{(accent ? Accent + Bold : "")}{v}{Reset}");
    public static void Step(int i, int n, string label) =>
        Console.WriteLine($"  {Accent}[{new string('■', i)}{new string('·', n - i)}]{Reset} {label}");
    public static void Dim(string t) => Console.WriteLine($"{Dim_}{t}{Reset}");
    public static void Warn(string t) => Console.WriteLine($"  {Yellow}! {t}{Reset}");
    public static void Error(string t) => Console.Error.WriteLine($"{Red}✖ {t}{Reset}");

    public static string Bytes(ulong b) => b switch
    {
        >= 1UL << 30 => $"{b / (double)(1UL << 30):0.00} GB",
        >= 1UL << 20 => $"{b / (double)(1UL << 20):0} MB",
        _ => $"{b / 1024.0:0} KB",
    };
}
