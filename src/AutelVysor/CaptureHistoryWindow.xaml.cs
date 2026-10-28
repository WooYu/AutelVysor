using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AutelVysor;

public partial class CaptureHistoryWindow : Window
{
    private sealed record CaptureItem(string Path, DateTime Modified, long Bytes)
    {
        public string FileName => System.IO.Path.GetFileName(Path);
        public string Details => $"{Modified:yyyy-MM-dd HH:mm}  ·  {Bytes / 1024d / 1024:0.0} MB";
    }

    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private MediaElement? _video;
    private bool _mediaOpened;
    private bool _playing;
    private bool _updatingSeek;
    private DateTime _loadingStarted;

    public CaptureHistoryWindow()
    {
        InitializeComponent();
        AutelVysor.Services.DarkWindowChrome.Attach(this);
        Loaded += (_, _) => Refresh();
        Closed += (_, _) => { ResetPreview(); _playbackTimer.Tick -= PlaybackTick; };
        _playbackTimer.Tick += PlaybackTick;
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
        ResetPreview();
        var available = SelectedPath is string selected && File.Exists(selected);
        OpenButton.IsEnabled = FolderButton.IsEnabled = DeleteButton.IsEnabled = available;
        if (SelectedPath is not string path) return;
        if (!available) { PreviewHint.Text = "This capture is no longer available. Refresh the list."; return; }
        StatusText.Text = System.IO.Path.GetFileName(path);
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg"))
        {
            VideoControls.Visibility = SeekSlider.Visibility = Visibility.Visible;
            ImageScroll.Visibility = Visibility.Collapsed;
            var video = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Close,
                Stretch = System.Windows.Media.Stretch.Uniform,
                ScrubbingEnabled = true
            };
            _video = video;
            VideoHost.Content = video;
            video.MediaOpened += (_, _) => VideoOpened(video);
            video.MediaFailed += (_, args) => VideoFailed(video, args.ErrorException);
            video.MediaEnded += (_, _) => { if (ReferenceEquals(_video, video)) StopPlayback(); };
            PreviewHint.Text = "Loading recording…";
            _loadingStarted = DateTime.UtcNow;
            _playbackTimer.Start();
            try { video.Source = new Uri(path, UriKind.Absolute); video.Play(); }
            catch (Exception ex) { VideoFailed(video, ex); }
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            PreviewImage.Source = image;
            ImageControls.Visibility = Visibility.Visible;
            ImageDetails.Text = $"{image.PixelWidth} × {image.PixelHeight} px";
            FitImage_Click(this, new RoutedEventArgs());
            PreviewHint.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { PreviewHint.Text = "Image preview failed. Use Open selected. " + BoundedMessage(ex); }
    }

    private void ResetPreview()
    {
        if (VideoControls is not null) VideoControls.Visibility = SeekSlider.Visibility = ImageControls.Visibility = Visibility.Collapsed;
        ImageScroll.Visibility = Visibility.Visible;
        _playbackTimer.Stop();
        var previous = _video;
        _video = null; // Ignore queued media events from the previous selection.
        _mediaOpened = _playing = false;
        if (previous is not null)
        {
            previous.Close();
            previous.Source = null;
        }
        VideoHost.Content = null;
        PreviewImage.Source = null;
        PlayButton.IsEnabled = PauseButton.IsEnabled = StopButton.IsEnabled = SeekSlider.IsEnabled = false;
        _updatingSeek = true;
        SeekSlider.Value = 0;
        SeekSlider.Maximum = 1;
        _updatingSeek = false;
        PlaybackTime.Text = "00:00 / --:--";
        PreviewHint.Text = "Select a screenshot or recording to preview.";
        PreviewHint.Visibility = Visibility.Visible;
    }

