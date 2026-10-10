using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Communication.App.Presentation;

/// <summary>Keep borderless maximized windows in the monitor work area, including auto-hide activation edges.</summary>
internal static class WindowWorkArea
{
    public static void Attach(Window window)
    {
        HwndSource? source = null;
        window.SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(WindowProc);
        };
        window.Closed += (_, _) => source?.RemoveHook(WindowProc);
    }

    private static nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        if (message != WmGetMinMaxInfo)
            return 0;
        var monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return 0;
        var edges = new List<int>();
        for (var edge = 0; edge < 4; edge++)
        {
            var bar = new AppBarData { Size = Marshal.SizeOf<AppBarData>(), Edge = edge, Bounds = info.Monitor };
            if (SHAppBarMessage(0x0000000B, ref bar) != 0)
                edges.Add(edge); // ABM_GETAUTOHIDEBAREX
        }
        var bounds = CalculateBounds(ToRect(info.Monitor), ToRect(info.Work), edges);
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition = new((int)(bounds.Left - info.Monitor.Left), (int)(bounds.Top - info.Monitor.Top));
        limits.MaxSize = new((int)bounds.Width, (int)bounds.Height);
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return 0;
    }

    // Coordinates are physical screen pixels, so this also works with mixed-DPI and negative-origin monitors.
    internal static Rect CalculateBounds(Rect monitor, Rect work, IReadOnlyCollection<int> autoHideEdges)
    {
        var left = work.Left;
        var top = work.Top;
        var right = work.Right;
        var bottom = work.Bottom;
        const int activationGap = 2;
        foreach (var edge in autoHideEdges)
            switch (edge)
            {
                case 0 when left == monitor.Left:
                    left += activationGap;
                    break;
                case 1 when top == monitor.Top:
                    top += activationGap;
                    break;
                case 2 when right == monitor.Right:
                    right -= activationGap;
                    break;
                case 3 when bottom == monitor.Bottom:
                    bottom -= activationGap;
                    break;
            }
        return new Rect(left, top, right - left, bottom - top);
    }
    private static Rect ToRect(NativeRect value) => new(value.Left, value.Top, value.Right - value.Left, value.Bottom - value.Top);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point(int x, int y)
    {
        public int X = x, Y = y;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size; public NativeRect Monitor, Work; public int Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int Size; public nint Window; public uint CallbackMessage; public int Edge; public NativeRect Bounds; public nint Parameter;
    }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shell32.dll")] private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);
}
