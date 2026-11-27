using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class DesktopLayer
{
    private const int Style = -16;
    private const int ExStyle = -20;
    private const int Child = 0x40000000;
    private const int Popup = unchecked((int)0x80000000);
    private const int Frame = 0x00C40000;
    private const int NormalWindow = 0x00CF0000;
    private const int Layered = 0x00080000;
    private const int NoRedirectionBitmap = 0x00200000;
    private const int ToolWindow = 0x00000080;
    private const int AppWindow = 0x00040000;
    private const int NoActivate = 0x08000000;
    private const int ClipChildren = 0x02000000;
    private const uint FrameChanged = 0x0020;
    private const uint NoFocus = 0x0010;
    private const uint Show = 0x0040;

    internal static string Attach(IntPtr window, Rectangle monitor, bool show = true)
    {
        IntPtr programManager = FindWindow("Progman", null);
        if (programManager == IntPtr.Zero)
            throw new InvalidOperationException("未找到 Windows 桌面。请打开窗口预览。");

        IntPtr result;
        bool raised = (GetWindowLong(programManager, ExStyle) & NoRedirectionBitmap) != 0;
        IntPtr icons = FindWindowEx(programManager, IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr worker = FindWindowEx(programManager, IntPtr.Zero, "WorkerW", null);
        if (worker == IntPtr.Zero || !raised)
        {
            SendMessageTimeout(programManager, 0x052C, new IntPtr(0xD), new IntPtr(1), 0x0002, 800, out result);
            icons = FindWindowEx(programManager, IntPtr.Zero, "SHELLDLL_DefView", null);
            worker = FindWindowEx(programManager, IntPtr.Zero, "WorkerW", null);
        }

        IntPtr parent;
        IntPtr after;
        if (raised && icons != IntPtr.Zero && worker != IntPtr.Zero)
        {
            parent = programManager;
            after = icons;
        }
        else
        {
            raised = false;
            parent = ClassicWorker();
            after = IntPtr.Zero;
        }
        if (parent == IntPtr.Zero)
            throw new InvalidOperationException("找不到桌面图标后方的壁纸层。仍可用窗口预览。");

        ReleaseTopmost(window);
        int style = GetWindowLong(window, Style);
        int extended = GetWindowLong(window, ExStyle);
        UpdateStyle(window, Style, (style & ~(Popup | Frame)) | Child | ClipChildren);
        UpdateStyle(window, ExStyle, (extended & ~AppWindow) | ToolWindow | NoActivate | (raised ? Layered : 0));
        Reparent(window, parent);
        if (raised && !SetLayeredWindowAttributes(window, 0, 255, 2)) throw new Win32Exception();
        var origin = new Point(monitor.X, monitor.Y);
        if (!ScreenToClient(parent, ref origin)) throw new Win32Exception();
        Position(window, after, new Rectangle(origin, monitor.Size), NoFocus | FrameChanged | (show ? Show : 0));
        if (GetAncestor(window, 1) != parent) throw new InvalidOperationException("桌面挂接没有成功。");
        return raised ? "Windows 11 layered desktop" : "WorkerW desktop";
    }

    internal static void Float(IntPtr window, Rectangle area, bool preview, bool focus)
    {
        if (IsAttached(window)) Reparent(window, IntPtr.Zero);
        ReleaseTopmost(window);
        int style = GetWindowLong(window, Style);
        int extended = GetWindowLong(window, ExStyle);
        int presentation = preview ? NormalWindow : Popup;
        UpdateStyle(window, Style, (style & ~(Child | NormalWindow | Popup)) | presentation | ClipChildren);
        UpdateStyle(window, ExStyle, (extended & ~(Layered | ToolWindow | AppWindow | NoActivate)) |
            (preview ? AppWindow : ToolWindow));
        Position(window, preview ? new IntPtr(-2) : new IntPtr(-1), area, FrameChanged | Show | (focus ? 0 : NoFocus));
        if (focus) SetForegroundWindow(window);
    }

    internal static void ReleaseTopmost(IntPtr window)
    {
        SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | NoFocus);
    }

    internal static void KeepPlayVisible(IntPtr window)
    {
        SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | NoFocus);
    }

    internal static Rectangle OuterBounds(IntPtr window)
    {
        Rect r;
        if (!GetWindowRect(window, out r)) throw new Win32Exception();
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    internal static Rectangle FitExternalWallpaper(IntPtr window)
    {
        IntPtr parent = GetAncestor(window, 1);
        if (parent == IntPtr.Zero || parent == GetDesktopWindow()) return OuterBounds(window);

        Rect client;
        if (!GetClientRect(parent, out client)) throw new Win32Exception();
        var size = new Size(client.Right - client.Left, client.Bottom - client.Top);
        if (size.Width <= 0 || size.Height <= 0) return OuterBounds(window);
        var origin = Point.Empty;
        if (!ClientToScreen(parent, ref origin)) throw new Win32Exception();
        var area = new Rectangle(origin, size);
        if (OuterBounds(window) != area)
            Position(window, IntPtr.Zero, new Rectangle(Point.Empty, size), NoFocus | 0x0004);
        return OuterBounds(window);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError=true)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError=true)]
    private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError=true)]
    private static extern bool ClientToScreen(IntPtr window, ref Point point);

    internal static bool IsAttached(IntPtr window)
    {
        IntPtr parent = GetAncestor(window, 1);
        return parent != IntPtr.Zero && parent != GetDesktopWindow();
    }

    private static IntPtr ClassicWorker()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr candidate, IntPtr data)
        {
            if (FindWindowEx(candidate, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            IntPtr next = FindWindowEx(IntPtr.Zero, candidate, "WorkerW", null);
            if (next == IntPtr.Zero || FindWindowEx(next, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return true;
            found = next;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static void UpdateStyle(IntPtr window, int index, int value)
    {
        SetLastError(0);
        int previous = SetWindowLong(window, index, value);
        if (previous == 0 && Marshal.GetLastWin32Error() != 0) throw new Win32Exception();
    }

    private static void Reparent(IntPtr window, IntPtr parent)
    {
        SetLastError(0);
        IntPtr previous = SetParent(window, parent);
        if (previous == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) throw new Win32Exception();
    }

    private static void Position(IntPtr window, IntPtr after, Rectangle rect, uint flags)
    {
        if (!SetWindowPos(window, after, rect.X, rect.Y, rect.Width, rect.Height, flags)) throw new Win32Exception();
    }

    private delegate bool EnumProc(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint="SetWindowLongW", SetLastError=true)] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetParent(IntPtr window, IntPtr parent);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr w, IntPtr l, uint flags, uint milliseconds, out IntPtr output);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
}
