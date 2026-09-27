using System.Diagnostics;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Titan.App.Input;
using Titan.App.Services;
using Titan.App.Views;
using Windows.Graphics;

namespace Titan.App;

public sealed partial class MainWindow : Window
{
    private const string IntroScript =
        "ARCHIVO 01 · ACCESO RESTRINGIDO\nSUJETO: TITAN\nCLASE: ANOMALÍA CONTENIDA\n{0}\nESTADO: EN REPOSO\n\nABRIENDO CONTENCIÓN…";

    private readonly EngineHost _host;
    private readonly GamepadInput _pad = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(Button Button, Rectangle Dot, UIElement View)> _tabs = [];
    private readonly DispatcherTimer _introTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private int _active;
    private double _lastFrame;
    private bool _visible = true;
    private bool _introOn = true;
    private string _introFull = "";
    private int _introTyped;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindow();

        _host = new EngineHost(DispatcherQueue);
        Home.Attach(_host);
        Memory.Attach(_host);
        Home.ScanRequested += Sweep;
        if (_host.RecoverIfNeeded() is { } recovered) Home.AnnounceRecovery(recovered);

        AddTab("INICIO", Home);
        AddTab("JUEGOS", Games);
        AddTab("MEMORIA", Memory);
        AddTab("INFORMES", Reports);
        Select(0);

        BuildPadLegend();
        _pad.Pressed += OnPadButton;
        _pad.Navigate += OnPadNavigate;
        _pad.Discharge += () => { if (_active == 0) Home.Discharge(); };
        _pad.ConnectionChanged += c => DispatcherQueue.TryEnqueue(() => PadBar.Visibility = c ? Visibility.Visible : Visibility.Collapsed);
        PadBar.Visibility = _pad.Connected ? Visibility.Visible : Visibility.Collapsed;

        _host.Updated += UpdateStatus;
        UpdateStatus();

