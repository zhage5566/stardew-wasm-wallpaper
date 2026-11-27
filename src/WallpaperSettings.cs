using System;
using System.IO;
using Microsoft.Win32;

internal sealed class WallpaperSettings
{
    private readonly string favoritePath;
    private readonly string startupKey;
    private const string StartupValue = "StardewWasmWallpaper";

    internal WallpaperSettings(string data, string startupKey = @"Software\Microsoft\Windows\CurrentVersion\Run")
    {
        favoritePath = Path.Combine(data, "favorite-display.txt");
        this.startupKey = startupKey;
    }

    internal bool TryGetFavorite(out string display)
    {
        display = null;
        if (!File.Exists(favoritePath)) return false;
        string stored = File.ReadAllText(favoritePath).Trim();
        display = stored.Length == 0 ? null : stored;
        return true;
    }

    internal void SetFavorite(bool enabled, string display)
    {
        if (!enabled)
        {
            if (File.Exists(favoritePath)) File.Delete(favoritePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(favoritePath));
        string pending = favoritePath + ".tmp";
        File.WriteAllText(pending, display ?? "");
        if (File.Exists(favoritePath)) File.Replace(pending, favoritePath, null);
        else File.Move(pending, favoritePath);
    }

    internal bool StartsWithWindows(string executable)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(startupKey))
            return key != null && String.Equals(key.GetValue(StartupValue) as string,
                StartupCommand(executable), StringComparison.OrdinalIgnoreCase);
    }

    internal void SetStartup(bool enabled, string executable)
    {
        if (!enabled)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(startupKey, true))
                if (key != null) key.DeleteValue(StartupValue, false);
            return;
        }
        string command = StartupCommand(executable);
        if (command.Length > 260)
            throw new IOException("启动命令超过 Windows 的路径限制。请把便携包移到路径更短的文件夹。");
        using (var key = Registry.CurrentUser.CreateSubKey(startupKey))
            key.SetValue(StartupValue, command, RegistryValueKind.String);
    }

    private static string StartupCommand(string executable)
    {
        return "\"" + Path.GetFullPath(executable) + "\" --startup";
    }
}
