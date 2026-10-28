using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AutelVysor;

/// <summary>Bounded presentation for diagnostic evidence, separate from device playback verification.</summary>
public sealed class TextReportWindow : Window
{
    public TextReportWindow(string title, string report)
    {
        AutelVysor.Services.DarkWindowChrome.Attach(this);
        Title = title; Width = 900; Height = 650; MinWidth = 500; MinHeight = 350;
        MaxWidth = SystemParameters.WorkArea.Width; MaxHeight = SystemParameters.WorkArea.Height;
        Background = (Brush)new BrushConverter().ConvertFromString("#202322")!;
        Foreground = Brushes.White; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/AutelVysor;component/Assets/AutelVysor.ico"));
        var root = new DockPanel { Margin = new Thickness(14) };
        var actions = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var copy = new Button { Content = "Copy report", Padding = new Thickness(10, 7, 10, 7) };
        copy.Click += (_, _) => { try { Clipboard.SetText(report); copy.Content = "Copied"; } catch (Exception ex) { copy.Content = "Copy failed"; copy.ToolTip = ex.Message; } };
        actions.Children.Add(copy);
        var explanation = new TextBlock { Text = "Configuration / Android indicators only · actual playback not verified", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(explanation, 1); actions.Children.Add(explanation);
        DockPanel.SetDock(actions, Dock.Top); root.Children.Add(actions);
        var text = new TextBox { Name = "ReportText", Text = report, IsReadOnly = true, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 13, Background = Background, Foreground = Foreground,
            Padding = new Thickness(8) };
        root.Children.Add(text); Content = root;
    }
}
