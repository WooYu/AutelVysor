using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static object? CaptureCall(AutelVysor.CaptureHistoryWindow window, string method, params object[] args)
        => typeof(AutelVysor.CaptureHistoryWindow).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);

    private static void SelectCapture(AutelVysor.CaptureHistoryWindow window, params string[] paths)
    {
        var itemType = typeof(AutelVysor.CaptureHistoryWindow).GetNestedType("CaptureItem", BindingFlags.NonPublic)!;
        var items = paths.Select(path => Activator.CreateInstance(itemType, path, DateTime.Now, new FileInfo(path).Length)).ToArray();
        var list = (ListBox)window.FindName("CaptureList");
        list.ItemsSource = items;
        list.SelectedIndex = 0;
    }

    private static async Task CapturePreviewLifecycle()
    {
        var path = State("autel-fixture-long-readable-screenshot-name.png");
        var pixels = new byte[120 * 200 * 4];
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 120; x++)
        {
            var offset = (y * 120 + x) * 4;
            pixels[offset] = (byte)(80 + x); pixels[offset + 1] = (byte)(40 + y); pixels[offset + 2] = 50;
        }
        var bitmap = BitmapSource.Create(120, 200, 96, 96, PixelFormats.Bgr32, null, pixels, 120 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var window = new AutelVysor.CaptureHistoryWindow { ShowInTaskbar = false };
        try
        {
            window.Show(); await Task.Delay(60);
            SelectCapture(window, path);
            window.UpdateLayout();
            var image = (Image)window.FindName("PreviewImage");
            Check(image.Source is not null, "Image capture did not preview");
            Check(!((Button)window.FindName("PlayButton")).IsEnabled, "Image enabled video playback");
            var list = (ListBox)window.FindName("CaptureList");
            var surface = (Border)((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).Template.FindName("Surface", (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0));
            Check(((SolidColorBrush)surface.Background).Color == (Color)ColorConverter.ConvertFromString("#173454"), "Capture selection became unreadable");
            var imageOrigin = image.TransformToAncestor(window).Transform(new Point());
            Check(imageOrigin.X > list.TransformToAncestor(window).Transform(new Point()).X + list.ActualWidth, "Preview is not to the right of captures");
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            list.SelectedIndex = -1;
            Check(image.Source is null, "Deselect retained image");
            Check(!((Button)window.FindName("OpenButton")).IsEnabled, "No selection still allows open");
            SelectCapture(window, path);
            window.UpdateLayout();
            var output = Path.GetFullPath("test-results"); Directory.CreateDirectory(output);
            var render = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); render.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(render));
            using var screenshot = File.Create(Path.Combine(output, "capture-preview.png")); png.Save(screenshot);
        }
        finally { window.Close(); }
    }

    private static Task CaptureVideoLifecycle()
    {
        // Deliberately invalid media: lifecycle and fallback must work without any installed codec.
        var first = State("autel-fixture-first.mp4"); var second = State("autel-fixture-second.mkv");
        File.WriteAllText(first, "not encoded video"); File.WriteAllText(second, "not encoded video");
        var window = new AutelVysor.CaptureHistoryWindow();
        var host = (ContentControl)window.FindName("VideoHost");
        try
        {
            SelectCapture(window, first, second);
            var old = host.Content as MediaElement;
            Check(old is not null && old.Source!.LocalPath == first, "Video selection did not attach embedded media");
            Check(!((Button)window.FindName("PlayButton")).IsEnabled && !((Slider)window.FindName("SeekSlider")).IsEnabled, "Unopened video permits playback/seek");
            ((ListBox)window.FindName("CaptureList")).SelectedIndex = 1;
            Check(old!.Source is null && !ReferenceEquals(host.Content, old), "Switching video retained the old source");
            var current = (MediaElement)host.Content!;
            CaptureCall(window, "VideoOpened", old);
            Check(!((Button)window.FindName("PlayButton")).IsEnabled, "Stale opened event enabled playback for an unopened selection");
            // Exercise UI state transitions directly, without asserting codec decode success.
            CaptureCall(window, "VideoOpened", current);
            Check(((Button)window.FindName("PlayButton")).IsEnabled && ((Button)window.FindName("StopButton")).IsEnabled && !((Button)window.FindName("PauseButton")).IsEnabled,
                "Opened recording does not expose explicit playback controls");
            CaptureCall(window, "Play_Click", window, new RoutedEventArgs());
            Check(((Button)window.FindName("PauseButton")).IsEnabled && !((Button)window.FindName("PlayButton")).IsEnabled, "Play did not update controls");
            CaptureCall(window, "Pause_Click", window, new RoutedEventArgs());
            Check(((Button)window.FindName("PlayButton")).IsEnabled && !((Button)window.FindName("PauseButton")).IsEnabled, "Pause did not update controls");
            CaptureCall(window, "Stop_Click", window, new RoutedEventArgs());
            Check(current.Position == TimeSpan.Zero, "Stop did not rewind");
            CaptureCall(window, "VideoFailed", old, new InvalidOperationException("stale failure"));
            Check(ReferenceEquals(host.Content, current), "Old video failure cleared the new selection");
            CaptureCall(window, "VideoFailed", current, new InvalidOperationException(new string('x', 10000)));
            Check(current.Source is null && host.Content is null, "Media failure retained file ownership");
            Check(((TextBlock)window.FindName("PreviewHint")).Text.Contains("Open selected") && ((TextBlock)window.FindName("PreviewHint")).Text.Length < 800, "Media failure lacks bounded actionable fallback");
            Check(((Button)window.FindName("OpenButton")).IsEnabled && !((Button)window.FindName("PlayButton")).IsEnabled, "Failure disabled external fallback or enabled playback");
            SelectCapture(window, second);
            var timeout = (MediaElement)host.Content!;
            typeof(AutelVysor.CaptureHistoryWindow).GetField("_loadingStarted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, DateTime.UtcNow.AddSeconds(-20));
            CaptureCall(window, "PlaybackTick", window, EventArgs.Empty);
            Check(timeout.Source is null && ((TextBlock)window.FindName("PreviewHint")).Text.Contains("15 seconds"), "Stalled decoder was not bounded and released");
            SelectCapture(window, first);
            var closing = (MediaElement)host.Content!;
            window.Close();
            Check(closing.Source is null && host.Content is null, "Closing captures retained video source");
            using (File.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }
}
