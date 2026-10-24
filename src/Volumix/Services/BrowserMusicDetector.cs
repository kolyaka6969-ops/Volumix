using System;
using System.Collections.Generic;
using System.IO;

namespace Volumix.Services;

/// <summary>Музыкальный сайт, открытый в браузере.</summary>
public enum BrowserMusic
{
    None,
    YouTube,
    YouTubeMusic,
    YandexMusic,
}

/// <summary>
/// Узнаёт, что играет в браузере. Windows отдаёт в медиа-сеансах только программу, а не сайт,
/// поэтому смотрим заголовки окон браузеров: у вкладки Яндекс Музыки в нём есть «Яндекс Музыка»,
/// у YouTube Music — «YouTube Music», у обычного ролика — «YouTube».
/// </summary>
internal sealed class BrowserMusicDetector
{
    /// <summary>Сколько помним, что музыкальный сайт видели: вкладка может уйти в фон, а песня — продолжаться.</summary>
    internal static readonly TimeSpan Memory = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    private static readonly string[] BrowserExes =
    {
        "chrome", "chromium", "msedge", "firefox", "opera", "brave", "vivaldi", "browser", "yandexbrowser",
    };

    private readonly List<Window> _windows = new();
    private DateTime _checkedAt = DateTime.MinValue;
    private DateTime _lastSeen = DateTime.MinValue;
    private BrowserMusic _kind = BrowserMusic.None;

    /// <summary>Что за музыкальный сайт видели только что или недавно. Сканирует окна не чаще раза в две секунды.</summary>
    public BrowserMusic Check()
    {
        Refresh();
        var now = DateTime.UtcNow;
        return now - _lastSeen < Memory ? _kind : BrowserMusic.None;
    }

    /// <summary>
    /// Уточняет сайт по названию песни: если оно есть в заголовке окна, значит вкладка на виду
    /// и сайт известен точно. Иначе остаётся то, что запомнили.
    /// </summary>
    public BrowserMusic Match(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return BrowserMusic.None;
        var needle = title.Trim();
        // Слишком короткое название встречается в любом заголовке — так можно принять чужую вкладку за свою.
        if (needle.Length < 6)
            return BrowserMusic.None;
        Refresh();
        foreach (var window in _windows)
        {
            if (window.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return window.Kind;
        }
        return BrowserMusic.None;
    }

    /// <summary>
    /// Продлевает память, пока браузерный сеанс считается музыкой: песня играет часами,
    /// а заголовок окна браузера меняется вместе с вкладкой.
    /// </summary>
    public void Touch(BrowserMusic kind)
    {
        if (kind == BrowserMusic.None)
            return;
        var now = DateTime.UtcNow;
        _kind = kind;
        _lastSeen = now;
        _checkedAt = now;
    }

    /// <summary>«Яндекс Музыка», «Яндекс.Музыка», «Yandex Music» — всё это одна вкладка.</summary>
    internal static bool LooksLikeYandexMusic(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        var lower = text.ToLowerInvariant();
        return (lower.Contains("яндекс") || lower.Contains("yandex"))
            && (lower.Contains("музык") || lower.Contains("music"));
    }

    /// <summary>«YouTube Music», «Song — YouTube Music» — сайт YouTube Music, а не обычный ролик.</summary>
    internal static bool LooksLikeYouTubeMusic(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains("youtube music", StringComparison.OrdinalIgnoreCase);

    /// <summary>Любая вкладка YouTube. У обычного ролика в заголовке «… — YouTube».</summary>
    internal static bool LooksLikeYouTube(string? text) =>
        !string.IsNullOrEmpty(text) && text.Contains("youtube", StringComparison.OrdinalIgnoreCase);

    internal static BrowserMusic ClassifyTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return BrowserMusic.None;
        if (LooksLikeYandexMusic(title))
            return BrowserMusic.YandexMusic;
        if (LooksLikeYouTubeMusic(title))
            return BrowserMusic.YouTubeMusic;
        if (LooksLikeYouTube(title))
            return BrowserMusic.YouTube;
        return BrowserMusic.None;
    }

    /// <summary>Сайты по важности: музыкальные плееры важнее обычного YouTube.</summary>
    internal static int Rank(BrowserMusic kind) => kind switch
    {
        BrowserMusic.YandexMusic => 3,
        BrowserMusic.YouTubeMusic => 2,
        BrowserMusic.YouTube => 1,
        _ => 0,
    };

    private void Refresh()
    {
        var now = DateTime.UtcNow;
        if (now - _checkedAt < CheckInterval)
            return;
        _checkedAt = now;

        var windows = new List<Window>();
        var found = BrowserMusic.None;
        try
        {
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                    return true;
                var title = NativeMethods.GetWindowTitle(hWnd);
                var kind = ClassifyTitle(title);
                if (kind == BrowserMusic.None)
                    return true;
                if (!IsBrowserWindow(hWnd))
                    return true;
                windows.Add(new Window(title, kind));
                if (Rank(kind) > Rank(found))
                    found = kind;
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            // Окна перебираются на лету: если не вышло, просто не считаем, что сайт открыт.
        }

        _windows.Clear();
        _windows.AddRange(windows);
        if (found != BrowserMusic.None)
        {
            _kind = found;
            _lastSeen = now;
        }
    }

    private static bool IsBrowserWindow(IntPtr hWnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0 || pid == Environment.ProcessId)
                return false;
            var path = NativeMethods.GetProcessPath(pid);
            if (path == null)
                return false;
            var exe = Path.GetFileNameWithoutExtension(path);
            return Array.Exists(BrowserExes, b => string.Equals(b, exe, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private readonly record struct Window(string Title, BrowserMusic Kind);
}