        VisibilityChanged += (_, e) => _visible = e.Visible;
        CompositionTarget.Rendering += OnFrame;
        Closed += OnClosed;
        StartIntro();
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        var tb = AppWindow.TitleBar;
        tb.PreferredHeightOption = TitleBarHeightOption.Tall;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 14, 14, 15);
        tb.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 234, 234, 230);
        double scale = GetDpiScale();
        AppWindow.Resize(new SizeInt32((int)(1280 * scale), (int)(800 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsMaximizable = true; }
    }

    private double GetDpiScale()
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return GetDpiForWindow(hwnd) / 96.0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    // ------------------------------------------------------------------ tabs
    private void AddTab(string label, UIElement view)
    {
        int index = _tabs.Count;
        var dot = new Rectangle { Width = 5, Height = 5, Fill = (Brush)Application.Current.Resources["Gold"], VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(dot);
        content.Children.Add(new TextBlock { Text = label });
        var button = new Button { Content = content, Style = (Style)Application.Current.Resources["Tab"] };
        button.Click += (_, _) => Select(index);
        Tabs.Children.Add(button);
        _tabs.Add((button, dot, view));
    }

    private void Select(int index)
    {
        _active = (index + _tabs.Count) % _tabs.Count;
        for (int i = 0; i < _tabs.Count; i++)
        {
            bool on = i == _active;
            _tabs[i].Dot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            _tabs[i].Button.Foreground = (Brush)Application.Current.Resources[on ? "Ink" : "Ink2"];
            _tabs[i].View.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateStatus()
    {
        var t = _clock.Elapsed;
        Status.Text = $"{(_host.IsElevated ? "MOTOR ACTIVO" : "SOLO LECTURA")} · T+{t:hh\\:mm\\:ss} · {_host.Cores}C/{_host.Threads}T";
        ModeDot.Fill = (Brush)Application.Current.Resources[_host.IsElevated ? "Ink" : "Gold"];
    }

    // ------------------------------------------------------------------ frame loop (0 fps while hidden)
    private void OnFrame(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        float dt = (float)Math.Min((now - _lastFrame) / 1000, .05);
        _lastFrame = now;
        if (!_visible) return;

        _pad.Poll(now, dt);
        if (_pad.Connected)
        {
            Home.Entity.TriggerLeft = _pad.LeftTrigger;
            Home.Entity.TriggerRight = _pad.RightTrigger;
            Home.SetStick(_pad.RightStick.X, _pad.RightStick.Y);
            LtMeter.Width = 46 * _pad.LeftTrigger;
            RtMeter.Width = 46 * _pad.RightTrigger;
        }

        if (_active == 0) Home.Tick(dt, now);
        else if (_active == 2) Memory.Tick(dt, now);
    }

    // ------------------------------------------------------------------ pointer → magnetic field
    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(null).Position;
        Home.SetPointer(new Vector2((float)p.X, (float)p.Y), true);
    }

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e) => Home.SetPointer(default, false);

    // ------------------------------------------------------------------ Xbox controller
    private void OnPadButton(PadButton b)
    {
        if (_introOn) { if (b is PadButton.A or PadButton.B) EndIntro(); return; }
        switch (b)
        {
            case PadButton.A:
                if (FocusManager.GetFocusedElement(Root.XamlRoot) is ButtonBase focused) Invoke(focused);
                else if (_active == 0) Home.ToggleBoost();
                break;
            case PadButton.B:
                if (_active == 0) Home.ReleaseIfBoosted(); else Select(0);
                break;
            case PadButton.X:
                if (_active == 0) Home.Clean(); else if (_active == 2) Memory.Purge(Titan.Engine.Memory.PurgeOps.Standby);
                break;
            case PadButton.Y:
                if (_active == 0) Home.ToggleBoost();
                break;
            case PadButton.LB: Select(_active - 1); break;
            case PadButton.RB: Select(_active + 1); break;
        }
    }

    private void OnPadNavigate(PadDirection d)
    {
        if (_introOn) return;
        var dir = d switch
        {
            PadDirection.Up => FocusNavigationDirection.Up,
            PadDirection.Down => FocusNavigationDirection.Down,
            PadDirection.Left => FocusNavigationDirection.Left,
            _ => FocusNavigationDirection.Right,
        };
        var options = new FindNextElementOptions { SearchRoot = Root.XamlRoot.Content };
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is null || !FocusManager.TryMoveFocus(dir, options))
            FocusManager.TryMoveFocus(FocusNavigationDirection.Next, options);
    }

    private static void Invoke(ButtonBase button)
    {
        if (FrameworkElementAutomationPeer.CreatePeerForElement(button) is IInvokeProvider invoker) invoker.Invoke();
        else if (button is ToggleButton toggle) toggle.IsChecked = !toggle.IsChecked;
    }

    private void BuildPadLegend()
    {
        (string Key, string Text)[] items =
        [
            ("A", "ELEGIR"), ("B", "ATRÁS"), ("X", "LIMPIAR"), ("Y", "BOOST"),
            ("LB RB", "SECCIONES"), ("RS", "MAGNETISMO"), ("LT+RT", "DESCARGA"),
        ];
        var mono = (FontFamily)Application.Current.Resources["Mono"];
        var ivory = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 237, 237, 233));
        var mode = new TextBlock { Text = "MODO XBOX", FontFamily = mono, FontSize = 10, CharacterSpacing = 120, Foreground = (Brush)Application.Current.Resources["Gold"], VerticalAlignment = VerticalAlignment.Center };
        PadLegend.Children.Add(mode);
        foreach (var (key, text) in items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            var glyph = new Border
            {
                MinWidth = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1),
                BorderBrush = ivory, Padding = new Thickness(4, 0, 4, 0),
                Child = new TextBlock { Text = key, FontFamily = mono, FontSize = 9, Foreground = ivory, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
            };
            row.Children.Add(glyph);
            row.Children.Add(new TextBlock { Text = text, FontFamily = mono, FontSize = 10, CharacterSpacing = 120, Foreground = ivory, VerticalAlignment = VerticalAlignment.Center });
            PadLegend.Children.Add(row);
        }
    }

    // ------------------------------------------------------------------ cinema
    private void StartIntro()
    {
        string mem = _host.Memory is { } m ? $"MEMORIA RETENIDA: {Units.GbText(m.StandbyBytes)}" : "MEMORIA RETENIDA: CALCULANDO";
        _introFull = string.Format(IntroScript, mem);
        _introTimer.Tick += (_, _) =>
        {
            _introTyped = Math.Min(_introFull.Length, _introTyped + 3);
            IntroText.Text = _introFull[.._introTyped] + "▌";
            if (_introTyped >= _introFull.Length)
            {
                _introTimer.Stop();
                DispatcherQueue.CreateTimer().Let(t => { t.Interval = TimeSpan.FromMilliseconds(650); t.IsRepeating = false; t.Tick += (_, _) => EndIntro(); t.Start(); });
            }
        };
        _introTimer.Start();
    }

    private void Intro_Click(object sender, RoutedEventArgs e) => EndIntro();

    private void EndIntro()
    {
        if (!_introOn) return;
        _introOn = false;
        _introTimer.Stop();
        var fade = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(700), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, Intro);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Completed += (_, _) => { Intro.Visibility = Visibility.Collapsed; Home.FocusEntity(); };
        sb.Begin();
    }

    private void Sweep()
    {
        var move = new DoubleAnimation { From = -140, To = Root.ActualHeight, Duration = TimeSpan.FromMilliseconds(1050), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(move, ScanShift);
        Storyboard.SetTargetProperty(move, "Y");
        var show = new DoubleAnimationUsingKeyFrames();
        show.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 1 });
        show.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1050), Value = 0 });
        Storyboard.SetTarget(show, Scan);
        Storyboard.SetTargetProperty(show, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(move);
        sb.Children.Add(show);
        sb.Begin();
    }

    // ------------------------------------------------------------------ shutdown: never leave the PC boosted
    private void OnClosed(object sender, WindowEventArgs args)
    {
        CompositionTarget.Rendering -= OnFrame;
        if (_host.IsElevated && _host.Boosted) _host.RestoreNow();
        _host.Dispose();
    }
}

internal static class FluentExtensions
{
    public static T Let<T>(this T value, Action<T> action) { action(value); return value; }
}
