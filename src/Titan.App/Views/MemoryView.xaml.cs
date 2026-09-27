using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Titan.App.Rendering;
using Titan.App.Services;
using Titan.Engine.Memory;

namespace Titan.App.Views;

public sealed partial class MemoryView : UserControl
{
    private const int Rungs = 24;

    private static readonly (string Title, string Desc, string Tag, PurgeOps Op)[] Operations =
    [
        ("Standby de prioridad 0", "Caché marcada como desechable. Coste mínimo.", "RECOMENDADO", PurgeOps.StandbyLowPriority),
        ("Lista Standby completa", "Caché de archivos devuelta a memoria libre. Usado por BOOST.", "RECOMENDADO", PurgeOps.Standby),
        ("Lista Modificada", "Páginas pendientes escritas a disco.", "", PurgeOps.ModifiedList),
        ("Working set del sistema", "Caché de archivos del kernel recortada.", "", PurgeOps.SystemWorkingSet),
        ("Working sets de procesos", "Páginas retiradas a todas las apps. Riesgo de tirones.", "SOLO MANUAL", PurgeOps.WorkingSets),
    ];

    private ShaderSurface<HelixParams>? _surface;
    private EngineHost? _host;
    private float _t, _pulse, _stb, _use, _stbTarget;
    private double _lastDraw;

    public MemoryView()
    {
        InitializeComponent();
        for (int i = 0; i < Operations.Length; i++) Ops.Children.Add(BuildRow(i));
        Loaded += (_, _) => _surface ??= new ShaderSurface<HelixParams>(HelixPanel, "Helix.hlsl", 1f);
    }

    public void Attach(EngineHost host)
    {
        _host = host;
        host.Updated += Refresh;
        host.GuardPurged += r => Say($"Guardián: purga automática. +{Units.Gb((ulong)Math.Max(0, r.FreedBytes)):0.00} GB libres.");
        host.GuardToggled += on => { _syncing = true; GuardToggle.IsChecked = on; _syncing = false; };
        host.GovernorChanged += g => { if (g.LastAction is { } a) Say("Gobernador: " + a + "."); };
        Say(host.IsElevated ? "Lectura en vivo. Umbral vigilado." : "Solo lectura: sin permisos de administrador no puedo purgar.");
        Refresh();
    }

    private UIElement BuildRow(int i)
    {
        var (title, desc, tag, op) = Operations[i];
        var grid = new Grid { ColumnSpacing = 14, Padding = new Thickness(0, 14, 0, 14), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["Line"], BorderThickness = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = (Style)Application.Current.Resources["Label"];
        grid.Children.Add(new TextBlock { Text = $"0{i + 1}", Style = label, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Spacing = 4 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["Body"], FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        if (tag.Length > 0) head.Children.Add(new TextBlock { Text = tag, Style = label, FontSize = 9, VerticalAlignment = VerticalAlignment.Center });
        text.Children.Add(head);
        text.Children.Add(new TextBlock { Text = desc, Style = label, FontSize = 11, CharacterSpacing = 0, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var button = new Button { Content = "VACIAR", Style = (Style)Application.Current.Resources["Pill"], VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => Purge(op);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        return grid;
    }

    public async void Purge(PurgeOps op)
    {
        if (_host is null) return;
        if (!_host.IsElevated) { Say("Solo lectura: sin permisos de administrador no puedo purgar."); return; }
        _pulse = 1;
        var r = await _host.PurgeAsync(op);
        Say(r.Steps.All(s => s.Ok)
            ? $"{Describe(op)}: +{Units.Gb((ulong)Math.Max(0, r.FreedBytes)):0.00} GB de memoria libre real."
            : $"Incidencia: {r.Steps.First(s => !s.Ok).Error}");
        _host.Refresh();
    }

    private static string Describe(PurgeOps op) => op switch
    {
        PurgeOps.StandbyLowPriority => "Standby prioridad 0 purgada",
        PurgeOps.Standby => "Lista Standby purgada",
        PurgeOps.ModifiedList => "Lista Modificada escrita a disco",
        PurgeOps.SystemWorkingSet => "Working set del sistema recortado",
        PurgeOps.WorkingSets => "Working sets retirados. Uso no recomendado en juego",
        _ => "Purga completada",
    };

    private bool _syncing;

    private void Guard_Changed(object sender, RoutedEventArgs e)
    {
        if (_host is null || _syncing) return;
        bool on = GuardToggle.IsChecked == true;
        if (on && !_host.IsElevated) { GuardToggle.IsChecked = false; Say("El guardián necesita permisos de administrador."); return; }
        _host.SetGuard(on);
        Say(!on ? "Guardián en pausa."
            : _host.Boosted ? "Guardián armado: vuelve cuando termine el BOOST. Ahora manda el gobernador."
            : $"Guardián activo. Purgo solo si la memoria libre baja de {ThresholdValue.Text}.");
    }

    private void Say(string text) => Voice.Text = "› " + text;

    private void Refresh()
    {
        var m = _host?.Memory;
        if (m is null) return;
        double gbPerRung = Units.Gb(m.TotalBytes) / Rungs;
        _use = (float)Math.Round(Units.Gb(m.InUseBytes + m.ModifiedBytes) / gbPerRung);
        _stbTarget = (float)Math.Min(Rungs - _use, Math.Round(Units.Gb(m.StandbyBytes) / gbPerRung));
        Kicker.Text = $"MEMORIA FÍSICA · {Units.GbText(m.TotalBytes)}";
        FreeValue.Text = Units.GbText(m.FreeBytes);
        StandbyValue.Text = m.Detailed ? Units.GbText(m.StandbyBytes) : "—";
        ModifiedValue.Text = m.Detailed ? Units.GbText(m.ModifiedBytes) : "—";
        InUseValue.Text = Units.GbText(m.InUseBytes);
        RungNote.Text = $"1 PELDAÑO ≈ {gbPerRung:0.0} GB · {Rungs} PELDAÑOS";
        ThresholdValue.Text = $"{Units.Gb(StandbyGuardOptions.ForRam(m.TotalBytes).FreeBelowBytes):0.#} GB";
    }

    public void Tick(float dt, double nowMs)
    {
        if (_surface is null || !_surface.Ready) return;
        _stb += (_stbTarget - _stb) * (1 - MathF.Exp(-dt * 4));
        _pulse *= MathF.Exp(-dt * 2.5f);
        _t += dt * (1 + _pulse * 2);
        bool calm = _pulse < .01f && MathF.Abs(_stbTarget - _stb) < .01f;
        if (calm && nowMs - _lastDraw < 32) return;      // 30 fps when nothing is happening
        _lastDraw = nowMs;
        _surface.Render(new HelixParams
        {
            Res = new(_surface.PixelWidth, _surface.PixelHeight), Time = _t,
            Use = _use, Stb = MathF.Round(_stb), N = Rungs, Pulse = _pulse,
            Bg = EntityAnimator.Page, Gold = EntityAnimator.FerroYellow,
        });
    }
}
