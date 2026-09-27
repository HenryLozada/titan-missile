using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Titan.App.Rendering;
using Titan.App.Services;
using Titan.Engine.SystemTweaks;

namespace Titan.App.Views;

public sealed partial class TweaksView : UserControl
{
    private readonly EntityAnimator _cube = new() { State = EntityState.Persist };
    private ShaderSurface<EntityParams>? _surface;
    private EngineHost? _host;
    private bool _busy;

    public SoundEngine? Sound { get; set; }
    public event Action? Changed;

    public TweaksView()
    {
        InitializeComponent();
        Loaded += (_, _) => _surface ??= new ShaderSurface<EntityParams>(CubePanel, "Entity.hlsl", .9f);
    }

    public void Attach(EngineHost host)
    {
        _host = host;
        _ = Reload();
    }

    private static Style St(string k) => (Style)Application.Current.Resources[k];
    private static Brush Res(string k) => (Brush)Application.Current.Resources[k];

    public void Tick(float dt, double nowMs)
    {
        if (_surface is null || !_surface.Ready) return;
        if (_cube.Update(dt, nowMs, new EntityTargets(0, 0, 0, .06f, .4f, .15f, true)))
            _surface.Render(_cube.Params(_surface.PixelWidth, _surface.PixelHeight));
    }

    private async Task Reload()
    {
        if (_host is null) return;
        Voice.Text = "› Leyendo el estado actual del sistema…";
        var status = await Task.Run(_host.Tweaks.Status);
        Rows.Children.Clear();
        foreach (var s in status) Rows.Children.Add(BuildRow(s));
        Count.Text = $"{_host.Tweaks.AppliedCount} AJUSTES FIJOS POR TITAN";
        Voice.Text = _host.IsElevated ? "› Valores originales archivados en %ProgramData%\\TITAN." : "› Solo lectura: sin permisos de administrador no puedo cambiar ajustes.";
        Changed?.Invoke();
    }

    private UIElement BuildRow(TweakStatus s)
    {
        var d = s.Definition;
        var grid = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 14, 0, 14), BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 0, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 4 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(new TextBlock { Text = d.Title, Style = St("Body"), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        head.Children.Add(new TextBlock { Text = d.Impact.ToUpperInvariant(), Style = St("Label"), FontSize = 9, VerticalAlignment = VerticalAlignment.Center });
        text.Children.Add(head);
        text.Children.Add(new TextBlock { Text = d.Description + (d.RequiresRestart ? " Requiere reiniciar." : ""), Style = St("Label"), FontSize = 11, CharacterSpacing = 0, TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(text);

        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        state.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 7, Height = 7, Fill = s.Active ? Res("Gold") : Res("Line"), VerticalAlignment = VerticalAlignment.Center });
        state.Children.Add(new TextBlock { Text = s.Detail.ToUpperInvariant(), Style = St("Label"), FontSize = 10, CharacterSpacing = 80, TextWrapping = TextWrapping.Wrap, MaxWidth = 130 });
        Grid.SetColumn(state, 1);
        grid.Children.Add(state);

        if (d.Kind != TweakKind.Diagnostic)
        {
            bool applied = s.Applied;
            var b = new Button { Content = applied ? "REVERTIR" : s.Active ? "YA ACTIVO" : "APLICAR", Style = St("Pill"), MinWidth = 110, VerticalAlignment = VerticalAlignment.Center };
            b.IsEnabled = _host?.IsElevated == true && (applied || !s.Active);
            b.Click += async (_, _) => await Toggle(d, applied);
            Grid.SetColumn(b, 2);
            grid.Children.Add(b);
        }
        return grid;
    }

    private async Task Toggle(TweakDefinition d, bool applied)
    {
        if (_host is null || _busy) return;
        _busy = true;
        try
        {
            if (applied)
            {
                await Task.Run(() => _host.Tweaks.Revert(d.Id));
                Sound?.Play(Sfx.Release);
            }
            else
            {
                if (_host.Tweaks.AppliedCount == 0)
                {
                    Voice.Text = "› Primer cambio persistente: creando punto de restauración…";
                    bool ok = await Task.Run(TweakCatalog.TryCreateRestorePoint);
                    if (!ok) Voice.Text = "› Windows no creó el punto de restauración (protección desactivada o ya hay uno de hoy). Sigo: el original queda archivado igualmente.";
                }
                await Task.Run(() => _host.Tweaks.Apply(d.Id));
                Sound?.Play(Sfx.Persist);
                _cube.Gulp(6, Environment.TickCount64);
            }
        }
        catch (Exception ex)
        {
            Voice.Text = "› No se pudo: " + ex.Message;
        }
        _busy = false;
        await Reload();
    }

    private async void RestorePoint_Click(object sender, RoutedEventArgs e)
    {
        if (_host?.IsElevated != true) return;
        RestorePointButton.IsEnabled = false;
        Voice.Text = "› Creando punto de restauración…";
        bool ok = await Task.Run(TweakCatalog.TryCreateRestorePoint);
        Voice.Text = ok ? "› Punto de restauración creado." : "› Windows no lo creó: activa la protección del sistema o espera 24 h desde el último.";
        RestorePointButton.IsEnabled = true;
    }
}
