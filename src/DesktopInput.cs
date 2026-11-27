using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal sealed class DesktopInput : IDisposable
{
    private readonly Control owner;
    private readonly Action<Pointer> forward;
    private readonly Func<Point, bool> withinWallpaper;
    private readonly Action<DesktopKey> forwardKey;
    private readonly Hook callback;
    private readonly Hook keyboardCallback;
    private readonly Dictionary<uint, KeyboardData> gameKeys = new Dictionary<uint, KeyboardData>();
    private readonly HashSet<uint> swallowed = new HashSet<uint>();
    private IntPtr hook, keyboardHook;
    private Thread hitWorker;
    private volatile bool watching;
    private volatile Hit sample;
    private bool left, right;
    private int lastHoverTick, lastPressTick;
    private Point lastPress;
    private string lastButton;
    private int clickCount;

    internal DesktopInput(Control owner, Action<Pointer> forward, Func<Point, bool> withinWallpaper,
        Action<DesktopKey> forwardKey = null)
    {
        this.owner = owner;
        this.forward = forward;
        this.withinWallpaper = withinWallpaper;
        this.forwardKey = forwardKey;
        callback = OnMouse;
        keyboardCallback = OnKeyboard;
    }

    internal bool Active { get { return hook != IntPtr.Zero; } }

    internal bool Start()
    {
        if (Active) return true;
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (!Active) return false;
        if (forwardKey != null)
        {
            keyboardHook = SetWindowsHookEx(13, keyboardCallback, GetModuleHandle(null), 0);
            if (keyboardHook == IntPtr.Zero) { Stop(); return false; }
        }
        watching = true;
        if (hitWorker == null || !hitWorker.IsAlive)
        {
            hitWorker = new Thread(ObservePointer) { IsBackground = true, Name = "Desktop icon hit test" };
            hitWorker.SetApartmentState(ApartmentState.STA);
            hitWorker.Start();
        }
        return true;
    }

    internal void Stop()
    {
        watching = false;
        if (keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(keyboardHook);
            keyboardHook = IntPtr.Zero;
            ReleaseKeys();
            swallowed.Clear();
        }
        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
        Point end = Cursor.Position;
        if (left) forward(new Pointer("mouseReleased", "left", end, 0, clickCount, 0));
        if (right) forward(new Pointer("mouseReleased", "right", end, 0, clickCount, 0));
        left = right = false;
        sample = null;
    }

    private void ObservePointer()
    {
        // Desktop accessibility is read off-thread. An unavailable or slow result
        // never causes the mouse hook to steal input.
        bool previouslyEligible = false;
        while (watching)
        {
            Point point = Cursor.Position;
            bool empty = IsDesktopPoint(point) && IsBlank(point);
            sample = new Hit(point, empty, Environment.TickCount);
            bool eligible = forwardKey != null && empty && withinWallpaper(point) && IsDesktopWindow(GetForegroundWindow());
            if (previouslyEligible && !eligible)
            {
                try { owner.BeginInvoke((Action)CheckKeyboardFocus); }
                catch (InvalidOperationException) { }
            }
            previouslyEligible = eligible;
            Thread.Sleep(38);
        }
    }

    private IntPtr OnMouse(int code, IntPtr parameter, IntPtr address)
    {
        if (code < 0 || !watching) return CallNextHookEx(hook, code, parameter, address);
        int command = parameter.ToInt32();
        var mouse = (MouseData)Marshal.PtrToStructure(address, typeof(MouseData));
        int now = Environment.TickCount;
        bool down = command == 0x0201 || command == 0x0204;
        bool up = command == 0x0202 || command == 0x0205;
        bool usingLeft = command == 0x0201 || command == 0x0202;
        string button = usingLeft ? "left" : "right";
        bool empty = IsHovered(mouse.At, now) &&
            (forwardKey == null || IsDesktopWindow(GetForegroundWindow()));

        try
        {
            if (down && empty)
            {
                clickCount = lastButton == button && unchecked(now - lastPressTick) <= GetDoubleClickTime() &&
                    Math.Abs(lastPress.X - mouse.At.X) <= 4 && Math.Abs(lastPress.Y - mouse.At.Y) <= 4 ? 2 : 1;
                lastPressTick = now;
                lastPress = mouse.At;
                lastButton = button;
                if (usingLeft) left = true;
                else right = true;
                Queue(new Pointer("mousePressed", button, mouse.At, Buttons(), clickCount, 0));
                return new IntPtr(1);
            }
            if (up && (usingLeft ? left : right))
            {
                if (usingLeft) left = false;
                else right = false;
                Queue(new Pointer("mouseReleased", button, mouse.At, Buttons(), clickCount, 0));
                return new IntPtr(1);
            }
            if (command == 0x020A && empty)
            {
                double delta = -(short)(mouse.Extra >> 16);
                Queue(new Pointer("mouseWheel", "none", mouse.At, Buttons(), 0, delta));
                return new IntPtr(1);
            }
            if (command == 0x0200 && (empty || left || right) && unchecked(now - lastHoverTick) > 33)
            {
                lastHoverTick = now;
                Queue(new Pointer("mouseMoved", "none", mouse.At, Buttons(), 0, 0));
            }
        }
        catch (InvalidOperationException) { }
        return CallNextHookEx(hook, code, parameter, address);
    }

    private bool IsHovered(Point at, int now)
    {
        var nearby = sample;
        return nearby != null && nearby.Empty && unchecked((uint)(now - nearby.When)) <= 180 &&
            Math.Abs(nearby.At.X - at.X) <= 2 && Math.Abs(nearby.At.Y - at.Y) <= 2 &&
            withinWallpaper(at) && IsDesktopPoint(at);
    }

    private bool HasKeyboardFocus()
    {
        return watching && IsDesktopWindow(GetForegroundWindow()) && IsHovered(Cursor.Position, Environment.TickCount);
    }

    private void CheckKeyboardFocus()
    {
        if (gameKeys.Count > 0 && !HasKeyboardFocus()) ReleaseKeys();
    }

    private IntPtr OnKeyboard(int code, IntPtr parameter, IntPtr address)
    {
        if (code < 0 || !watching) return CallNextHookEx(keyboardHook, code, parameter, address);
        var key = (KeyboardData)Marshal.PtrToStructure(address, typeof(KeyboardData));
        bool up = (key.Flags & 0x80) != 0;
        bool owned = swallowed.Contains(key.VirtualKey);
        if (up)
        {
            swallowed.Remove(key.VirtualKey);
            if (gameKeys.Remove(key.VirtualKey)) SendKey(key, true, false);
            if (owned) return new IntPtr(1);
        }
        if (!HasKeyboardFocus()) ReleaseKeys();
        else if (!up && !SystemShortcut(key))
        {
            if (owned && !gameKeys.ContainsKey(key.VirtualKey)) return new IntPtr(1);
            if (SendKey(key, false, gameKeys.ContainsKey(key.VirtualKey)))
            {
                gameKeys[key.VirtualKey] = key;
                // Windows must also see Ctrl and Shift, including for system
                // shortcuts such as the control toggle and layout switching.
                if (key.VirtualKey < 160 || key.VirtualKey > 163)
                {
                    swallowed.Add(key.VirtualKey);
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(keyboardHook, code, parameter, address);
    }

    private static bool SystemShortcut(KeyboardData key)
    {
        return (key.Flags & 0x20) != 0 || (key.VirtualKey >= 164 && key.VirtualKey <= 165) ||
            key.VirtualKey == 44 || key.VirtualKey == 91 || key.VirtualKey == 92 ||
            IsDown(18) || IsDown(91) || IsDown(92) || (key.VirtualKey == 27 && IsDown(17));
    }

    private static bool IsDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

    private bool SendKey(KeyboardData key, bool up, bool repeat)
    {
        bool shift = key.VirtualKey == 160 || key.VirtualKey == 161 ? !up || IsDown(key.VirtualKey == 160 ? 161 : 160) : IsDown(16);
        bool control = key.VirtualKey == 162 || key.VirtualKey == 163 ? !up || IsDown(key.VirtualKey == 162 ? 163 : 162) : IsDown(17);
        int modifiers = (shift ? 8 : 0) | (control ? 2 : 0);
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero));
        DesktopKey input = DesktopKey.Map(key.VirtualKey, key.Scan, (key.Flags & 1) != 0, up, repeat, modifiers, layout);
        if (input == null) return false;
        try { owner.BeginInvoke((Action)delegate { forwardKey(input); }); }
        catch (InvalidOperationException) { return false; }
        return true;
    }

    private void ReleaseKeys()
    {
        foreach (KeyboardData key in gameKeys.Values) SendKey(key, true, false);
        gameKeys.Clear();
    }

    private void Queue(Pointer point)
    {
        if (owner.IsHandleCreated && !owner.IsDisposed)
            owner.BeginInvoke((Action)delegate { forward(point); });
    }

    private int Buttons() { return (left ? 1 : 0) | (right ? 2 : 0); }

    internal static bool IsDesktopPoint(Point point)
    {
        return IsDesktopWindow(WindowFromPoint(point));
    }

    private static bool IsDesktopWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        IntPtr root = GetAncestor(window, 2);
        if (root == IntPtr.Zero) return false;
        var kind = new System.Text.StringBuilder(32);
        GetClassName(root, kind, kind.Capacity);
        if (kind.ToString() != "Progman" && kind.ToString() != "WorkerW") return false;
        uint actual, explorer;
        GetWindowThreadProcessId(root, out actual);
        GetWindowThreadProcessId(GetShellWindow(), out explorer);
        return explorer != 0 && explorer == actual;
    }

    internal static int DesktopRole(Point point)
    {
        Accessibility.IAccessible location = null;
        try
        {
            object child;
            if (AccessibleObjectFromPoint(point, out location, out child) != 0 || location == null) return -1;
            object role = location.get_accRole(child);
            return role is int ? (int)role : -1;
        }
        catch (COMException) { return -1; }
        catch (InvalidCastException) { return -1; }
        finally { if (location != null && Marshal.IsComObject(location)) Marshal.ReleaseComObject(location); }
    }

    private static bool IsBlank(Point point)
    {
        // ROLE_SYSTEM_LIST is the desktop list itself. An icon resolves to a
        // list item, and every unrecognized role remains under Windows control.
        return DesktopRole(point) == 0x21;
    }

    public void Dispose() { Stop(); }

    private sealed class Hit
    {
        internal readonly Point At;
        internal readonly bool Empty;
        internal readonly int When;
        internal Hit(Point at, bool empty, int when) { At = at; Empty = empty; When = when; }
    }

    internal sealed class Pointer
    {
        internal readonly string Type, Button;
        internal readonly Point At;
        internal readonly int Buttons, Clicks;
        internal readonly double Delta;
        internal Pointer(string type, string button, Point at, int buttons, int clicks, double delta)
        { Type = type; Button = button; At = at; Buttons = buttons; Clicks = clicks; Delta = delta; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData { internal Point At; internal uint Extra, Flags, Time; internal IntPtr Info; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData { internal uint VirtualKey, Scan, Flags, Time; internal IntPtr Info; }
    private delegate IntPtr Hook(int code, IntPtr parameter, IntPtr data);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int id, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr parameter, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromPoint(Point point,
        [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible element,
        [MarshalAs(UnmanagedType.Struct)] out object child);
}
