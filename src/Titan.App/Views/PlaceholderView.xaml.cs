using Microsoft.UI.Xaml.Controls;

namespace Titan.App.Views;

public sealed partial class PlaceholderView : UserControl
{
    public PlaceholderView() => InitializeComponent();

    public string Kicker { get => KickerText.Text; set => KickerText.Text = value; }
    public string Title { get => TitleText.Text; set => TitleText.Text = value; }
    public string Body { get => BodyText.Text; set => BodyText.Text = value; }
}
