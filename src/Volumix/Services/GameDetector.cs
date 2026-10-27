using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace Volumix.Services;

/// <summary>
/// Играет ли пользователь прямо сейчас: на переднем плане игра (в том числе в окне, как Minecraft) или любое
/// полноэкранное окно — фильм, презентация. Островок с плеером в это время не показывается.
/// </summary>
internal sealed class GameDetector
{
    /// <summary>Не игру перепроверяем: игровой движок мог загрузиться позже окна (лаунчер Minecraft, заставка).</summary>
    private static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(15);
    /// <summary>Список игр Xbox Game Bar перечитываем не чаще раза в минуту.</summary>
    private static readonly TimeSpan GameBarRefresh = TimeSpan.FromMinutes(1);

    /// <summary>Папки магазинов игр. Лаунчеры (Steam, Epic Games Launcher) лежат вне их и играми не считаются.</summary>
    private static readonly string[] GameFolders =
    {
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\gog games\", @"\riot games\", @"\xboxgames\",
        @"\ubisoft game launcher\games\", @"\ea games\", @"\origin games\", @"\games\",
    };

    private static readonly string[] NotGameFolders = { @"\epic games\launcher\", @"\riot client\" };

    /// <summary>
    /// Программы, которые никогда не считаются играми. В список игр Xbox Game Bar (GameConfigStore) Windows нередко
    /// заносит браузеры, Discord, плееры или редакторы — из-за этого островок скрывался при клике по браузеру.
    /// </summary>
    internal static readonly HashSet<string> ExcludedExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "chromium", "msedge", "firefox", "opera", "brave", "vivaldi", "browser", "yandexbrowser", "thorium",
        "zen", "waterfox", "librewolf", "explorer", "devenv", "code", "spotify", "telegram", "discord", "steam",
        "epicgameslauncher", "antigravity", "taskmgr", "cmd", "powershell", "windowsterminal",
    };

    /// <summary>Библиотеки, которые грузят только игры: движки, игровые SDK магазинов, LWJGL у Minecraft.</summary>
    private static readonly HashSet<string> GameModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "unityplayer.dll", "gameassembly.dll", "lwjgl.dll", "lwjgl64.dll", "lwjgl32.dll", "lwjgl_opengl.dll", "glfw.dll",
        "steam_api.dll", "steam_api64.dll", "eossdk-win64-shipping.dll", "eossdk-win32-shipping.dll", "galaxy.dll",
        "galaxy64.dll", "fmod.dll", "fmod64.dll", "fmodstudio.dll", "fmodstudio64.dll", "bink2w64.dll", "binkw32.dll",
        "binkw64.dll", "physx3_x64.dll", "physx_64.dll", "gameoverlayrenderer64.dll",
    };

    /// <summary>Окна рабочего стола и панели задач: на них островок нужен.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly Dictionary<uint, (string Path, bool Game, DateTime Checked)> _processes = new();
    private HashSet<string> _gameBar = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _gameBarRead = DateTime.MinValue;
    private readonly uint _ownProcess = (uint)Environment.ProcessId;
    private IntPtr _lastForegroundHwnd;
    private bool _lastForegroundResult;
    private DateTime _lastForegroundTime;

    /// <summary>Сейчас на переднем плане игра или полноэкранное окно.</summary>
    public bool IsGameInFront()
    {
        try
        {
            var now = DateTime.UtcNow;
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == _lastForegroundHwnd && now - _lastForegroundTime < TimeSpan.FromMilliseconds(1500))
                return _lastForegroundResult;

            bool isGame = false;
            if (NativeMethods.SHQueryUserNotificationState(out int state) == 0 &&
                state is NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN or NativeMethods.QUNS_PRESENTATION_MODE)
            {
                isGame = true;
            }
            else if (hwnd != IntPtr.Zero && !ShellClasses.Contains(NativeMethods.GetWindowClass(hwnd)))
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != 0 && pid != _ownProcess)
                {
                    isGame = IsFullscreen(hwnd) || IsGameProcess(pid);
                }
            }

            _lastForegroundHwnd = hwnd;
            _lastForegroundResult = isGame;
            _lastForegroundTime = now;
            return isGame;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Окно без заголовка на весь монитор: игра «в окне без рамки», видео на весь экран. Окно с заголовком
    /// не считается — развёрнутый браузер при скрытой панели задач тоже закрывает весь монитор.
    /// </summary>
    private static bool IsFullscreen(IntPtr hwnd)
    {
        const long caption = 0x00C00000; // WS_CAPTION
        if ((NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64() & caption) == caption)
            return false;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return false;
        var info = new NativeMethods.MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info))
            return false;
        var m = info.Monitor;
        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
    }

    private bool IsGameProcess(uint pid)
    {
        var path = NativeMethods.GetProcessPath(pid);
        if (path == null)
            return false;
        var exe = Path.GetFileNameWithoutExtension(path);
        if (ExcludedExes.Contains(exe))
            return false;

        var now = DateTime.UtcNow;
        // Номер процесса мог достаться другой программе — тогда путь другой
        if (_processes.TryGetValue(pid, out var cached) && cached.Path == path &&
            (cached.Game || now - cached.Checked < RecheckDelay))
            return cached.Game;

        bool game = IsGamePath(path) || IsGameBarGame(path) || HasGameModules(pid);
        if (_processes.Count > 256)
            _processes.Clear();
        _processes[pid] = (path, game, now);
        return game;
    }

    /// <summary>Игра по пути к exe: из библиотеки Steam, Epic, GOG и других магазинов или собранная на Unreal.</summary>
    internal static bool IsGamePath(string path)
    {
        var lower = path.ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(lower);
        if (ExcludedExes.Contains(name))
            return false;
        if (NotGameFolders.Any(lower.Contains))
            return false;
        // Unreal Engine называет exe игры «Имя-Win64-Shipping.exe»
        return name.EndsWith("-win64-shipping") || name.EndsWith("-wingdk-shipping") || GameFolders.Any(lower.Contains);
    }

    internal static bool IsGameModule(string module) => GameModules.Contains(module);

    /// <summary>Xbox Game Bar сам ведёт список программ, которые он счёл играми.</summary>
    private bool IsGameBarGame(string path)
    {
        var exeName = Path.GetFileNameWithoutExtension(path);
        if (ExcludedExes.Contains(exeName))
            return false;

        if (DateTime.UtcNow - _gameBarRead > GameBarRefresh)
        {
            _gameBarRead = DateTime.UtcNow;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
                if (children != null)
                    foreach (var name in children.GetSubKeyNames())
                    {
                        using var child = children.OpenSubKey(name);
                        if (child?.GetValue("MatchedExeFullPath") is string exe && exe.Length > 0)
                            set.Add(exe);
                    }
            }
            catch (Exception) { }
            _gameBar = set;
        }
        return _gameBar.Contains(path);
    }

    private static bool HasGameModules(uint pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero)
            return false;
        try
        {
            var modules = new IntPtr[1024];
            int size = IntPtr.Size * modules.Length;
            if (!NativeMethods.EnumProcessModulesEx(handle, modules, size, out int needed, NativeMethods.LIST_MODULES_ALL))
                return false;
            int count = Math.Min(needed / IntPtr.Size, modules.Length);
            var name = new StringBuilder(260);
            for (int i = 0; i < count; i++)
            {
                name.Clear();
                if (NativeMethods.GetModuleBaseName(handle, modules[i], name, name.Capacity) > 0 && IsGameModule(name.ToString()))
                    return true;
            }
            return false;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
