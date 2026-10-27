using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static Volumix.Services.NativeMethods;
using Forms = System.Windows.Forms;

namespace Volumix.Services;

/// <summary>
/// Окно в слое обоев: поверх картинки рабочего стола, но под значками — так же устроены Lively и Wallpaper Engine.
/// Окно дочернее для Explorer, поэтому создаётся и живёт только в потоке, где создан этот объект.
/// </summary>
internal sealed class DesktopLayer : IDisposable
{
    /// <summary>
    /// Куда вставлять окно. Windows 11 24H2+: значки (SHELLDLL_DefView) лежат прямо в Progman, окно встаёт сразу под ними
    /// (Raised). Раньше: по сообщению 0x052C Progman создаёт WorkerW позади значков, окно встаёт в него поверх всего,
    /// что там уже есть — в том числе поверх Wallpaper Engine и Lively. У родителя без своей поверхности
    /// (WS_EX_NOREDIRECTIONBITMAP) окно должно быть слоистым (Layered), иначе его не видно.
    /// </summary>
    private sealed record Target(IntPtr Parent, IntPtr Icons, bool Raised, bool Layered);

    private const uint WM_SPAWN_WORKER = 0x052C;
    /// <summary>
    /// Wallpaper Engine и Lively тоже встают сразу под значки и после смены обоев оказываются поверх нас —
    /// проверяем почаще, чтобы вернуться наверх почти сразу. Проверка — пара вызовов GetWindow.
    /// </summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(1);

    private readonly UIElement _content;
    // Монитор по имени устройства (\\.\DISPLAY2); null — основной
    private readonly string? _device;
    private readonly DispatcherTimer _watch;
    private HwndSource? _source;
    private Decorator? _frame;
    private Target? _target;
    private bool _visible;

