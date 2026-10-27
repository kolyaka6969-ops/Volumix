using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Volumix.Views;
using static Volumix.Services.NativeMethods;

namespace Volumix.Services;

/// <summary>
/// Мышь над кнопками на обоях. Окно обоев лежит под значками рабочего стола, и щелчки достаются Explorer,
/// поэтому смотрим на мышь глобальным хуком, как Lively. Хук тормозит мышь во всей системе, пока работает,
/// поэтому живёт в своём потоке и только сверяет точку с готовыми прямоугольниками кнопок.
/// Нажатие на кнопку забираем у Explorer, иначе он начинал бы рамку выделения значков; всё остальное ему.
/// </summary>
internal sealed class DesktopPointer : IDisposable
{
    /// <summary>Кнопка на экране в физических пикселях; Owner — чьё это окно обоев.</summary>
    public readonly record struct Target(Rectangle Bounds, object Owner, DesktopControl Control);

    private volatile Target[] _targets = Array.Empty<Target>();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private LowLevelMouseProc? _proc;
    // Дальше — только в потоке хука
    private Target? _hover;
    private Target? _pressed;

    private double _fraction;

    /// <summary>
    /// Мышь навелась, ушла, зажала кнопку: цель под мышью (null — ни одной), зажата ли она и где мышь
    /// по ширине цели (0…1, для полосы прогресса). Из потока хука.
    /// </summary>
    public event Action<Target?, bool, double>? Pointer;
    /// <summary>Кнопку нажали и отпустили; для полосы — где отпустили (0…1). Из потока хука.</summary>
    public event Action<Target, double>? Click;

    public void SetTargets(Target[] targets) => _targets = targets;

    public bool Enabled
    {
        get => _thread != null;
        set
        {
            if (value == (_thread != null))
                return;
            if (value)
                Start();
            else
                Stop();
        }
    }

    private void Start()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _proc = OnMouse;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            ready.Set();
            if (_hook == IntPtr.Zero)
                return;
            // Хуку нужен цикл сообщений в его потоке
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "Volumix desktop pointer",
        };
        _thread.Start();
        ready.Wait();
    }

    private void Stop()
    {
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
        _thread = null;
        if (_hover != null || _pressed != null)
        {
            _hover = _pressed = null;
            Pointer?.Invoke(null, false, 0);
        }
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        int msg = unchecked((int)wParam.ToInt64());
        if (code >= 0 && msg is WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_LBUTTONUP)
        {
            try
            {
                int x = Marshal.ReadInt32(lParam);
                int y = Marshal.ReadInt32(lParam, 4);
                if (Handle(msg, new POINT { X = x, Y = y }))
                    return new IntPtr(1); // наше нажатие — Explorer его не видит
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Разбирает событие мыши; true — событие наше и дальше не идёт.</summary>
    private bool Handle(int msg, POINT pt)
    {
        // Полосу прогресса тянут: она остаётся целью, даже если мышь с неё ушла
        if (_pressed is { Control: DesktopControl.Seek } seek)
        {
            double at = Fraction(seek, pt);
            if (msg == WM_LBUTTONUP)
            {
                _pressed = null;
                _hover = seek.Bounds.Contains(pt.X, pt.Y) ? seek : null;
                Click?.Invoke(seek, at);
                Pointer?.Invoke(_hover, false, at);
                return true;
            }
            if (msg == WM_MOUSEMOVE && Math.Abs(at - _fraction) > 0.0005)
            {
                _fraction = at;
                Pointer?.Invoke(seek, true, at);
            }
            return false;
        }

        Target? hit = null;
        foreach (var target in _targets)
            if (target.Bounds.Contains(pt.X, pt.Y))
            {
                hit = target;
                break;
            }
        // Над кнопкой может быть окно — тогда мышь его, а не наша. Спрашиваем только над кнопками: это дороже
        if (hit != null && msg != WM_LBUTTONUP && !OverDesktop(pt))
            hit = null;

        var hover = _hover;
        bool wasPressed = _pressed != null && _pressed == hover;
        bool ours = false;
        if (msg == WM_LBUTTONDOWN)
        {
            _pressed = hit;
            ours = hit != null;
        }
        else if (msg == WM_LBUTTONUP)
        {
            // Отпускание после нашего нажатия тоже наше, где бы мышь ни была: Explorer нажатия не видел
            ours = _pressed != null;
            if (hit is { } clicked && _pressed == hit)
                Click?.Invoke(clicked, Fraction(clicked, pt));
            _pressed = null;
        }
        _hover = hit;
        bool pressed = _pressed != null && _pressed == hit;
        double fraction = hit is { } h ? Fraction(h, pt) : 0;
        if (hit != hover || pressed != wasPressed || (hit?.Control == DesktopControl.Seek && pressed))
        {
            _fraction = fraction;
            Pointer?.Invoke(hit, pressed, fraction);
        }
        return ours;
    }

    private static double Fraction(Target target, POINT pt) =>
        target.Bounds.Width <= 0 ? 0 : Math.Clamp((pt.X - target.Bounds.Left) / (double)target.Bounds.Width, 0, 1);

    /// <summary>Под точкой рабочий стол (значки, слой обоев, наше окно, Wallpaper Engine), а не чьё-то окно.</summary>
    private static bool OverDesktop(POINT pt)
    {
        var window = WindowFromPoint(pt);
        if (window == IntPtr.Zero)
            return false;
        var root = GetAncestor(window, GA_ROOT);
        var targetHwnd = root == IntPtr.Zero ? window : root;
        var name = new StringBuilder(64);
        GetClassName(targetHwnd, name, name.Capacity);
        var className = name.ToString();
        if (className is "Progman" or "WorkerW")
            return true;
        GetWindowThreadProcessId(targetHwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId)
            return true;
        return IsWallpaperProcess(pid);
    }

    public void Dispose() => Enabled = false;
}
