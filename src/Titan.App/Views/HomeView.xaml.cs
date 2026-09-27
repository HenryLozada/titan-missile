using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Titan.App.Rendering;
using Titan.App.Services;
using Titan.Engine.Memory;
using Titan.Engine.Session;

namespace Titan.App.Views;

public sealed partial class HomeView : UserControl
{
    private enum Phase { Idle, Running, Done, Release }

    private static readonly string[] StepNames = ["energía", "servicios", "prioridad del juego", "segundo plano", "planificador", "caché"];

    private readonly EntityAnimator _anim = new();
    private readonly Rectangle[] _segments = new Rectangle[6];
    private readonly DispatcherTimer _typer = new() { Interval = TimeSpan.FromMilliseconds(18) };
    private ShaderSurface<EntityParams>? _surface;
    private EngineHost? _host;
    private Phase _phase = Phase.Idle;
    private int _step;
    private string _voice = "";
    private int _typed;
    private string? _override;          // a one-off line (purge result, errors) shown instead of the idle line

    /// <summary>Asks the window for the golden scan sweep.</summary>
    public event Action? ScanRequested;

    public bool IsBoosted => _phase == Phase.Done;

    public HomeView()
    {
        InitializeComponent();
        for (int i = 0; i < 6; i++)
        {
            _segments[i] = new Rectangle { Width = 22, Height = 2 };
            Segments.Children.Add(_segments[i]);
        }
        _typer.Tick += (_, _) =>
        {
            _typed = Math.Min(_voice.Length, _typed + 2);
            Voice.Text = _voice[.._typed];
            if (_typed >= _voice.Length) _typer.Stop();
        };
        Loaded += (_, _) =>
        {
            _surface ??= new ShaderSurface<EntityParams>(EntityPanel, "Entity.hlsl", .85f);
            if (_surface.Error is string err) Say("Error de render: " + err);
        };
        Refresh();
    }

    public void Attach(EngineHost host)
    {
        _host = host;
        host.Updated += Refresh;
        Refresh();
    }

    public EntityAnimator Entity => _anim;

    // ------------------------------------------------------------------ frame
    public void Tick(float dt, double nowMs)
    {
        if (_surface is null || !_surface.Ready) return;
        if (_anim.Update(dt, nowMs, Targets()))
            _surface.Render(_anim.Params(_surface.PixelWidth, _surface.PixelHeight));
    }

    private EntityTargets Targets() => _phase switch
    {
        Phase.Running => new(.5f, 0, _step / 6f, 2.4f, 1.8f, .45f, false),
        Phase.Done => new(0, 1, 1, .3f, 1.2f, 1, false),
        Phase.Release => new(0, 0, 0, .45f, 1, .4f, false),
        _ => new(0, 0, 0, .16f, 1, .15f, true),
    };

    /// <summary>Pointer anywhere in the window magnetizes the surface; closer = stronger.</summary>
    public void SetPointer(Vector2 windowPoint, bool inside)
    {
        if (_anim.StickActive) return;
        if (!inside) { _anim.Pointer = Vector3.Zero; return; }
        var t = EntityPanel.TransformToVisual(null);
        var c = t.TransformPoint(new Windows.Foundation.Point(EntityPanel.ActualWidth / 2, EntityPanel.ActualHeight / 2));
        float half = (float)Math.Min(EntityPanel.ActualWidth, EntityPanel.ActualHeight) / 2;
        if (half < 1) return;
        float x = (windowPoint.X - (float)c.X) / half, y = -(windowPoint.Y - (float)c.Y) / half;
        _anim.Pointer = new(x, y, .45f + .55f * MathF.Exp(-MathF.Sqrt(x * x + y * y) / 1.2f));
    }

    public void SetStick(float x, float y)
    {
        bool active = x != 0 || y != 0;
        if (active) _anim.Pointer = new(x * 1.6f, y * 1.6f, 1);
        else if (_anim.StickActive) _anim.Pointer = Vector3.Zero;
        _anim.StickActive = active;
    }

    // ------------------------------------------------------------------ actions
    private void Entity_Click(object sender, RoutedEventArgs e) => ToggleBoost();
    private void Clean_Click(object sender, RoutedEventArgs e) => Clean();

    public void ToggleBoost()
    {
        if (_phase == Phase.Idle) _ = Boost();
        else if (_phase == Phase.Done) _ = Release();
    }

    public void ReleaseIfBoosted() { if (_phase == Phase.Done) _ = Release(); }

    private bool RequireAdmin()
    {
        if (_host?.IsElevated == true) return true;
        Say("Sin permisos de administrador. Cierra y abre TITAN aceptando el aviso de Windows para operar.");
        return false;
    }

    private async Task Boost()
    {
        if (_host is null || !RequireAdmin()) return;
        _phase = Phase.Running; _step = 0; _override = null;
        _anim.State = EntityState.Analysis;
        Refresh();
        var progress = new Progress<BoostProgress>(p => { _step = p.Step; Refresh(); });
        BoostReport report;
        try
        {
            report = await _host.BoostAsync(progress);
        }
        catch (Exception ex)
        {
            _phase = Phase.Idle; _anim.State = EntityState.Rest;
            Say("Operación abortada: " + ex.Message);
            return;
        }
        _phase = Phase.Done;
        _anim.State = EntityState.MaxBoost;
        _anim.Kick(7); _anim.Shock();
        ScanRequested?.Invoke();
        double freed = report.Purge is null ? 0 : Math.Max(0, report.Purge.FreedBytes) / (1024.0 * 1024 * 1024);
        _override = $"{freed:0.00} GB recuperados. {Count(report.Throttled)} procesos moderados, {Count(report.Frozen)} congelados. " +
                    $"Ninguno terminado. {report.ServicesPaused.Count} servicios en pausa. Tócame para revertir.";
        if (report.Warnings.Count > 0) _override += $" ({report.Warnings.Count} avisos)";
        _host.Refresh();
        Refresh();
    }