    private void FitImage_Click(object sender, RoutedEventArgs e)
    {
        PreviewImage.Width = PreviewImage.Height = double.NaN;
        PreviewImage.Stretch = System.Windows.Media.Stretch.Uniform;
        ImageScroll.HorizontalScrollBarVisibility = ImageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    private void ActualImage_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is not BitmapSource image) return;
        PreviewImage.Width = image.PixelWidth;
        PreviewImage.Height = image.PixelHeight;
        PreviewImage.Stretch = System.Windows.Media.Stretch.Fill;
        ImageScroll.HorizontalScrollBarVisibility = ImageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    private void VideoOpened(MediaElement video)
    {
        if (!ReferenceEquals(_video, video)) return;
        _mediaOpened = true;
        video.Pause(); // Show the first frame; playback is an explicit action.
        video.Position = TimeSpan.Zero;
        PreviewHint.Visibility = Visibility.Collapsed;
        var duration = video.NaturalDuration;
        SeekSlider.IsEnabled = duration.HasTimeSpan && duration.TimeSpan.TotalSeconds > 0;
        SeekSlider.Maximum = SeekSlider.IsEnabled ? duration.TimeSpan.TotalSeconds : 1;
        PlayButton.IsEnabled = StopButton.IsEnabled = true;
        PauseButton.IsEnabled = false;
        StatusText.Text = "Recording ready. Playback depends on Windows media codecs; Open selected also uses your default app.";
        PlaybackTick(this, EventArgs.Empty);
    }

    private void VideoFailed(MediaElement video, Exception error)
    {
        if (!ReferenceEquals(_video, video)) return;
        ResetPreview();
        PreviewHint.Text = "Windows could not preview this recording (unsupported codec, incomplete or damaged file). " +
            "Use Open selected to try your default video player. " + BoundedMessage(error);
        StatusText.Text = "Recording preview unavailable. The original file is unchanged.";
    }

    private static string BoundedMessage(Exception error)
    {
        var message = error.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length > 300 ? message[..300] + "…" : message;
    }

    private void PlaybackTick(object? sender, EventArgs e)
    {
        if (_video is not { } video) return;
        if (!_mediaOpened)
        {
            if (DateTime.UtcNow - _loadingStarted > TimeSpan.FromSeconds(15))
                VideoFailed(video, new TimeoutException("The Windows media decoder did not open the recording within 15 seconds."));
            return;
        }
        _updatingSeek = true;
        SeekSlider.Value = video.Position.TotalSeconds;
        _updatingSeek = false;
        PlaybackTime.Text = $"{FormatTime(video.Position)} / {(video.NaturalDuration.HasTimeSpan ? FormatTime(video.NaturalDuration.TimeSpan) : "--:--")}";
    }

    private static string FormatTime(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaOpened || _video is null) return;
        _video.Play(); _playing = true;
        PlayButton.IsEnabled = false; PauseButton.IsEnabled = true;
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (!_mediaOpened || _video is null) return;
        _video.Pause(); _playing = false;
        PlayButton.IsEnabled = true; PauseButton.IsEnabled = false;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        if (!_mediaOpened || _video is null) return;
        _video.Pause(); _video.Position = TimeSpan.Zero; _playing = false;
        PlayButton.IsEnabled = true; PauseButton.IsEnabled = false;
        PlaybackTick(this, EventArgs.Empty);
    }

    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeek || !_mediaOpened || _video is null || !SeekSlider.IsEnabled) return;
        _video.Position = TimeSpan.FromSeconds(e.NewValue);
        if (!_playing) _video.Pause();
        PlaybackTick(this, EventArgs.Empty);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPath is not string path || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "Open failed: " + BoundedMessage(ex); }
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPath is not string path || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "Explorer failed: " + BoundedMessage(ex); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPath is not string path || !File.Exists(path)) return;
        if (MessageBox.Show(this, $"Delete this local file?\n\n{path}",
            "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ResetPreview(); // Close the decoder before deleting its file.
        try { File.Delete(path); Refresh(); }
        catch (Exception ex) { StatusText.Text = "Delete failed: " + BoundedMessage(ex); }
    }
}
