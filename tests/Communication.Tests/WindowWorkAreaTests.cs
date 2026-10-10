using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Communication.App;

internal static class WindowWorkAreaTests
{
    public static void VerifyMaximized(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info) || !GetWindowRect(hwnd, out var actual))
            throw new InvalidOperationException("Unable to inspect maximized native window bounds");
        if (actual.Left < info.Work.Left || actual.Top < info.Work.Top || actual.Right > info.Work.Right || actual.Bottom > info.Work.Bottom)
            throw new InvalidOperationException($"Maximized window exceeds its monitor work area: window={actual.Left},{actual.Top},{actual.Right},{actual.Bottom}; work={info.Work.Left},{info.Work.Top},{info.Work.Right},{info.Work.Bottom}");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size; public NativeRect Monitor, Work; public int Flags;
    }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect bounds);
    public static void Run()
    {
        var calculate = typeof(MainWindow).Assembly.GetType("Communication.App.Presentation.WindowWorkArea")!
            .GetMethod("CalculateBounds", BindingFlags.NonPublic | BindingFlags.Static)!;
        Rect Bounds(Rect monitor, Rect work, params int[] edges) => (Rect)calculate.Invoke(null, [monitor, work, edges])!;
        void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }
        var primary = new Rect(0, 0, 1920, 1080);
        var reserved = new Rect(0, 0, 1920, 1032);
        Check(Bounds(primary, reserved) == reserved, "Maximized windows respect a visible taskbar's work area");
        Check(Bounds(primary, primary, 3).Bottom == 1078, "Bottom auto-hide taskbar retains its activation edge");
        Check(Bounds(primary, primary, 0).Left == 2, "Left auto-hide taskbar retains its activation edge");
        Check(Bounds(primary, primary, 1).Top == 2, "Top auto-hide taskbar retains its activation edge");
        Check(Bounds(primary, primary, 2).Right == 1918, "Right auto-hide taskbar retains its activation edge");
        Check(Bounds(primary, reserved, 3) == reserved, "Visible taskbar reservation is not subtracted twice");
        var secondary = new Rect(-2560, -200, 2560, 1440);
        var bounds = Bounds(secondary, secondary, 0, 3);
        Check(bounds == new Rect(-2558, -200, 2558, 1438), "Negative monitor origins and high-resolution screens retain physical-pixel activation gaps");
    }
}