    private async Task Release()
    {
        if (_host is null) return;
        _phase = Phase.Release; _anim.State = EntityState.Release;
        _anim.Shock(); ScanRequested?.Invoke();
        _override = null;
        Refresh();
        var r = await _host.RestoreAsync();
        await Task.Delay(900);
        _phase = Phase.Idle; _anim.State = EntityState.Rest;
        _override = r.Failures.Count == 0
            ? $"Estado previo restaurado: {r.Reverted} cambios revertidos en {r.Elapsed.TotalMilliseconds:0} ms."
            : $"Restaurado con {r.Failures.Count} incidencias: {r.Failures[0]}";
        _host.Refresh();
        Refresh();
    }

    public async void Clean()
    {
        if (_host is null || _phase is Phase.Running or Phase.Release || !RequireAdmin()) return;
        _anim.Gulp(8, Environment.TickCount64);
        var r = await _host.PurgeAsync(PurgeOps.Boost);
        _override = r.Steps.All(s => s.Ok)
            ? $"Caché purgada: +{Math.Max(0, r.FreedBytes) / (1024.0 * 1024 * 1024):0.00} GB de memoria libre real. Sin incidencias."
            : $"Purga incompleta: {r.Steps.First(s => !s.Ok).Error}";
        _host.Refresh();
        Refresh();
    }

    /// <summary>LT + RT: a visual discharge only (no system action).</summary>
    public void Discharge()
    {
        _anim.Gulp(11, Environment.TickCount64);
        _anim.Shock();
        ScanRequested?.Invoke();
    }

    public void FocusEntity() => EntityButton.Focus(FocusState.Programmatic);

    public void Say(string text) { _override = text; Refresh(); }

    public void AnnounceRecovery(RestoreReport r) =>
        Say($"Sesión anterior interrumpida. Restauré {r.Reverted} cambios antes de empezar.");

    // ------------------------------------------------------------------ text
    private void Refresh()
    {
        var mem = _host?.Memory;
        string standby = mem is null ? "—" : $"{Units.Gb(mem.StandbyBytes):0.00}".Replace('.', ',');
        int watched = _host?.Watched ?? 0;

        (string kicker, string head, string idleVoice, string solid) = _phase switch
        {
            Phase.Running => ($"ESTADO 02 · ANÁLISIS · {_step}/6", "ANÁLISIS",
                              $"Procesando: {StepNames[Math.Clamp(_step - 1, 0, 5)]}.", "OCTAEDRO · AIRE"),
            Phase.Done => ("ESTADO 03 · MAX BOOST", "CONTENCIÓN\nACTIVA", "", "TETRAEDRO · FUEGO"),
            Phase.Release => ("ESTADO 04 · LIBERACIÓN", "LIBERACIÓN", "Restaurando estado previo. Procesos reanudados. Plan de energía original.", "ICOSAEDRO · AGUA"),
            _ => ("ESTADO 01 · REPOSO", "EN\nREPOSO",
                  $"Caché retenida: {standby} GB. Procesos en observación: {watched}. En espera de orden.", "DODECAEDRO · ÉTER"),
        };
        Kicker.Text = kicker;
        Headline.Text = head;
        SolidLine.Text = "SÓLIDO · " + solid;
        PlanLine.Text = "PLAN ··· " + (_host?.PowerPlan is { Length: > 0 } p ? p.ToUpperInvariant() : "—");
        SessionLine.Text = "SESIÓN · " + (_phase == Phase.Done ? "ABIERTA · REVERSIBLE" : "NINGUNA");
        Hint.Text = _phase switch
        {
            Phase.Running => "ABSORBIENDO",
            Phase.Done => "TOCAR PARA LIBERAR",
            Phase.Release => "LIBERANDO",
            _ => "TOCAR PARA BOOST",
        };
        AutomationPropertiesHelper.SetName(EntityButton, _phase == Phase.Done ? "Restaurar el sistema" : "Activar BOOST");

        for (int i = 0; i < 6; i++)
            _segments[i].Fill = (Brush)Application.Current.Resources[
                _phase == Phase.Done ? "Gold" : _phase == Phase.Running && i < _step ? "Ink" : "Line"];

        if (mem is not null)
        {
            FreeValue.Text = Units.GbText(mem.FreeBytes);
            FreeNote.Text = mem.FreeBytes < 1UL << 30 ? "Memoria libre crítica" : "Disponible sin desalojar a nadie";
            StandbyValue.Text = mem.Detailed ? Units.GbText(mem.StandbyBytes) : "—";
            StandbyNote.Text = mem.StandbyBytes < 1UL << 30 ? "Caché devuelta a memoria libre" : "Archivos inactivos retenidos";
        }
        BgValue.Text = watched.ToString();
        BgNote.Text = _phase == Phase.Done ? "EcoQoS aplicado · 0 terminados"
                    : _host?.WatchedSample is { Length: > 0 } s ? $"{s} en observación" : "Nada que moderar";
        CleanButton.IsEnabled = _phase is Phase.Idle or Phase.Done;

        string voice = _phase == Phase.Idle || _phase == Phase.Done ? (_override ?? idleVoice) : idleVoice;
        if (voice != _voice)
        {
            _voice = voice; _typed = 0;
            _typer.Start();
        }
    }

    private static int Count(IEnumerable<string> collapsed) =>
        collapsed.Sum(n => Regex.Match(n, @"×(\d+)$") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 1);
}

internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject o, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(o, name);
}