    public DesktopLayer(UIElement content, string? device = null)
    {
        _content = content;
        _device = device;
        // Explorer могут перезапустить, экран — сменить, другая программа обоев — перекрыть: раз в секунду
        // проверяем, что окно на месте
        _watch = new DispatcherTimer { Interval = WatchInterval };
        _watch.Tick += (_, _) => Ensure();
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value)
                return;
            _visible = value;
            if (value)
            {
                Ensure();
                _watch.Start();
            }
            else
            {
                _watch.Stop();
                Destroy();
            }
        }
    }

    private IntPtr Handle => _source is { IsDisposed: false } s ? s.Handle : IntPtr.Zero;

    private void Ensure()
    {
        var target = FindTarget();
        if (target == null)
            return; // Explorer ещё запускается — попробуем на следующей проверке

        var hwnd = Handle;
        if (hwnd != IntPtr.Zero && _target is { } t &&
            t.Parent == target.Parent &&
            t.Icons == target.Icons &&
            t.Raised == target.Raised &&
            IsWindow(hwnd) &&
            (t.Icons == IntPtr.Zero || IsWindow(t.Icons)) &&
            GetAncestor(hwnd, GA_PARENT) == t.Parent)
        {
            Place(hwnd, t, onlyIfMoved: true);
            return;
        }

        Destroy();
        _target = target;

        var (x, y, width, height) = Bounds(_target, _device);
        var p = new HwndSourceParameters("Volumix desktop", width, height)
        {
            ParentWindow = _target.Parent,
            PositionX = x,
            PositionY = y,
            // Без WS_VISIBLE: новое дочернее окно встаёт поверх значков, показываем его уже под ними
            WindowStyle = WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            ExtendedWindowStyle = WS_EX_NOACTIVATE,
        };
        try
        {
            // Промежуточная рамка: если Explorer сам уничтожит окно, содержимое можно отцепить и вставить в новое
            _frame = new Decorator { Child = _content };
            _source = new HwndSource(p) { RootVisual = _frame };
        }
        catch (Exception)
        {
            Destroy(); // окно Explorer пропало между поиском и созданием
            return;
        }
        _source.CompositionTarget.BackgroundColor = Colors.Black;
        hwnd = _source.Handle;
        if (_target.Layered)
        {
            // Progman здесь без своей поверхности (рисуется через DirectComposition), и обычное дочернее окно
            // в нём не видно — нужно слоистое со своей
            _source.AddHook(KeepLayered);
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_LAYERED;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        }
        Place(hwnd, _target, onlyIfMoved: false);
    }

    /// <summary>WPF снимает WS_EX_LAYERED со всех окон без попиксельной прозрачности — не даём.</summary>
    private static IntPtr KeepLayered(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_STYLECHANGING && unchecked((int)wParam.ToInt64()) == GWL_EXSTYLE)
        {
            var style = Marshal.PtrToStructure<STYLESTRUCT>(lParam);
            style.StyleNew |= WS_EX_LAYERED;
            Marshal.StructureToPtr(style, lParam, false);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>Границы монитора; если его отключили — основного.</summary>
    internal static System.Drawing.Rectangle ScreenBounds(string? device)
    {
        if (device != null)
            foreach (var screen in Forms.Screen.AllScreens)
                if (screen.DeviceName == device)
                    return screen.Bounds;
        return Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
    }

    /// <summary>Весь монитор в координатах родителя (родитель охватывает все мониторы).</summary>
    private static (int X, int Y, int Width, int Height) Bounds(Target target, string? device)
    {
        var screen = ScreenBounds(device);
        var origin = new POINT { X = screen.Left, Y = screen.Top };
        ScreenToClient(target.Parent, ref origin);
        return (origin.X, origin.Y, screen.Width, screen.Height);
    }

    private void Place(IntPtr hwnd, Target target, bool onlyIfMoved)
    {
        if (onlyIfMoved)
        {
            var screen = ScreenBounds(_device);
            bool inPlace = GetWindowRect(hwnd, out var r)
                           && r.Left == screen.Left && r.Top == screen.Top && r.Right == screen.Right && r.Bottom == screen.Bottom;
            if (inPlace && InOrder(hwnd, target))
                return;
        }
        var (x, y, width, height) = Bounds(target, _device);
        IntPtr after = target.Raised ? target.Icons : IntPtr.Zero; // HWND_TOP внутри WorkerW
        SetWindowPos(hwnd, after, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Окно стоит выше всего остального в слое обоев. Окна Volumix на других мониторах могут стоять
    /// между нами и значками: они друг друга не перекрывают, и наверх их не тащим — иначе окна
    /// толкались бы между собой каждую секунду.
    /// </summary>
    private static bool InOrder(IntPtr hwnd, Target target)
    {
        var w = target.Raised ? GetWindow(target.Icons, GW_HWNDNEXT) : GetWindow(target.Parent, GW_CHILD);
        while (w != IntPtr.Zero && w != hwnd && IsOwnWindow(w))
            w = GetWindow(w, GW_HWNDNEXT);
        return w == hwnd;
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    /// <summary>
    /// Ищет окно Wallpaper Engine или Lively, если они запущены.
    /// </summary>
    internal static IntPtr FindWallpaperWindow()
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            GetWindowThreadProcessId(top, out uint pid);
            if (IsWallpaperProcess(pid))
            {
                result = top;
                return false;
            }

            var cls = GetWindowClass(top);
            if (cls is "Progman" or "WorkerW")
            {
                EnumChildWindows(top, (child, _) =>
                {
                    GetWindowThreadProcessId(child, out uint cpid);
                    if (IsWallpaperProcess(cpid))
                    {
                        result = child;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (result != IntPtr.Zero)
                    return false;
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// Находит родительское окно в слое рабочего стола (WorkerW или Progman), в которое встроены обои.
    /// </summary>
    private static IntPtr GetWallpaperHost(IntPtr weWnd)
    {
        IntPtr host = weWnd;
        while (true)
        {
            var parent = GetAncestor(host, GA_PARENT);
            if (parent == IntPtr.Zero)
                break;
            host = parent;
            var pcls = GetWindowClass(host);
            if (pcls is "Progman" or "WorkerW")
                break;
        }
        return host;
    }

    private static Target? FindTarget()
    {
        var we = FindWallpaperWindow();
        if (we != IntPtr.Zero)
        {
            var host = GetWallpaperHost(we);
            var icons = FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (icons != IntPtr.Zero)
                return new Target(host, icons, Raised: true, Layered: NoSurface(host));

            // Значки не в host (например, значки в Progman, а обои в WorkerW)
            // Встаём в host поверх всего (HWND_TOP), позади значков
            return new Target(host, Icons: IntPtr.Zero, Raised: false, Layered: NoSurface(host));
        }

        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            return null;
        SendMessageTimeout(progman, WM_SPAWN_WORKER, new IntPtr(0xD), new IntPtr(1), SMTO_NORMAL, 1000, out _);

        if (NoSurface(progman))
        {
            var icons = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (icons != IntPtr.Zero)
                return new Target(progman, icons, Raised: true, Layered: true);
            // Значки не в Progman: их перенесла другая программа обоев — ищем, где они теперь
        }

        IntPtr defView = IntPtr.Zero, fallbackHost = IntPtr.Zero, worker = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            var view = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero)
            {
                defView = view;
                fallbackHost = top;
                worker = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            }
            return true;
        }, IntPtr.Zero);
        if (worker != IntPtr.Zero)
            return new Target(worker, defView, Raised: false, Layered: NoSurface(worker));
        // Слоя позади значков нет — встаём под значки в том же окне, что и они
        return defView == IntPtr.Zero ? null : new Target(fallbackHost, defView, Raised: true, Layered: true);
    }

    private static bool NoSurface(IntPtr hwnd) =>
        (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_NOREDIRECTIONBITMAP) != 0;

    private void Destroy()
    {
        if (_frame != null)
        {
            _frame.Child = null;
            _frame = null;
        }
        if (_source == null)
            return;
        bool raised = _target?.Raised ?? true;
        if (!_source.IsDisposed)
            _source.Dispose();
        _source = null;
        _target = null;
        // В старой схеме WorkerW сам не перерисовывается и оставил бы последний кадр. Как в Lively:
        // null заставляет Windows заново применить текущие обои.
        if (!raised && FindWallpaperWindow() == IntPtr.Zero)
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, null, SPIF_UPDATEINIFILE);
    }

    public void Dispose()
    {
        _watch.Stop();
        _visible = false;
        Destroy();
    }
}
