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
    /// и должно быть слоистым. Раньше: по сообщению 0x052C Progman создаёт WorkerW позади значков, окно встаёт в него.
    /// </summary>
    private sealed record Target(IntPtr Parent, IntPtr Icons, bool Raised);

    private const uint WM_SPAWN_WORKER = 0x052C;
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(2);

    private readonly UIElement _content;
    private readonly DispatcherTimer _watch;
    private HwndSource? _source;
    private Decorator? _frame;
    private Target? _target;
    private bool _visible;

    public DesktopLayer(UIElement content)
    {
        _content = content;
        // Explorer могут перезапустить, экран — сменить: раз в пару секунд проверяем, что окно на месте
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
        var hwnd = Handle;
        if (hwnd != IntPtr.Zero && _target is { } t && IsWindow(hwnd) && IsWindow(t.Icons) && GetAncestor(hwnd, GA_PARENT) == t.Parent)
        {
            Place(hwnd, t, onlyIfMoved: true);
            return;
        }

        Destroy();
        _target = FindTarget();
        if (_target == null)
            return; // Explorer ещё запускается — попробуем на следующей проверке

        var (x, y, width, height) = Bounds(_target);
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
        if (_target.Raised)
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

    private static System.Drawing.Rectangle Screen =>
        Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

    /// <summary>Весь основной монитор в координатах родителя (родитель охватывает все мониторы).</summary>
    private static (int X, int Y, int Width, int Height) Bounds(Target target)
    {
        var screen = Screen;
        var origin = new POINT { X = screen.Left, Y = screen.Top };
        ScreenToClient(target.Parent, ref origin);
        return (origin.X, origin.Y, screen.Width, screen.Height);
    }

    private static void Place(IntPtr hwnd, Target target, bool onlyIfMoved)
    {
        if (onlyIfMoved)
        {
            var screen = Screen;
            bool inPlace = GetWindowRect(hwnd, out var r)
                           && r.Left == screen.Left && r.Top == screen.Top && r.Right == screen.Right && r.Bottom == screen.Bottom;
            bool inOrder = target.Raised ? GetWindow(target.Icons, GW_HWNDNEXT) == hwnd : GetWindow(target.Parent, GW_CHILD) == hwnd;
            if (inPlace && inOrder)
                return;
        }
        var (x, y, width, height) = Bounds(target);
        IntPtr after = target.Raised ? target.Icons : IntPtr.Zero; // HWND_TOP внутри WorkerW
        SetWindowPos(hwnd, after, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private static Target? FindTarget()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
            return null;
        SendMessageTimeout(progman, WM_SPAWN_WORKER, new IntPtr(0xD), new IntPtr(1), SMTO_NORMAL, 1000, out _);

        if ((GetWindowLongPtr(progman, GWL_EXSTYLE).ToInt64() & WS_EX_NOREDIRECTIONBITMAP) != 0)
        {
            var icons = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            return icons == IntPtr.Zero ? null : new Target(progman, icons, Raised: true);
        }

        IntPtr defView = IntPtr.Zero, worker = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            var view = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero)
            {
                defView = view;
                worker = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            }
            return true;
        }, IntPtr.Zero);
        return worker == IntPtr.Zero ? null : new Target(worker, defView, Raised: false);
    }

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
        if (!raised)
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, null, SPIF_UPDATEINIFILE);
    }

    public void Dispose()
    {
        _watch.Stop();
        _visible = false;
        Destroy();
    }
}
