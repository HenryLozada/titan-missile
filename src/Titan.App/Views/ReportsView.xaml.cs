using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Titan.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Titan.App.Views;

public sealed partial class ReportsView : UserControl
{
    private ReportStore? _store;
    private string? _gameId;

    public ReportsView() => InitializeComponent();

    public void Attach(ReportStore store)
    {
        _store = store;
        Rebuild();
    }

    /// <summary>Line under the title while a session is being measured.</summary>
    public void SetLive(string? text) => Live.Text = text ?? "";

    public void Rebuild()
    {
        if (_store is null) return;
        Sessions.Children.Clear();
        foreach (var r in _store.All)
        {
            var b = new Button { Style = (Style)Application.Current.Resources["Pill"], BorderThickness = new Thickness(0, 0, 0, 1),
                                 BorderBrush = (Brush)Application.Current.Resources["Line"], HorizontalAlignment = HorizontalAlignment.Stretch,
                                 HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 10, 0, 10) };
            var text = new StackPanel { Spacing = 3 };
            text.Children.Add(new TextBlock { Text = r.GameName, FontWeight = Microsoft.UI.Text.FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = $"{r.Start:dd/MM HH:mm} · {(r.Boosted ? "BOOST" : "SIN BOOST")} · {(r.AvgFps > 0 ? $"{r.AvgFps:0} FPS" : "sin cuadros")}",
                FontSize = 10, CharacterSpacing = 60, Opacity = .7,
            });
            b.Content = text;
            b.Click += (_, _) => { _gameId = r.GameId; ShowGame(); };
            Sessions.Children.Add(b);
        }
        _gameId ??= _store.All.FirstOrDefault()?.GameId;
        ShowGame();
    }

    private (SessionReport? On, SessionReport? Off) Pair()
    {
        var mine = _store?.All.Where(r => r.GameId == _gameId).ToList() ?? [];
        return (mine.FirstOrDefault(r => r.Boosted && r.AvgFps > 0), mine.FirstOrDefault(r => !r.Boosted && r.AvgFps > 0));
    }

    private void ShowGame()
    {
        var (on, off) = Pair();
        var any = on ?? off;
        GameTitle.Text = any?.GameName.ToUpperInvariant() ?? "SIN SESIONES TODAVÍA";
        static string F(double v) => v > 0 ? $"{v:0}" : "—";
        AvgOn.Text = F(on?.AvgFps ?? 0); AvgOff.Text = off is null ? "sin medición sin BOOST" : $"vs {off.AvgFps:0} sin BOOST{Delta(on?.AvgFps, off.AvgFps)}";
        LowOn.Text = F(on?.Low1Fps ?? 0); LowOff.Text = off is null ? "—" : $"vs {off.Low1Fps:0} sin BOOST{Delta(on?.Low1Fps, off.Low1Fps)}";
        StutOn.Text = on is null ? "—" : on.Stutters.ToString(); StutOff.Text = off is null ? "—" : $"vs {off.Stutters} sin BOOST";
        DrawChart();
    }

    private static string Delta(double? on, double off) =>
        on is double v && off > 0 ? $"  ({(v - off) / off:+0%;-0%;0%})" : "";

    private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

    private void DrawChart()
    {
        var (on, off) = Pair();
        double w = Chart.ActualWidth, h = Chart.ActualHeight;
        LineOn.Points.Clear(); LineOff.Points.Clear();
        ChartEmpty.Text = on is null && off is null ? "Juega una partida con BOOST para ver su gráfica." : "";
        if (w < 10 || h < 10) return;
        float max = Math.Max(33.4f, Math.Max(on?.FrameTimes.DefaultIfEmpty().Max() ?? 0, off?.FrameTimes.DefaultIfEmpty().Max() ?? 0));
        void Plot(Microsoft.UI.Xaml.Shapes.Polyline line, float[]? times)
        {
            if (times is null || times.Length < 2) return;
            for (int i = 0; i < times.Length; i++)
                line.Points.Add(new Windows.Foundation.Point(8 + (w - 16) * i / (times.Length - 1), h - 8 - (h - 16) * Math.Min(1, times[i] / max)));
        }
        Plot(LineOff, off?.FrameTimes);
        Plot(LineOn, on?.FrameTimes);
    }

    private string Summary()
    {
        var (on, off) = Pair();
        if (on is null) return "TITAN · sin sesiones con BOOST todavía.";
        string s = $"TITAN · {on.GameName}\nBOOST: {on.AvgFps:0} FPS · 1% lows {on.Low1Fps:0} · {on.Stutters} tirones >50 ms";
        if (off is not null) s += $"\nSin BOOST: {off.AvgFps:0} FPS · 1% lows {off.Low1Fps:0} · {off.Stutters} tirones";
        return s + "\nMedido con ETW (Present), misma máquina.";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var p = new DataPackage();
        p.SetText(Summary());
        Clipboard.SetContent(p);
        Sub.Text = "Resumen copiado al portapapeles.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(Card);
            var pixels = (await rtb.GetPixelsAsync()).ToArray();
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TITAN");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"titan-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            using var mem = new InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, mem);
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels);
            await enc.FlushAsync();
            mem.Seek(0);
            var bytes = new byte[mem.Size];
            await mem.ReadAsync(bytes.AsBuffer(), (uint)mem.Size, InputStreamOptions.None);
            await File.WriteAllBytesAsync(path, bytes);
            Sub.Text = "Tarjeta guardada en " + path;
        }
        catch (Exception ex)
        {
            Sub.Text = "No pude guardar la tarjeta: " + ex.Message;
        }
    }
}
