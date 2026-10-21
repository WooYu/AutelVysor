using System.IO;
using System.Windows;
using Microsoft.Win32;
using AutelVysor.Models;
using AutelVysor.Services;

namespace AutelVysor;

public partial class BatchOperationsWindow : Window
{
    private readonly AndroidBridge _bridge;
    private readonly IReadOnlyList<AndroidDevice> _devices;
    private CancellationTokenSource? _operationCts;

    public BatchOperationsWindow(AndroidBridge bridge, IReadOnlyList<AndroidDevice> devices)
    {
        InitializeComponent();
        _bridge = bridge;
        _devices = devices;
        DevicesText.Text = $"{devices.Count} selected online device(s): " +
            string.Join(" · ", devices.Select(x => $"{x.DisplayName} ({x.Serial})"));
        Closed += (_, _) => _operationCts?.Cancel();
    }

    private void Append(string line)
    {
        OutputBox.AppendText(line + Environment.NewLine);
        if (OutputBox.Text.Length > 300_000)
            OutputBox.Text = OutputBox.Text[^150_000..];
        OutputBox.ScrollToEnd();
    }

    private async Task RunBatchAsync(Func<AndroidDevice, CancellationToken, Task<string>> action)
    {
        if (_operationCts is not null)
        {
            StatusText.Text = "A batch is already running.";
            return;
        }

        using var cts = new CancellationTokenSource();
        _operationCts = cts;
        var pass = 0;
        var failed = 0;
        try
        {
            foreach (var device in _devices)
            {
                if (cts.IsCancellationRequested)
                    break;
                Append($">>> {device.DisplayName} | {device.Serial}");
                StatusText.Text = $"Running on {device.DisplayName}…";
                try
                {
                    var result = await action(device, cts.Token);
                    if (!string.IsNullOrEmpty(result))
                        Append(result);
                    pass++;
                    Append("[PASS]");
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    Append("[CANCELLED]");
                    break;
                }
                catch (Exception ex)
                {
                    failed++;
                    Append("[FAILED] " + ex.Message);
                }
                Append(string.Empty);
            }
            StatusText.Text = $"Completed: {pass} succeeded, {failed} failed" +
                (cts.IsCancellationRequested ? ", cancelled." : ".");
        }
        finally
        {
            _operationCts = null;
        }
    }

    private async void RunCommand_Click(object sender, RoutedEventArgs e)
    {
        var command = CommandBox.Text.Trim();
        if (command.Length == 0)
        {
            StatusText.Text = "Enter a command.";
            return;
        }
        if (MessageBox.Show(this,
            $"Run the following shell command on {_devices.Count} device(s)?\n\n{command}\n\nADB commands may modify device state.",
            "Confirm batch shell", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunBatchAsync(async (device, ct) =>
        {
            var result = await _bridge.RunShellCommandResultAsync(device.Serial, command, ct);
            result.EnsureSuccess("Batch shell command failed.");
            return result.FormatForDisplay(4000);
        });
    }

    private async void InstallApk_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select APK for all selected devices",
            Filter = "Android APK (*.apk)|*.apk"
        };
        if (dialog.ShowDialog(this) != true) return;

        if (MessageBox.Show(this,
            $"Install {Path.GetFileName(dialog.FileName)} on {_devices.Count} device(s)?",
            "Confirm batch install", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunBatchAsync(async (device, ct) =>
        {
            await _bridge.InstallAsync(device.Serial, dialog.FileName, ct);
            return "Installed " + Path.GetFileName(dialog.FileName);
        });
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        StatusText.Text = "Cancellation requested. Current ADB command is being stopped.";
    }
}
