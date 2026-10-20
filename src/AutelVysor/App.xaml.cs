using System.IO;
using System.Windows;
using System.Windows.Threading;
using AutelVysor.Services;

namespace AutelVysor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);

        if (e.Args.Any(x => string.Equals(x, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            // Self-test is a CI/release probe, not an interactive WPF session.  Exit the
            // process explicitly after the probe so native tool processes/dispatcher
            // lifetime cannot keep a headless Windows runner alive.
            var exitCode = SelfTestService.Run();
            Environment.ExitCode = exitCode;
            Environment.Exit(exitCode);
            return;
        }

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
