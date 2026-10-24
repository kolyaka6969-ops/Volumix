using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Volumix.Services;

public sealed record ProcessInfo(string Key, string DisplayName, ImageSource? Icon)
{
    private static readonly Dictionary<uint, ProcessInfo?> Cache = new();
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Возвращает null, если процесс уже завершён или недоступен.</summary>
    public static ProcessInfo? Get(uint pid)
    {
        if (Cache.TryGetValue(pid, out var cached))
            return cached;

        var info = Create(pid);
        Cache[pid] = info;
        return info;
    }

    public static void Forget(uint pid) => Cache.Remove(pid);

    public static string? GetKey(uint pid)
    {
        var path = NativeMethods.GetProcessPath(pid);
        return path is null ? null : Path.GetFileNameWithoutExtension(path);
    }

    private static ProcessInfo? Create(uint pid)
    {
        var path = NativeMethods.GetProcessPath(pid);
        if (path is null)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                return new ProcessInfo(p.ProcessName, p.ProcessName, null);
            }
            catch
            {
                return null;
            }
        }

        var key = Path.GetFileNameWithoutExtension(path);
        string name = key;
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc))
                name = desc.Trim();
        }
        catch
        {
            // оставляем имя файла
        }

        return new ProcessInfo(key, name, LoadIcon(path));
    }

    private static ImageSource? LoadIcon(string path)
    {
        if (IconCache.TryGetValue(path, out var icon))
            return icon;
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico != null)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                icon = src;
            }
        }
        catch
        {
            icon = null;
        }
        IconCache[path] = icon;
        return icon;
    }
}
