using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AutelVysor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);

        // The executable uses an explicit Program.Main() entry point. Ensure the
        // shared WPF theme is loaded before any windows parse StaticResource styles.
        // Program.Main intentionally bypasses Application.InitializeComponent().
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/AutelVysor;component/Theme.xaml")
        });

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static string LogPath
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Autel",
                "AutelVysor");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "AutelVysor.log");
        }
    }

    private static void WriteException(string source, Exception exception)
    {
        try
        {
            File.AppendAllText(
                LogPath,
                $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never hide the original failure.
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        => WriteException("DispatcherUnhandledException", e.Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteException("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }
}
