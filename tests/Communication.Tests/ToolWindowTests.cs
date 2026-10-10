using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Communication.App;
using Communication.App.Presentation;
using Communication.App.ViewModels;
using Communication.App.Views;

internal static class ToolWindowTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
    public static async Task Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ics-tool-window-{Guid.NewGuid():N}.json");
        var shell = new ShellViewModel(new WorkspaceViewModel(path));
        var main = new MainWindow(shell) { WindowState = WindowState.Normal, ShowInTaskbar = false };
        var closed = false;
        main.Closed += (_, _) => closed = true;
        var theme = Appearance.Current.Theme;
        var language = Appearance.Current.Language;
        try
        {
            main.Show();
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            main.WindowState = WindowState.Maximized;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            WindowWorkAreaTests.VerifyMaximized(main);
            main.WindowState = WindowState.Normal;
            shell.ModbusCommand.Execute(null);
            shell.ChecksumCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var tool = Application.Current.Windows.OfType<ChecksumWindow>().Single();
            Check(shell.IsModbus && shell.SelectedPageIndex == 1 && tool.Topmost && tool.IsVisible, "Checksum tool opens on top without changing the active feature");
            var navigation = Visuals<TabControl>(main).First();
            Check(navigation.Items.Count == 2, "Only endpoint and Modbus management remain in main navigation");
            shell.ChecksumCommand.Execute(null);
            Check(Application.Current.Windows.OfType<ChecksumWindow>().Single() == tool, "Repeated tool command reuses the existing window");
            tool.UpdateLayout();
            var preview = new RenderTargetBitmap((int)tool.ActualWidth, (int)tool.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            preview.Render(tool);
            var previewDirectory = Path.Combine(Path.GetTempPath(), "CommunicationStudio-appearance-preview");
            Directory.CreateDirectory(previewDirectory);
            using (var output = File.Create(Path.Combine(previewDirectory, "checksum-tool-compact.png")))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(preview));
                encoder.Save(output);
            }
            ((Button)tool.FindName("MinimizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(tool.WindowState == WindowState.Minimized, "Tool caption button minimizes the window");
            shell.ChecksumCommand.Execute(null);
            Check(tool.WindowState == WindowState.Normal && Application.Current.Windows.OfType<ChecksumWindow>().Single() == tool, "Tool command restores the minimized window rather than opening another");
            var pin = (ToggleButton)tool.FindName("PinButton");
            pin.IsChecked = false;
            Check(!tool.Topmost, "Unpinning disables always-on-top");
            pin.IsChecked = true;
            Check(tool.Topmost, "Pinning restores always-on-top");
            Appearance.Current.Apply("Light", "en-US", false);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(tool.Title == "Checksums" && ((SolidColorBrush)tool.Background).Color == ((SolidColorBrush)main.Background).Color, "Tool title and theme update with the application");
            shell.Checksum.Input = "01 02";
            tool.Close();
            shell.ChecksumCommand.Execute(null);
            var reopened = Application.Current.Windows.OfType<ChecksumWindow>().Single();
            Check(reopened != tool && reopened.Topmost && shell.Checksum.Input == "01 02", "Reopened tool defaults to pinned and retains calculation input");
            shell.DataConverterCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            var converter = Application.Current.Windows.OfType<DataConverterWindow>().Single();
            Check(converter.Topmost && shell.IsModbus && converter.Title == "Data Converter", "Converter opens pinned and localized without changing feature");
            shell.DataConverterCommand.Execute(null);
            Check(Application.Current.Windows.OfType<DataConverterWindow>().Single() == converter, "Converter window reused");
            ((Button)converter.FindName("MinimizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(converter.WindowState == WindowState.Minimized, "Converter minimizes before restore");
            shell.DataConverterCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(converter.WindowState == WindowState.Normal, "Converter restores from minimized");
            ((ToggleButton)converter.FindName("PinButton")).IsChecked = false;
            Check(!converter.Topmost, "Converter unpins");
            Check(converter.Owner is null && reopened.Owner is null && converter.ShowInTaskbar && reopened.ShowInTaskbar, "Tools are independent top-level taskbar windows");
            reopened.Topmost = false;
            main.WindowState = WindowState.Minimized;
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(converter.IsVisible && reopened.IsVisible && converter.WindowState == WindowState.Normal && reopened.WindowState == WindowState.Normal, $"Minimizing main keeps unpinned tools available: converter={converter.IsVisible}/{converter.WindowState}, checksum={reopened.IsVisible}/{reopened.WindowState}");
            Check(IsWindowVisible(new System.Windows.Interop.WindowInteropHelper(converter).Handle) && IsWindowVisible(new System.Windows.Interop.WindowInteropHelper(reopened).Handle), "Independent tool native windows remain visible");
            main.WindowState = WindowState.Normal;
            shell.DataConverter.Input = "3F 80 00 00";
            shell.DataConverter.OutputFormat = shell.DataConverter.Formats.Single(f => f.Format == Communication.Core.DataFormat.Single);
            shell.DataConverter.OutputBigEndian = true;
            for (var i = 0; i < 200 && shell.DataConverter.Busy; i++)
                await Task.Delay(10);
            Check(shell.DataConverter.Output == "1" && !shell.DataConverter.HasError, "Converter viewmodel processes byte order");
            Check(shell.DataConverter.ByteCount == 4 && shell.DataConverter.OutputByteCount == 4, "Both sides count raw bytes rather than display characters");
            shell.DataConverter.Input = "00";
            for (var i = 0; i < 200 && shell.DataConverter.Busy; i++)
                await Task.Delay(10);
            Check(shell.DataConverter.ByteCount == 1 && shell.DataConverter.OutputByteCount == 0 && shell.DataConverter.HasError, "Output conversion failure retains valid input byte count");
            shell.DataConverter.Input = "GG";
            for (var i = 0; i < 200 && shell.DataConverter.Busy; i++)
                await Task.Delay(10);
            Check(shell.DataConverter.HasError && shell.DataConverter.Output == "" && !shell.DataConverter.CopyCommand.CanExecute(null), "Invalid input clears stale output and disables copying");
            Check(shell.DataConverter.CopyInputCommand.CanExecute(null), "Invalid input can still be copied");
            shell.DataConverter.ClearCommand.Execute(null);
            Check(shell.DataConverter.Input == "" && !shell.DataConverter.CopyInputCommand.CanExecute(null), "Clearing empties input and disables input copy");
            shell.DataConverter.Input = "00";
            shell.DataConverter.Input = "00 01";
            shell.DataConverter.OutputFormat = shell.DataConverter.Formats.Single(f => f.Format == Communication.Core.DataFormat.Hex);
            for (var i = 0; i < 200 && shell.DataConverter.Busy; i++)
                await Task.Delay(10);
            Check(shell.DataConverter.Output == "00 01", "Rapid edits publish only the newest result");
            Appearance.Current.Apply("Dark", "zh-CN", false);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(converter.Title == "数据转换器", "Converter live language change");
            shell.DataConverter.Input = "00 AG";
            for (var i = 0; i < 200 && shell.DataConverter.Busy; i++)
                await Task.Delay(10);
            Check(shell.DataConverter.Error.Contains("输入格式无效") && shell.DataConverter.Error.Contains("A–F"), "Invalid HEX uses an actionable Chinese error");
            Check(shell.DataConverter.ByteCountText.EndsWith("字节"), "Byte count is localized");
            converter.UpdateLayout();
            var converterPreview = new RenderTargetBitmap((int)converter.ActualWidth, (int)converter.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            converterPreview.Render(converter);
            using (var output = File.Create(Path.Combine(previewDirectory, "data-converter-dark.png")))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(converterPreview));
                encoder.Save(output);
            }
            Appearance.Current.Apply("Light", "en-US", false);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Check(shell.DataConverter.Error.StartsWith("Invalid Hex:") && !shell.DataConverter.Error.Contains("输入"), "Existing error updates to English without changing input");
            Check(shell.DataConverter.ByteCountText.EndsWith("bytes"), "Byte count switches to English");
            Check(((SolidColorBrush)converter.Background).Color == ((SolidColorBrush)main.Background).Color, "Converter theme follows main window");
            converter.UpdateLayout();
            converterPreview = new RenderTargetBitmap((int)converter.ActualWidth, (int)converter.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            converterPreview.Render(converter);
            using (var output = File.Create(Path.Combine(previewDirectory, "data-converter-light.png")))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(converterPreview));
                encoder.Save(output);
            }
            shell.DataConverter.Input = "00 01";
            for (var i = 0; i < 200 && shell.DataConverter.Busy; i++)
                await Task.Delay(10);
            converter.Close();
            shell.DataConverterCommand.Execute(null);
            converter = Application.Current.Windows.OfType<DataConverterWindow>().Single();
            Check(converter.Topmost && shell.DataConverter.Input == "00 01", "Converter retains input after closing");
            main.Close();
            for (var i = 0; i < 200 && !closed; i++)
                await Task.Delay(10);
            Check(!converter.IsVisible, "Main close closes converter");
            Check(closed && !reopened.IsVisible, "Closing the application also closes its tool window");
        }
        finally
        {
            Appearance.Current.Apply(theme, language, false);
            if (!closed)
            {
                main.Close();
                for (var i = 0; i < 200 && !closed; i++)
                    await Task.Delay(10);
            }
            if (File.Exists(path))
                File.Delete(path);
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var nested in Visuals<T>(child))
                yield return nested;
        }
    }
}
