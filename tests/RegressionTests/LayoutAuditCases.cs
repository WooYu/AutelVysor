using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AutelVysor.Models;
using AutelVysor.Services;

internal static partial class Program
{
    private static System.Windows.Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new System.Windows.Rect(new Point(), element.RenderSize));

    private static async Task MinimumWindowLayouts()
    {
        var main = new AutelVysor.MainWindow { Width = 400, Height = 600, ShowInTaskbar = false };
        using var mirror = new AutelVysor.Controls.MirrorSessionControl(Bridge(), null, null, new AppSettings(), TestDevice());
        try
        {
            var tabs = (TabControl)main.FindName("SessionTabs");
            tabs.Items.Add(new TabItem { Header = "Device", Content = mirror });
            ((FrameworkElement)main.FindName("EmptyWorkspace")).Visibility = Visibility.Collapsed;
            main.Show(); await Task.Delay(60);
            ((ColumnDefinition)main.FindName("SidebarColumn")).Width = new GridLength(260);
            ((Expander)main.FindName("MirrorSettingsSection")).IsExpanded = true;
            var focus = (Button)mirror.FindName("FocusKeyboardButton");
            focus.Content = "Ready ✓";
            var overlayTitle = (TextBlock)mirror.FindName("OverlayTitle");
            overlayTitle.Text = "Mirror unavailable";
            main.UpdateLayout();
            Check(((ColumnDefinition)main.FindName("SidebarColumn")).ActualWidth >= 260, "Sidebar became too narrow for its controls");
            var label = (TextBlock)((CheckBox)mirror.FindName("PhoneKeyboardToggle")).Content;
            var labelText = new FormattedText(label.Text, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                label.FontSize, System.Windows.Media.Brushes.White, VisualTreeHelper.GetDpi(label).PixelsPerDip);
            Check(labelText.Width <= label.ActualWidth + 1 || label.ActualHeight > label.FontSize * 1.3,
                "Phone keyboard option does not wrap in the narrow sidebar");
            var focusText = new FormattedText(focus.Content.ToString()!, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface(focus.FontFamily, focus.FontStyle, focus.FontWeight, focus.FontStretch),
                focus.FontSize, System.Windows.Media.Brushes.White, VisualTreeHelper.GetDpi(focus).PixelsPerDip);
            Check(focusText.Width <= focus.ActualWidth - focus.Padding.Left - focus.Padding.Right + 1,
                "Keyboard focus status is clipped in the narrow sidebar");
            var titleText = new FormattedText(overlayTitle.Text, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface(overlayTitle.FontFamily, overlayTitle.FontStyle, overlayTitle.FontWeight, overlayTitle.FontStretch),
                overlayTitle.FontSize, System.Windows.Media.Brushes.White, VisualTreeHelper.GetDpi(overlayTitle).PixelsPerDip);
            Check(titleText.Width <= overlayTitle.ActualWidth + 1 || overlayTitle.ActualHeight > overlayTitle.FontSize * 1.3,
                "Narrow mirror overlay title does not wrap");
            SaveUsabilityRender(main, "layout-main.png");
        }
        finally { main.Close(); }

        var debug = DebugFixtureWindow();
        debug.Width = debug.MinWidth; debug.Height = debug.MinHeight; debug.ShowInTaskbar = false;
        try
        {
            debug.Show(); await Task.Delay(100);
            var name = (TextBlock)debug.FindName("DeviceNameText");
            name.Text = new string('W', 150);
            debug.UpdateLayout();
            var refresh = (Button)debug.FindName("RefreshPackagesButton");
            Check(Bounds(name, debug).Right <= Bounds(refresh, debug).Left,
                "Long device name overlaps header actions");
            var tabs = (TabControl)debug.FindName("DebugTabs");
            for (var index = 0; index < tabs.Items.Count; index++)
            {
                tabs.SelectedIndex = index;
                await Task.Delay(30); debug.UpdateLayout();
                if (index == 0)
                {
                    var package = (ComboBox)debug.FindName("PackageBox");
                    var install = (Button)debug.FindName("InstallApkButton");
                    Check(Math.Abs(Bounds(package, debug).Bottom - Bounds(install, debug).Bottom) < 2,
                        "Package field and install button do not align");
                }
                if (index == 4)
                {
                    var command = (TextBox)debug.FindName("ShellCommandBox");
                    var run = (Button)debug.FindName("RunShellButton");
                    Check(Math.Abs(Bounds(command, debug).Bottom - Bounds(run, debug).Bottom) < 2,
                        "Shell input and run button do not align");
                }
                SaveUsabilityRender(debug, $"layout-debug-{index}.png");
            }
        }
        finally { debug.Close(); }

        var devices = Enumerable.Range(0, 40).Select(i => new AndroidDevice(
            $"test-device-{i:D3}", "device", "Long device name for layout testing", "", "", "1", "")).ToArray();
        var batch = new AutelVysor.BatchOperationsWindow(Bridge(), devices) { ShowInTaskbar = false };
        batch.Width = batch.MinWidth; batch.Height = batch.MinHeight;
        try
        {
            batch.Show(); await Task.Delay(30); batch.UpdateLayout();
            var list = (TextBlock)batch.FindName("DevicesText");
            var output = (TextBox)batch.FindName("OutputBox");
            Check(list.ActualHeight <= 68 && output.ActualHeight >= 100, "Long device list crowds out batch output");
            SaveUsabilityRender(batch, "layout-batch.png");
        }
        finally { batch.Close(); }

        var capture = new AutelVysor.CaptureHistoryWindow { ShowInTaskbar = false };
        capture.Width = capture.MinWidth; capture.Height = capture.MinHeight;
        try { capture.Show(); await Task.Delay(30); SaveUsabilityRender(capture, "layout-captures.png"); }
        finally { capture.Close(); }

        var keyboard = new AdbKeyboardService("unused", runner: (_, _) => Task.FromResult((0, "", "")));
        var unicode = new AutelVysor.UnicodeSetupWindow(keyboard, "test-device", "", true) { ShowInTaskbar = false };
        unicode.Width = unicode.MinWidth; unicode.Height = unicode.MinHeight;
        try
        {
            unicode.Show(); await Task.Delay(30); SaveUsabilityRender(unicode, "layout-unicode.png");
            ((ScrollViewer)unicode.FindName("SetupScroll")).ScrollToEnd();
            await Task.Delay(30); SaveUsabilityRender(unicode, "layout-unicode-bottom.png");
        }
        finally { unicode.Close(); }
    }
}
