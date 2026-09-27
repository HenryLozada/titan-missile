using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Titan.App.Services;
using Titan.Engine.Games;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Titan.App.Views;

public sealed partial class GamesView : UserControl
{
    private const int Columns = 6;
    private GameService? _games;
    private string _progress = "";

    public GamesView() => InitializeComponent();

    public void Attach(GameService games)
    {
        _games = games;
        games.LibraryChanged += Rebuild;
        games.ScanProgress += m => { _progress = m; UpdateSub(); };
        Rebuild();
    }

    private static Brush Res(string k) => (Brush)Application.Current.Resources[k];
    private static Style St(string k) => (Style)Application.Current.Resources[k];

    private void UpdateSub()
    {
        if (_games is null) return;
        Sub.Text = _games.Scanning
            ? $"Escaneando… {_progress}"
            : $"{_games.Games.Count} juegos en {string.Join(", ", GameLibrary.FixedDrives().Select(d => d.TrimEnd('\\')))} · launchers y carpetas locales · con AUTO la entidad actúa sola.";
        ScanButton.IsEnabled = !_games.Scanning;
    }

    private void Rebuild()
    {
        if (_games is null) return;
        UpdateSub();
        Cards.Children.Clear();
        Cards.ColumnDefinitions.Clear();
        Cards.RowDefinitions.Clear();
        for (int c = 0; c < Columns; c++) Cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var list = _games.Games;
        for (int i = 0; i < list.Count; i++)
        {
            if (i % Columns == 0) Cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = BuildCard(list[i]);
            Grid.SetColumn(card, i % Columns);
            Grid.SetRow(card, i / Columns);
            Cards.Children.Add(card);
        }
        if (list.Count == 0 && !_games.Scanning)
        {
            Cards.RowDefinitions.Add(new RowDefinition());
            var empty = new TextBlock { Text = "No encontré juegos. Pulsa ESCANEAR UNIDADES o AÑADIR .EXE.", Style = St("Label"), Margin = new Thickness(0, 40, 0, 0) };
            Grid.SetColumnSpan(empty, Columns);
            Cards.Children.Add(empty);
        }
    }

    private static string SourceLabel(GameEntry g)
    {
        if (g.Source != GameSource.Local) return g.Source switch
        {
            GameSource.BattleNet => "BATTLE.NET", GameSource.GOG => "GOG", GameSource.EA => "EA", GameSource.Manual => "MANUAL",
            _ => g.Source.ToString().ToUpperInvariant(),
        };
        string p = g.InstallDir;
        if (p.Contains("AnkerGames", StringComparison.OrdinalIgnoreCase)) return "ANKERGAMES";
        if (p.Contains("FitGirl", StringComparison.OrdinalIgnoreCase) || p.Contains("DODI", StringComparison.OrdinalIgnoreCase) || p.Contains("Repack", StringComparison.OrdinalIgnoreCase)) return "REPACK";
        return "LOCAL · " + p[..2].ToUpperInvariant();
    }

