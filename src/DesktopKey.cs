using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

internal sealed class DesktopKey
{
    private static readonly Dictionary<uint, string> Special = new Dictionary<uint, string>
    {
        { 8, "Backspace" }, { 9, "Tab" }, { 13, "Enter" }, { 19, "Pause" }, { 20, "CapsLock" },
        { 27, "Escape" }, { 32, "Space" }, { 33, "PageUp" }, { 34, "PageDown" }, { 35, "End" },
        { 36, "Home" }, { 37, "ArrowLeft" }, { 38, "ArrowUp" }, { 39, "ArrowRight" },
        { 40, "ArrowDown" }, { 45, "Insert" }, { 46, "Delete" },
        { 106, "NumpadMultiply" }, { 107, "NumpadAdd" }, { 109, "NumpadSubtract" },
        { 110, "NumpadDecimal" }, { 111, "NumpadDivide" },
        { 144, "NumLock" }, { 145, "ScrollLock" },
        { 160, "ShiftLeft" }, { 161, "ShiftRight" }, { 162, "ControlLeft" }, { 163, "ControlRight" },
        { 186, "Semicolon" }, { 187, "Equal" }, { 188, "Comma" }, { 189, "Minus" },
        { 190, "Period" }, { 191, "Slash" }, { 192, "Backquote" }, { 219, "BracketLeft" },
        { 220, "Backslash" }, { 221, "BracketRight" }, { 222, "Quote" }, { 226, "IntlBackslash" }
    };

    // The physical key stays the same when the active Windows layout changes.
    private const string NumberRow = "1234567890";
    private const string TopRow = "QWERTYUIOP";
    private const string HomeRow = "ASDFGHJKL";
    private const string BottomRow = "ZXCVBNM";

    internal string Json { get; private set; }

    internal static DesktopKey Map(uint vk, uint scan, bool extended, bool up, bool repeat,
        int modifiers, IntPtr layout)
    {
        string code;
        if (scan >= 0x02 && scan <= 0x0b) code = "Digit" + NumberRow[(int)scan - 0x02];
        else if (scan >= 0x10 && scan <= 0x19) code = "Key" + TopRow[(int)scan - 0x10];
        else if (scan >= 0x1e && scan <= 0x26) code = "Key" + HomeRow[(int)scan - 0x1e];
        else if (scan >= 0x2c && scan <= 0x32) code = "Key" + BottomRow[(int)scan - 0x2c];
        else if (vk >= 112 && vk <= 135) code = "F" + (vk - 111);
        else if (vk >= 96 && vk <= 105) code = "Numpad" + (vk - 96);
        else if (vk == 13 && extended) code = "NumpadEnter";
        else if (!Special.TryGetValue(vk, out code)) return null;

        int location = code.StartsWith("Numpad", StringComparison.Ordinal) ? 3 :
            vk == 160 || vk == 162 ? 1 : vk == 161 || vk == 163 ? 2 : 0;
        string key = LogicalName(code);
        string modified = Translate(vk, scan, modifiers, layout);
        if (modified.Length != 0 && vk != 13) key = modified;
        string text = !up && (modifiers & 2) == 0 ? modified : "";
        int browserVk = vk == 160 || vk == 161 ? 16 : vk == 162 || vk == 163 ? 17 : (int)vk;
        return new DesktopKey {
            Json = String.Format(CultureInfo.InvariantCulture,
                "{{\"type\":\"{0}\",\"code\":\"{1}\",\"key\":{2},\"text\":{3},\"unmodifiedText\":{4}," +
                "\"windowsVirtualKeyCode\":{5},\"nativeVirtualKeyCode\":{6},\"location\":{7}," +
                "\"modifiers\":{8},\"autoRepeat\":{9},\"isKeypad\":{10}}}",
                up ? "keyUp" : text.Length == 0 ? "rawKeyDown" : "keyDown",
                code, Quote(key), Quote(text), Quote(text), browserVk, scan, location, modifiers,
                repeat ? "true" : "false", location == 3 ? "true" : "false")
        };
    }

    private static string LogicalName(string code)
    {
        if (code == "Space") return " ";
        if (code == "NumpadEnter") return "Enter";
        if (code.StartsWith("Shift", StringComparison.Ordinal)) return "Shift";
        if (code.StartsWith("Control", StringComparison.Ordinal)) return "Control";
        if (code.StartsWith("Key", StringComparison.Ordinal)) return code.Substring(3).ToLowerInvariant();
        if (code.StartsWith("Digit", StringComparison.Ordinal)) return code.Substring(5);
        return code;
    }

    private static string Translate(uint vk, uint scan, int modifiers, IntPtr layout)
    {
        if (vk == 13) return "\r";
        if (vk < 32 || vk >= 112 && vk <= 183 || vk == 45 || vk == 46) return "";
        var state = new byte[256];
        if ((modifiers & 8) != 0) state[16] = 0x80;
        if ((GetKeyState(20) & 1) != 0) state[20] = 1;
        if ((GetKeyState(144) & 1) != 0) state[144] = 1;
        var characters = new StringBuilder(8);
        int count = ToUnicodeEx(vk, scan, state, characters, characters.Capacity, 4, layout);
        return count > 0 ? characters.ToString(0, count) : "";
    }

    private static string Quote(string value)
    {
        var json = new StringBuilder("\"");
        foreach (char character in value)
            json.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
        return json.Append('"').ToString();
    }

    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder text, int capacity, uint flags, IntPtr layout);
}
