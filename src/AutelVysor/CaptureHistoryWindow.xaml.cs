using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace AutelVysor;

public partial class CaptureHistoryWindow : Window
{
    private sealed record CaptureItem(string Path, DateTime Modified, long Bytes)
    {
        public string Display => $"{System.IO.Path.GetFileName(Path)}  ·  {Modified:yyyy-MM-dd HH:mm}  ·  {Bytes / 1024d / 1024:0.0} MB";
    }

    public CaptureHistoryWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private string? SelectedPath => (CaptureList.SelectedItem as CaptureItem)?.Path;

    private void Refresh()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        var items = new List<CaptureItem>();
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "autel-*", SearchOption.TopDirectoryOnly))
                {
                    if (!new[] { ".png", ".jpg", ".jpeg", ".mp4", ".mkv" }.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant()))
                        continue;
                    var info = new FileInfo(file);
                    items.Add(new CaptureItem(info.FullName, info.LastWriteTime, info.Length));
                }
            }
            catch { /* The user may not have access to every library folder. */ }
        }

        CaptureList.ItemsSource = items.OrderByDescending(x => x.Modified).Take(300).ToArray();
        StatusText.Text = $"{items.Count} saved screenshots / recordings found. Showing up to 300 recent captures.";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void CaptureSelected(object sender, SelectionChangedEventArgs e)
    {
        PreviewImage.Source = null;
        PreviewHint.Visibility = Visibility.Visible;
        if (SelectedPath is not string path || !File.Exists(path)) return;

        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg"))
        {
            PreviewHint.Text = "Video preview is not embedded. Click Open selected to play with the Windows default app.";
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1000;
            image.EndInit();
            image.Freeze();
            PreviewImage.Source = image;
            PreviewHint.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { StatusText.Text = "Preview failed: " + ex.Message; }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPath is not string path || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPath is not string path || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPath is not string path || !File.Exists(path)) return;
        if (MessageBox.Show(this, $"Delete this local file?\\n\\n{path}",
            "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        File.Delete(path);
        PreviewImage.Source = null;
        Refresh();
    }
}