    private FrameworkElement BuildCard(GameEntry g)
    {
        var root = new StackPanel { Spacing = 10 };

        // Cover: Steam art when there is one, otherwise the game's own icon on a quiet ground.
        var cover = new Grid { Height = 270, Background = Res("Soft") };
        if (g.CoverPath is { } art && File.Exists(art))
            cover.Children.Add(new Image { Source = new BitmapImage(new Uri(art)), Stretch = Stretch.UniformToFill });
        else
        {
            var fallback = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 14 };
            var icon = new Image { Width = 104, Height = 104 };
            fallback.Children.Add(icon);
            fallback.Children.Add(new TextBlock
            {
                Text = g.Name.ToUpperInvariant(), Style = St("Headline"), FontSize = 11, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 150, CharacterSpacing = 0, LineHeight = 15,
            });
            cover.Children.Add(fallback);
            if (g.Exe is { } exe) _ = LoadIcon(icon, exe);
        }
        if (_games?.Running is { } r && r.Game.Id == g.Id)
        {
            var live = new Border { Background = Res("Ink"), Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) };
            live.Child = new TextBlock { Text = "EN EJECUCIÓN", Style = St("Label"), FontSize = 9, Foreground = Res("Gold") };
            cover.Children.Add(live);
        }
        root.Children.Add(cover);

        root.Children.Add(new TextBlock { Text = SourceLabel(g), Style = St("Label"), FontSize = 9 });
        root.Children.Add(new TextBlock { Text = g.Name, Style = St("Body"), FontWeight = Microsoft.UI.Text.FontWeights.Medium, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });

        var actions = new Grid { ColumnSpacing = 6 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var auto = new CheckBox
        {
            Content = "AUTO", IsChecked = g.AutoBoost, FontFamily = (FontFamily)Application.Current.Resources["Mono"], FontSize = 10,
            CharacterSpacing = 140, MinWidth = 0, MinHeight = 24, FocusVisualPrimaryBrush = Res("Gold"),
        };
        ToolTipService.SetToolTip(auto, "BOOST automático al abrir este juego");
        auto.Checked += (_, _) => _games?.SetAuto(g, true);
        auto.Unchecked += (_, _) => _games?.SetAuto(g, false);
        root.Children.Add(auto);

        var play = new Button { Content = "JUGAR", Style = St("Pill"), Padding = new Thickness(10, 0, 10, 0), MinHeight = 30, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch };
        play.Click += (_, _) =>
        {
            try { _games?.Launch(g); }
            catch (Exception ex) { Sub.Text = "No pude abrirlo: " + ex.Message; }
        };
        actions.Children.Add(play);

        var more = new Button { Content = "···", Style = St("Pill"), Padding = new Thickness(8, 0, 8, 0), MinHeight = 30, FontSize = 11 };
        AutomationPropertiesHelper.SetName(more, "Más opciones de " + g.Name);
        var menu = new MenuFlyout();
        var measure = new MenuFlyoutItem { Text = "Medir sin BOOST (comparación)" };
        measure.Click += (_, _) => { _games?.MeasureWithoutBoost(g); Sub.Text = $"La próxima sesión de {g.Name} se medirá sin BOOST."; };
        var folder = new MenuFlyoutItem { Text = "Abrir carpeta" };
        folder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{g.InstallDir}\"") { UseShellExecute = true });
        var hide = new MenuFlyoutItem { Text = "Ocultar (no es un juego)" };
        hide.Click += (_, _) => _games?.Hide(g);
        menu.Items.Add(measure); menu.Items.Add(folder); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(hide);
        more.Flyout = menu;
        Grid.SetColumn(more, 1);
        actions.Children.Add(more);
        root.Children.Add(actions);
        return root;
    }

    private static async Task LoadIcon(Image target, string exe)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(exe);
            // 256 px: crisp on the card. ResizeThumbnail asks Windows for the largest icon the exe carries.
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 256, ThumbnailOptions.ResizeThumbnail);
            if (thumb is null || thumb.Size == 0) return;
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(thumb);
            target.Source = bmp;
        }
        catch { }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_games is null) return;
        await _games.RescanAsync();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        // Pickers from Windows.Storage refuse to run elevated; the classic dialog doesn't.
        if (OpenFile.Pick(App.MainWindow is { } w ? WinRT.Interop.WindowNative.GetWindowHandle(w) : 0) is { } exe)
            _games?.AddManual(exe);
    }
}

/// <summary>GetOpenFileNameW: works from an elevated process, unlike FileOpenPicker.</summary>
internal static class OpenFile
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize; public nint hwndOwner; public nint hInstance; public string lpstrFilter; public nint lpstrCustomFilter;
        public int nMaxCustFilter; public int nFilterIndex; public nint lpstrFile; public int nMaxFile; public nint lpstrFileTitle;
        public int nMaxFileTitle; public string? lpstrInitialDir; public string lpstrTitle; public int Flags; public short nFileOffset;
        public short nFileExtension; public string? lpstrDefExt; public nint lCustData; public nint lpfnHook; public nint lpTemplateName;
        public nint pvReserved; public int dwReserved; public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

    public static string? Pick(nint owner)
    {
        const int max = 1024;
        nint buffer = Marshal.AllocHGlobal(max * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(), hwndOwner = owner, lpstrFilter = "Juegos (*.exe)\0*.exe\0\0",
                lpstrFile = buffer, nMaxFile = max, lpstrTitle = "Elige el ejecutable del juego", Flags = 0x00001000 | 0x00000800 | 0x00080000,
            };
            return GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
