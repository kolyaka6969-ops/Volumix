using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Volumix.Services;
using Forms = System.Windows.Forms;

namespace Volumix.Views;

/// <summary>Край экрана, у которого стоит островок и в который он прячется.</summary>
public enum IslandEdge { Top, Bottom, Left, Right }

/// <summary>
/// Где стоит островок. На экране — доли свободного места слева и сверху (0,5 и 0 — вверху посередине),
/// на панели задач — доля её длины. Screen — имя монитора, «» — основной.
/// </summary>
public sealed record IslandPlacement(double X, double Y, string Screen, bool OnTaskbar, double TaskbarX)
{
    public static IslandPlacement Default { get; } = new(0.5, 0, "", false, 0.75);
}

/// <summary>
/// Островок с тем, что играет. Нажимается мышью, но фокус у активного окна не забирает.
/// Перетаскивается куда угодно и прилипает к краям и середине экрана, растягивается за края.
/// Задвинутый за край экрана прячется в него, оставляя тонкую полоску, и выезжает, когда курсор упирается в край.
/// Перетащенный на панель задач становится узкой строкой и вшивается в неё — окно островка становится дочерним
/// для панели: стоит на ней, а не поверх, и уезжает вместе со скрывающейся панелью.
/// </summary>
public partial class MediaIslandWindow : Window
{
    // Сплошные значки Segoe Fluent Icons
    private const string GlyphPlay = "\uF5B0";
    private const string GlyphPause = "\uF8AE";

    private static readonly Duration FadeTime = TimeSpan.FromMilliseconds(180);
    private static readonly Duration SlideTime = TimeSpan.FromMilliseconds(220);
    /// <summary>Сколько после прокрутки колёсика вместо исполнителя висит громкость.</summary>
    private static readonly TimeSpan VolumeTime = TimeSpan.FromMilliseconds(1500);
    /// <summary>Как часто смотрим на курсор, пока островок прячется в край: окно почти целиком за экраном и мышь не ловит.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    /// <summary>Курсор ушёл с выехавшего островка — через столько он прячется обратно.</summary>
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(700);
    /// <summary>
    /// Пока островок вшит в панель задач, присматриваем за ним: панель может перестроить свои окна поверх него.
    /// А островок, который тянут по панели, ещё не вшит и должен быть поверх неё.
    /// </summary>
    private static readonly TimeSpan TaskbarInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Сколько точек спрятанного островка видно у края экрана.</summary>
    private const double Peek = 5;
    /// <summary>Зазор между островком и краем экрана или панели задач.</summary>
    private const double Gap = 4;
    /// <summary>Отступ окна под тень.</summary>
    private const double FrameMargin = 16;
    /// <summary>Насколько задвинуть островок за край экрана, чтобы спрятать, или потянуть от края, чтобы достать.</summary>
    private const double FlickDistance = 24;
    /// <summary>С какого расстояния островок прилипает к краю или середине экрана.</summary>
    private const double SnapDistance = 14;
    /// <summary>Курсор у края может промахнуться мимо узкой полоски — ловим его с запасом по бокам.</summary>
    private const double HotZoneSlack = 40;

    public const double DefaultIslandWidth = 400;
    public const double MinIslandWidth = 300;
    public const double MaxIslandWidth = 900;
    public const double MinIslandScale = 0.7;
    public const double MaxIslandScale = 1.6;
    public const double DefaultTaskbarWidth = 320;
    public const double MinTaskbarWidth = 200;

    private enum Drag { None, Move, Left, Right, Bottom }

    private readonly DispatcherTimer _volumeTimer = new() { Interval = VolumeTime };
    private readonly DispatcherTimer _pollTimer = new() { Interval = PollInterval };
    private readonly DispatcherTimer _tuckTimer = new();
    private readonly DispatcherTimer _taskbarTimer = new() { Interval = TaskbarInterval };
    // Окно панели задач, в которое вшит островок
    private IntPtr _embeddedIn;
    private readonly System.Windows.Media.Effects.Effect _shadow;
    private IslandTrack? _track;
    private bool _shown;
    // Пользователь сам спрятал островок в край
    private bool _docked;
    // Автоскрытие: через сколько островок сам уезжает в край
    private TimeSpan? _autoHide;
    // Спрятанный островок сейчас выехал: курсор у края, сменился трек
    private bool _revealed;
    private bool _menuOpen;
    private double _x = 0.5, _y;
    private string _screen = "";
    private bool _onTaskbar;
    private double _taskbarX = IslandPlacement.Default.TaskbarX;
    private double _width = DefaultIslandWidth, _scale = 1, _taskbarWidth = DefaultTaskbarWidth;
    // Край, за который островок задвинули: в углу он касается двух краёв сразу
    private IslandEdge? _pushedEdge;
    private Drag _drag;
    // Полоса перемотки, которую сейчас тянут мышью
    private FrameworkElement? _scrub;
    private Point _dragStart;
    // Где курсор держит островок, от его левого верхнего угла
    private Vector _grab;
    private double _startWidth, _startScale, _startHeight;

    public event Action? Previous;
    public event Action? PlayPause;
    public event Action? Next;
    /// <summary>Перемотка: доля трека от 0 до 1.</summary>
    public event Action<double>? Seek;
    /// <summary>Колёсико мыши над островком: true — громче.</summary>
    public event Action<bool>? Wheel;
    /// <summary>Островок растянули или сбросили размер: ширина и масштаб на экране и ширина на панели задач.</summary>
    public event Action<double, double, double>? Resized;
    /// <summary>Островок спрятали в край или достали обратно.</summary>
    public event Action<bool>? DockedChanged;
    /// <summary>Островок передвинули.</summary>
    public event Action<IslandPlacement>? Moved;

    public MediaIslandWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeNoActivateWindow(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook(NoActivate);
        };
        SizeChanged += (_, _) => Place();
        _volumeTimer.Tick += (_, _) =>
        {
            _volumeTimer.Stop();
            SubtitleText.Text = _track?.Subtitle ?? "";
        };
        MouseWheel += (_, e) => Wheel?.Invoke(e.Delta > 0);
        Track.MouseEnter += (_, _) => TrackLine.Height = 6;
        Track.MouseLeave += (_, _) => TrackLine.Height = 4;
        // Полоса на панели задач под курсором толще — видно, что её можно нажать
        MiniSeek.MouseEnter += (_, _) => MiniTrack.Height = _track is { CanSeek: true } ? 4 : 2;
        MiniSeek.MouseLeave += (_, _) => MiniTrack.Height = 2;
        _pollTimer.Tick += (_, _) => Poll();
        _tuckTimer.Tick += (_, _) =>
        {
            _tuckTimer.Stop();
            _revealed = false;
            UpdateDock();
        };
        _taskbarTimer.Tick += (_, _) => WatchTaskbar();
        _shadow = Root.Effect;
        // Сменили тему Windows — островок на панели задач перекрашивается под неё
        Microsoft.Win32.UserPreferenceChangedEventHandler themeChanged = (_, _) => Dispatcher.BeginInvoke(ApplyMode);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += themeChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= themeChanged;
        MouseMove += (_, _) => OnDragMove();
        MouseLeftButtonUp += (_, _) => EndDrag();
        LostMouseCapture += (_, _) => EndDrag();
        ApplyMode();
    }

    /// <summary>Ширина островка на экране, без масштаба.</summary>
    public double IslandWidth
    {
        get => _width;
        set
        {
            _width = Math.Clamp(value, MinIslandWidth, MaxIslandWidth);
            ApplyMode();
        }
    }

    public double IslandScale
    {
        get => _scale;
        set
        {
            _scale = Math.Clamp(value, MinIslandScale, MaxIslandScale);
            ApplyMode();
        }
    }

    /// <summary>Ширина островка на панели задач: там он узкий, и ширина своя.</summary>
    public double TaskbarWidth
    {
        get => _taskbarWidth;
        set
        {
            _taskbarWidth = Math.Clamp(value, MinTaskbarWidth, MaxIslandWidth);
            ApplyMode();
        }
    }

    public IslandPlacement Placement => new(_x, _y, _screen, _onTaskbar, _taskbarX);

    public void SetPlacement(IslandPlacement placement)
    {
        _x = Fraction(placement.X, 0.5);
        _y = Fraction(placement.Y, 0);
        _screen = placement.Screen ?? "";
        _onTaskbar = placement.OnTaskbar;
        _taskbarX = Fraction(placement.TaskbarX, IslandPlacement.Default.TaskbarX);
        _pushedEdge = null;
        ApplyMode();
        if (IsVisible)
        {
            UpdateLayout();
            Place();
        }

        static double Fraction(double value, double fallback) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
    }

    /// <summary>Островок спрятан в край экрана и выезжает, только когда к краю подведут курсор.</summary>
    public bool Docked
    {
        get => _docked;
        set
        {
            if (_docked == value)
                return;
            _docked = value;
            _revealed = false;
            _tuckTimer.Stop();
            UpdateDock();
        }
    }

    /// <summary>Через сколько островок сам прячется в край; null — не прячется. Новый трек снова его показывает.</summary>
    public TimeSpan? AutoHide
    {
        get => _autoHide;
        set
        {
            if (_autoHide == value)
                return;
            _autoHide = value;
            if (value is { } hold && _shown && !_docked)
                Reveal(hold);
            else
            {
                _revealed = false;
                _tuckTimer.Stop();
                UpdateDock();
            }
        }
    }

    /// <summary>Островок на панели задач. Если панели нет (скрывается сама или стоит сбоку) — он на экране.</summary>
    private bool OnTaskbar => _onTaskbar && TaskbarRect() != null;
    // На панели задач островок не прячется: он там и так маленький
    private bool Tucks => (_docked || _autoHide != null) && !OnTaskbar;
    private bool IsOut => !Tucks || _revealed || _drag != Drag.None || _menuOpen;

    public bool IsShown => _shown;

    public void ShowTrack(IslandTrack track)
    {
        // Автоскрытие: новый трек, пауза или продолжение ненадолго достают островок из края
        bool changed = _track == null || _track.Title != track.Title || _track.IsPlaying != track.IsPlaying;
        _track = track;
        if (changed && _shown && !_docked && _autoHide is { } hold && Tucks)
            Reveal(hold);
        TitleText.Text = track.Title;
        TitleText.ToolTip = track.Title;
        if (!_volumeTimer.IsEnabled && _scrub == null)
            SubtitleText.Text = track.Subtitle ?? "";
        CoverBrush.ImageSource = track.Cover;
        CoverImage.Visibility = track.Cover != null ? Visibility.Visible : Visibility.Collapsed;
        CoverGlyph.Text = track.IsVideo ? "" : "";

        PlayButton.Content = track.IsPlaying ? GlyphPause : GlyphPlay;
        PlayButton.ToolTip = track.IsPlaying ? "Пауза" : "Играть";
        // Треугольник «играть» визуально левее центра — сдвигаем, как на обоях
        PlayButton.Padding = new Thickness(track.IsPlaying ? 0 : 2, 0, 0, 0);
        PreviousButton.IsEnabled = track.CanPrevious || track.CanSeek;
        NextButton.IsEnabled = track.CanNext;

        Track.Cursor = MiniSeek.Cursor = track.CanSeek ? Cursors.Hand : null;
        DurationText.Text = FormatTime(track.Duration);
        UpdateRows();
        UpdateProgress(track.PositionAt(DateTimeOffset.Now));
    }

    public void UpdateProgress(TimeSpan position)
    {
        // Пока полосу тянут мышью, на ней позиция, куда тянут, а не та, где играет
        if (_track is not { } track || track.Duration <= TimeSpan.Zero || _scrub != null)
            return;
        PositionText.Text = FormatTime(position, track.Duration);
        ProgressScale.ScaleX = MiniScale.ScaleX = Math.Clamp(position / track.Duration, 0, 1);
    }

    /// <summary>Громкость плеера вместо исполнителя, ненадолго.</summary>
    public void ShowVolume(double volume)
    {
        SubtitleText.Text = volume <= 0 ? "Без звука" : $"Громкость {volume:0} %";
        _volumeTimer.Stop();
        _volumeTimer.Start();
    }

    /// <summary>«3:07», «1:02:45». Позиция пишется в том же формате, что и длительность, чтобы не прыгала ширина.</summary>
    internal static string FormatTime(TimeSpan time, TimeSpan? duration = null)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;
        bool hours = (duration ?? time).TotalHours >= 1;
        return hours
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    public void ShowIsland()
    {
        if (_shown)
            return;
        _shown = true;
        if (!IsVisible)
            Show();
        ApplyMode();
        UpdateLayout();
        if (!_docked && _autoHide is { } hold && Tucks)
        {
            _revealed = true;
            _tuckTimer.Interval = hold;
            _tuckTimer.Start();
        }
        Place();
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeTime));
        // Выезжает со стороны своего края
        var target = SlideTarget();
        var from = OnTaskbar ? target + new Vector(0, 12) : target + Outward(NearestEdge()) * 12;
        AnimateSlide(from, target, FadeTime);
        UpdatePolling();
    }

    public void HideIsland()
    {
        if (!_shown)
            return;
        _shown = false;
        _revealed = false;
        _tuckTimer.Stop();
        _taskbarTimer.Stop();
        UpdatePolling();
        var fade = new DoubleAnimation(0, FadeTime);
        fade.Completed += (_, _) => { if (!_shown) Hide(); };
        BeginAnimation(OpacityProperty, fade);
        var current = new Vector(Slide.X, Slide.Y);
        AnimateSlide(current, current + Outward(NearestEdge()) * 12, FadeTime);
    }

    /// <summary>
    /// Щелчок по вшитому островку не делает панель задач активным окном: активная скрывающаяся панель
    /// перестаёт прятаться, пока не щёлкнешь по другому окну.
    /// </summary>
    private static IntPtr NoActivate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_MOUSEACTIVATE)
            return IntPtr.Zero;
        handled = true;
        return new IntPtr(NativeMethods.MA_NOACTIVATE);
    }

    /// <summary>
    /// Вшитый островок — над кнопками панели задач и посередине её высоты: панель перестраивает свои окна
    /// и меняет размер, и он мог оказаться под ними или съехать.
    /// </summary>
    private void WatchTaskbar()
    {
        if (_embeddedIn == IntPtr.Zero)
            KeepOnTop();
        else if (_drag == Drag.None)
            Place();
    }

    /// <summary>Островок, который тянут по панели задач, — поверх неё: вшивается он, только когда его отпустят.</summary>
    public void KeepOnTop()
    {
        if (!_shown || !OnTaskbar || _embeddedIn != IntPtr.Zero)
            return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // ---------- Вшивание в панель задач ----------

    /// <summary>Сделать окно островка дочерним для панели задач: оно встаёт на панель и двигается вместе с ней.</summary>
    private void Embed(IntPtr bar)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _embeddedIn == bar)
            return;
        Unembed();
        Topmost = false;
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE,
            new IntPtr(style & ~NativeMethods.WS_POPUP | NativeMethods.WS_CHILD));
        if (NativeMethods.SetParent(hwnd, bar) == IntPtr.Zero)
        {
            // Не вышло — остаёмся поверх панели, как раньше
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));
            Topmost = true;
            return;
        }
        _embeddedIn = bar;
        Redraw();
    }

    /// <summary>Вернуть островок обычным окном поверх всех.</summary>
    private void Unembed()
    {
        if (_embeddedIn == IntPtr.Zero)
            return;
        _embeddedIn = IntPtr.Zero;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        NativeMethods.SetParent(hwnd, IntPtr.Zero);
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE,
            new IntPtr(style & ~NativeMethods.WS_CHILD | NativeMethods.WS_POPUP));
        Topmost = true;
        Redraw();
    }

    /// <summary>
    /// Прозрачное окно, перевешенное в панель задач или обратно, теряет картинку: Windows её выбрасывает,
    /// а WPF досылает только изменившиеся куски, и островок остаётся пустым. Целиком окно перерисовывается,
    /// только когда меняется его размер, — меняем его на точку в прозрачном поле под тень.
    /// </summary>
    private void Redraw() => Host.Margin = new Thickness(0, 0, 0, Host.Margin.Bottom > 0 ? 0 : 1);

    /// <summary>Поставить вшитое окно на место: координаты дочернего окна — от угла панели, в пикселях.</summary>
    private void PlaceEmbedded(Rect window, Rect slot)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int x = (int)Math.Round((window.Left - slot.Left) * dpi.DpiScaleX);
        int y = (int)Math.Round((window.Top - slot.Top) * dpi.DpiScaleY);
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, NativeMethods.HWND_TOP, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Светлая ли панель задач: островок на ней перекрашивается под её цвет.</summary>
    private static bool LightTaskbar()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int light && light != 0;
    }

    // ---------- Вид: обычный или узкий на панели задач ----------

    private void ApplyMode()
    {
        bool compact = OnTaskbar;
        Root.Width = compact ? _taskbarWidth : _width;
        Root.Padding = compact ? new Thickness(6, 4, 8, 4) : new Thickness(10, 10, 12, 10);
        Root.CornerRadius = new CornerRadius(compact ? 8 : 20);
        // На панели задач островок как её кнопка: без тени и своего фона, только лёгкая подсветка в цвет панели
        bool light = compact && LightTaskbar();
        Root.Background = Brush(compact ? (light ? 0x0D000000 : 0x14FFFFFF) : 0xF2161618);
        Root.BorderBrush = Brush(compact ? (light ? 0x0F000000 : 0x0FFFFFFF) : 0x1FFFFFFF);
        Root.Effect = compact ? null : _shadow;
        Resources["IslandText"] = Brush(light ? 0xE6000000 : 0xFFFFFFFF);
        Resources["IslandSubtext"] = Brush(light ? 0x99000000 : 0xA6FFFFFF);
        Resources["IslandFaint"] = Brush(light ? 0x1F000000 : 0x33FFFFFF);
        Resources["IslandHover"] = Brush(light ? 0x14000000 : 0x26FFFFFF);
        Resources["IslandStrong"] = Brush(light ? 0xFF1C1C1C : 0xFFFFFFFF);
        Resources["IslandOnStrong"] = Brush(light ? 0xFFFFFFFF : 0xFF161618);
        CoverBox.Width = CoverBox.Height = compact ? 30 : 46;
        CoverBox.CornerRadius = CoverImage.CornerRadius = new CornerRadius(compact ? 7 : 10);
        CoverGlyph.FontSize = compact ? 15 : 20;
        TextPanel.Margin = compact ? new Thickness(8, 0, 6, 0) : new Thickness(12, 0, 8, 0);
        TitleText.FontSize = compact ? 12 : 14;
        SubtitleText.FontSize = compact ? 11 : 12;
        foreach (var button in new[] { PreviousButton, NextButton })
        {
            button.Width = button.Height = compact ? 26 : 32;
            button.FontSize = compact ? 12 : 14;
        }
        PlayButton.Width = PlayButton.Height = compact ? 28 : 36;
        PlayButton.FontSize = compact ? 12 : 15;
        PlayButton.Margin = new Thickness(compact ? 2 : 4, 0, compact ? 2 : 4, 0);
        BottomGrip.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (!compact)
            Zoom.ScaleX = Zoom.ScaleY = _scale;
        UpdateRows();
    }

    private static SolidColorBrush Brush(long argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    private void UpdateRows()
    {
        // У трансляций и части видеоплееров длительности нет — тогда и полосы нет
        bool timeline = _track is { Duration.Ticks: > 0 };
        bool compact = OnTaskbar;
        TimeRow.Visibility = timeline && !compact ? Visibility.Visible : Visibility.Collapsed;
        MiniSeek.Visibility = timeline && compact ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- Положение ----------

    private Forms.Screen? Monitor() =>
        Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _screen) ?? Forms.Screen.PrimaryScreen;

    private Rect ToDip(System.Drawing.Rectangle r)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(r.Left / dpi.DpiScaleX, r.Top / dpi.DpiScaleY, r.Width / dpi.DpiScaleX, r.Height / dpi.DpiScaleY);
    }

    /// <summary>Рабочая область монитора островка — экран без панели задач.</summary>
    private Rect Area() => Monitor() is { } m ? ToDip(m.WorkingArea) : SystemParameters.WorkArea;

    private Rect Bounds() => Monitor() is { } m ? ToDip(m.Bounds) : new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);

    /// <summary>Место панели задач на мониторе островка — где она стоит, когда видна.</summary>
    private Rect? TaskbarRect() => Taskbar()?.Slot;

    /// <summary>
    /// Панель задач монитора островка: её окно, место (где она стоит, когда видна) и сверху ли она. Ищем по окну панели, а не по рабочей области:
    /// у скрывающейся панели рабочая область — весь экран. Панель сбоку не в счёт: на ней островку не уместиться.
    /// </summary>
    private (Rect Slot, bool Top, IntPtr Hwnd)? Taskbar()
    {
        var bounds = Bounds();
        foreach (var hwnd in TaskbarWindows())
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var r))
                continue;
            var rect = ToDip(System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
            if (rect.Height >= rect.Width || rect.Height < 20 || rect.Width < bounds.Width * 0.5 ||
                rect.Left >= bounds.Right || rect.Right <= bounds.Left)
                continue;
            bool top = rect.Top + rect.Height / 2 < bounds.Top + bounds.Height / 2;
            var slot = new Rect(bounds.Left, top ? bounds.Top : bounds.Bottom - rect.Height, bounds.Width, rect.Height);
            return (slot, top, hwnd);
        }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<IntPtr> TaskbarWindows()
    {
        var main = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero)
            yield return main;
        var other = IntPtr.Zero;
        while ((other = NativeMethods.FindWindowEx(IntPtr.Zero, other, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return other;
    }

    private Size IslandSize() => new(Frame.ActualWidth * Zoom.ScaleX, Frame.ActualHeight * Zoom.ScaleY);

    /// <summary>Сам островок на экране, без отступа под тень и без сдвига в край.</summary>
    private Rect IslandRect()
    {
        var size = IslandSize();
        if (_onTaskbar && TaskbarRect() is { } bar)
        {
            double free = Math.Max(0, bar.Width - 2 * Gap - size.Width);
            return new Rect(bar.Left + Gap + _taskbarX * free, bar.Top + (bar.Height - size.Height) / 2, size.Width, size.Height);
        }
        var area = Area();
        double freeW = Math.Max(0, area.Width - 2 * Gap - size.Width);
        double freeH = Math.Max(0, area.Height - 2 * Gap - size.Height);
        return new Rect(area.Left + Gap + _x * freeW, area.Top + Gap + _y * freeH, size.Width, size.Height);
    }

    private void Place()
    {
        var bar = _onTaskbar ? TaskbarRect() : null;
        // На панели задач островок ужимается под её высоту
        if (bar is { } b && Frame.ActualHeight > 0)
        {
            double fit = Math.Min(1, (b.Height - 4) / Frame.ActualHeight);
            if (Math.Abs(Zoom.ScaleX - fit) > 0.001)
            {
                Zoom.ScaleX = Zoom.ScaleY = fit;
                return; // размер окна сменится, и Place вызовется снова
            }
        }
        var island = IslandRect();
        var window = new Rect(island.Left - FrameMargin, island.Top - FrameMargin, ActualWidth, ActualHeight);
        // Островок на панели задач вшит в неё и вшитым же тянется и растягивается по ней: каждое перевешивание —
        // лишняя перерисовка, а скрывающаяся панель уезжает из-под вынутого островка. Принесённый на панель
        // островок, пока его тянут, — поверх неё и вшивается, только когда его отпустят
        if (bar != null && _shown && Taskbar() is { } taskbar && (_drag == Drag.None || _embeddedIn == taskbar.Hwnd))
        {
            Embed(taskbar.Hwnd);
            if (_embeddedIn != IntPtr.Zero)
            {
                // Вшитый островок обрезает сама панель
                Host.Clip = null;
                PlaceEmbedded(window, taskbar.Slot);
                _taskbarTimer.Start();
                return;
            }
        }
        else
            Unembed();
        Left = window.Left;
        Top = window.Top;
        // Обрезаем по рабочей области — спрятанный островок не вылезает на панель задач и соседний монитор.
        // На самой панели задач — по всему экрану
        var clip = bar != null ? Bounds() : Area();
        clip.Offset(-Left, -Top);
        Host.Clip = new RectangleGeometry(clip);
        if (bar != null && _shown)
        {
            _taskbarTimer.Start();
            KeepOnTop();
        }
        else
            _taskbarTimer.Stop();
        if (_shown && _drag == Drag.None)
            UpdateDock();
    }

    /// <summary>Ближайший к островку край экрана: в него он и прячется.</summary>
    private IslandEdge NearestEdge()
    {
        var area = Area();
        var island = IslandRect();
        double top = island.Top - area.Top, bottom = area.Bottom - island.Bottom;
        double left = island.Left - area.Left, right = area.Right - island.Right;
        if (_pushedEdge is { } pushed && Distance(pushed) <= Gap + 0.5)
            return pushed;
        double min = new[] { top, bottom, left, right }.Min();
        return min == top ? IslandEdge.Top : min == bottom ? IslandEdge.Bottom : min == left ? IslandEdge.Left : IslandEdge.Right;

        double Distance(IslandEdge edge) => edge switch
        {
            IslandEdge.Top => top,
            IslandEdge.Bottom => bottom,
            IslandEdge.Left => left,
            _ => right,
        };
    }

    private static Vector Outward(IslandEdge edge) => edge switch
    {
        IslandEdge.Top => new Vector(0, -1),
        IslandEdge.Bottom => new Vector(0, 1),
        IslandEdge.Left => new Vector(-1, 0),
        _ => new Vector(1, 0),
    };

    /// <summary>Прижать островок к краю, в который он прячется.</summary>
    private void SnapToEdge(IslandEdge edge)
    {
        switch (edge)
        {
            case IslandEdge.Top: _y = 0; break;
            case IslandEdge.Bottom: _y = 1; break;
            case IslandEdge.Left: _x = 0; break;
            case IslandEdge.Right: _x = 1; break;
        }
    }

    /// <summary>С этой стороны панель задач: задвинутый туда островок встаёт на неё, а не прячется.</summary>
    private bool IsTaskbarSide(IslandEdge edge)
    {
        // Скрывающаяся панель задач рабочую область не уменьшает, поэтому смотрим, где стоит сама панель
        return Taskbar() is { } bar && edge == (bar.Top ? IslandEdge.Top : IslandEdge.Bottom);
    }

    // ---------- Край экрана ----------

    /// <summary>Сдвиг островка: на виду — никакого, иначе за край так, чтобы осталась полоска в <see cref="Peek"/> точек.</summary>
    private Vector SlideTarget()
    {
        // Вшитый в скрывающуюся панель задач островок уезжает вместе с ней сам
        if (OnTaskbar || IsOut)
            return new Vector();
        var edge = NearestEdge();
        var size = IslandSize();
        double depth = edge is IslandEdge.Top or IslandEdge.Bottom ? size.Height : size.Width;
        return Outward(edge) * (Gap + depth - Peek);
    }

    private void AnimateSlide(Vector from, Vector to, Duration time)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from.X, to.X, time) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from.Y, to.Y, time) { EasingFunction = ease });
    }

    private void UpdateDock()
    {
        // Прятаться островок может только вплотную к краю — прижимаем, если он стоял поодаль
        if (!IsOut && _shown)
        {
            double x = _x, y = _y;
            SnapToEdge(NearestEdge());
            if (x != _x || y != _y)
            {
                Place();
                Moved?.Invoke(Placement);
                return;
            }
        }
        AnimateSlide(new Vector(Slide.X, Slide.Y), SlideTarget(), SlideTime);
        UpdatePolling();
    }

    private void UpdatePolling()
    {
        if (_shown && Tucks)
            _pollTimer.Start();
        else
            _pollTimer.Stop();
    }

    /// <summary>Достать островок из края; hold — через сколько спрятать, даже если курсор так и не подошёл.</summary>
    private void Reveal(TimeSpan? hold)
    {
        _revealed = true;
        _tuckTimer.Stop();
        if (hold is { } delay)
        {
            _tuckTimer.Interval = delay;
            _tuckTimer.Start();
        }
        UpdateDock();
    }

    private void Poll()
    {
        if (!_shown || !Tucks || _drag != Drag.None || _menuOpen || CursorInWindow() is not { } cursor)
            return;
        if (!_revealed)
        {
            if (AtEdge(cursor))
                Reveal(null);
            return;
        }
        var island = Root.TransformToAncestor(this).TransformBounds(new Rect(Root.RenderSize));
        island.Inflate(10, 10);
        if (island.Contains(cursor))
            _tuckTimer.Stop();
        else if (!_tuckTimer.IsEnabled)
        {
            _tuckTimer.Interval = LeaveDelay;
            _tuckTimer.Start();
        }
    }

    /// <summary>Курсор упёрся в край экрана напротив спрятанного островка. Координаты — окна.</summary>
    private bool AtEdge(Point cursor)
    {
        var area = Area();
        area.Offset(-Left, -Top);
        var size = IslandSize();
        // Островок без сдвига: окно начинается с отступа под тень
        var island = new Rect(FrameMargin, FrameMargin, size.Width, size.Height);
        const double reach = Peek + 3;
        bool alongX = cursor.X >= island.Left - HotZoneSlack && cursor.X <= island.Right + HotZoneSlack;
        bool alongY = cursor.Y >= island.Top - HotZoneSlack && cursor.Y <= island.Bottom + HotZoneSlack;
        return NearestEdge() switch
        {
            IslandEdge.Top => alongX && cursor.Y <= area.Top + reach,
            IslandEdge.Bottom => alongX && cursor.Y >= area.Bottom - reach,
            IslandEdge.Left => alongY && cursor.X <= area.Left + reach,
            _ => alongY && cursor.X >= area.Right - reach,
        };
    }

    private Point? CursorInWindow()
    {
        try
        {
            return NativeMethods.GetCursorPos(out var p) ? PointFromScreen(new Point(p.X, p.Y)) : null;
        }
        catch (InvalidOperationException)
        {
            return null; // окно ещё не показано
        }
    }

    /// <summary>Курсор в точках экрана, а не окна: окно меняет размер и сдвигается, пока его тянут.</summary>
    private Point CursorOnScreen()
    {
        NativeMethods.GetCursorPos(out var p);
        var point = new Point(p.X, p.Y);
        return PresentationSource.FromVisual(this)?.CompositionTarget is { } target
            ? target.TransformFromDevice.Transform(point)
            : point;
    }

    /// <summary>Монитор под курсором — туда островок и перетаскивают.</summary>
    private static string ScreenUnderCursor()
    {
        NativeMethods.GetCursorPos(out var p);
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
        return screen.Primary ? "" : screen.DeviceName;
    }

    // ---------- Перетаскивание и растягивание ----------

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginDrag(Drag.Move, e);

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        BeginDrag(sender == LeftGrip ? Drag.Left : sender == RightGrip ? Drag.Right : Drag.Bottom, e);

    private void BeginDrag(Drag drag, MouseButtonEventArgs e)
    {
        _drag = drag;
        _dragStart = CursorOnScreen();
        _grab = _dragStart - IslandRect().TopLeft;
        _startWidth = OnTaskbar ? _taskbarWidth : _width;
        _startScale = Zoom.ScaleX;
        _startHeight = Frame.ActualHeight * Zoom.ScaleY;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove()
    {
        if (_drag == Drag.None)
            return;
        var cursor = CursorOnScreen();
        var delta = cursor - _dragStart;
        // Ширина растёт так, чтобы край шёл за курсором: у островка посередине — вдвое быстрее
        double anchor = OnTaskbar ? _taskbarX : _x;
        double grow = _drag switch
        {
            Drag.Left => -delta.X / Math.Max(anchor, 0.5) / _startScale,
            Drag.Right => delta.X / Math.Max(1 - anchor, 0.5) / _startScale,
            _ => 0,
        };
        switch (_drag)
        {
            case Drag.Left or Drag.Right when OnTaskbar:
                TaskbarWidth = _startWidth + grow;
                break;
            case Drag.Left or Drag.Right:
                IslandWidth = _startWidth + grow;
                break;
            case Drag.Bottom:
                IslandScale = _startScale * (_startHeight + delta.Y) / _startHeight;
                break;
            case Drag.Move:
                MoveTo(cursor, delta);
                break;
        }
    }

    /// <summary>
    /// Островок тянут мышью: прилипает к краям и середине, задвинутый за край экрана прячется в него,
    /// а отпущенный над панелью задач встаёт на неё.
    /// </summary>
    private void MoveTo(Point cursor, Vector delta)
    {
        // Спрятанный островок, который потянули от его края, больше не прячется
        if (_docked && !OnTaskbar && Vector.Multiply(delta, Outward(NearestEdge())) <= -FlickDistance)
            SetDocked(false);

        _screen = ScreenUnderCursor();
        if (TaskbarRect() is { } bar && bar.Contains(cursor))
        {
            var size = IslandSize();
            if (!_onTaskbar)
            {
                _onTaskbar = true;
                ApplyMode();
                UpdateLayout();
                size = IslandSize();
                // Принесённый на панель островок курсор держит за середину: размер-то сменился.
                // А тот, что тянут по панели, — там же, где за него взялись, иначе он прыгнет под курсором
                _grab = new Vector(size.Width / 2, size.Height / 2);
            }
            double free = Math.Max(1, bar.Width - 2 * Gap - size.Width);
            _taskbarX = Snap(cursor.X - _grab.X - bar.Left - Gap, free) / free;
            Place();
            return;
        }
        if (_onTaskbar)
        {
            _onTaskbar = false;
            ApplyMode();
            UpdateLayout();
            var size = IslandSize();
            _grab = new Vector(size.Width / 2, Math.Min(size.Height / 2, 30));
        }

        var area = Area();
        var island = IslandSize();
        double freeW = Math.Max(1, area.Width - 2 * Gap - island.Width);
        double freeH = Math.Max(1, area.Height - 2 * Gap - island.Height);
        var wanted = cursor - _grab;
        double left = wanted.X - area.Left - Gap, top = wanted.Y - area.Top - Gap;

        // Задвинули за край экрана — прячем туда. Со стороны панели задач островок на неё встаёт, а не прячется
        if (!_docked)
        {
            IslandEdge? pushed = top < -FlickDistance ? IslandEdge.Top
                : top > freeH + FlickDistance ? IslandEdge.Bottom
                : left < -FlickDistance ? IslandEdge.Left
                : left > freeW + FlickDistance ? IslandEdge.Right
                : null;
            if (pushed is { } edge && !IsTaskbarSide(edge))
            {
                _x = Math.Clamp(left / freeW, 0, 1);
                _y = Math.Clamp(top / freeH, 0, 1);
                SnapToEdge(edge);
                _pushedEdge = edge;
                EndDrag();
                if (_autoHide != null)
                {
                    // При автоскрытии островок и так живёт в краю: просто прячем его сейчас
                    _revealed = false;
                    _tuckTimer.Stop();
                    UpdateDock();
                }
                else
                    SetDocked(true);
                return;
            }
        }

        _x = Snap(left, freeW) / freeW;
        _y = Snap(top, freeH) / freeH;
        _pushedEdge = null;
        Place();
    }

    /// <summary>Прилипание к краям и середине: «почти у края» становится «у края».</summary>
    internal static double Snap(double offset, double free)
    {
        offset = Math.Clamp(offset, 0, free);
        if (offset < SnapDistance)
            return 0;
        if (free - offset < SnapDistance)
            return free;
        return Math.Abs(offset - free / 2) < SnapDistance ? free / 2 : offset;
    }

    private void EndDrag()
    {
        if (_drag == Drag.None)
            return;
        var drag = _drag;
        _drag = Drag.None;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        if (drag is Drag.Left or Drag.Right or Drag.Bottom)
            Resized?.Invoke(_width, _scale, _taskbarWidth);
        else
            Moved?.Invoke(Placement);
        // Отпущенный на панели задач островок вшивается в неё
        Place();
        UpdateDock();
    }

    private void SetDocked(bool docked)
    {
        Docked = docked;
        DockedChanged?.Invoke(docked);
    }

    /// <summary>Сменить место из меню и запомнить его.</summary>
    private void MoveFromMenu(IslandPlacement placement)
    {
        SetPlacement(placement);
        Moved?.Invoke(Placement);
    }

    private void Root_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = Root };
        bool onTaskbar = OnTaskbar;
        var taskbar = new MenuItem
        {
            Header = onTaskbar ? "Убрать с панели задач" : "Закрепить на панели задач",
            IsEnabled = onTaskbar || TaskbarRect() != null,
        };
        taskbar.Click += (_, _) => MoveFromMenu(Placement with { OnTaskbar = !onTaskbar });
        var dock = new MenuItem
        {
            Header = _docked ? "Не прятать в край" : "Спрятать в край экрана",
            IsEnabled = !onTaskbar,
        };
        dock.Click += (_, _) => SetDocked(!_docked);
        var home = new MenuItem
        {
            Header = "Вернуть наверх посередине",
            IsEnabled = onTaskbar || _x != 0.5 || _y != 0 || _screen != "",
        };
        home.Click += (_, _) => MoveFromMenu(IslandPlacement.Default with { TaskbarX = _taskbarX });
        var reset = new MenuItem
        {
            Header = "Обычный размер",
            IsEnabled = onTaskbar ? _taskbarWidth != DefaultTaskbarWidth : _width != DefaultIslandWidth || _scale != 1,
        };
        reset.Click += (_, _) =>
        {
            if (onTaskbar)
                TaskbarWidth = DefaultTaskbarWidth;
            else
            {
                _width = DefaultIslandWidth;
                _scale = 1;
                ApplyMode();
            }
            Resized?.Invoke(_width, _scale, _taskbarWidth);
        };
        menu.Items.Add(taskbar);
        menu.Items.Add(dock);
        menu.Items.Add(new Separator());
        menu.Items.Add(home);
        menu.Items.Add(reset);
        // Окно не активируется, и без этого меню не закрывалось бы по щелчку мимо
        menu.Opened += (_, _) =>
        {
            _menuOpen = true;
            if (PresentationSource.FromVisual(menu) is HwndSource src)
                NativeMethods.SetForegroundWindow(src.Handle);
        };
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            UpdateLayout();
            UpdateDock();
        };
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Previous?.Invoke();
    private void PlayPause_Click(object sender, RoutedEventArgs e) => PlayPause?.Invoke();
    private void Next_Click(object sender, RoutedEventArgs e) => Next?.Invoke();

    // ---------- Перемотка ----------

    /// <summary>
    /// Полосу перемотки нажимают или тянут мышью: пока тянут, островок показывает новую позицию,
    /// а плееру она уходит, только когда отпустят — иначе он перематывал бы на каждом движении.
    /// </summary>
    private void Seek_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var bar = (FrameworkElement)sender;
        if (_track is not { CanSeek: true } || bar.ActualWidth <= 0)
            return;
        _scrub = bar;
        bar.CaptureMouse();
        ShowScrub(ScrubFraction(e));
        e.Handled = true;
    }

    private void Seek_MouseMove(object sender, MouseEventArgs e)
    {
        if (_scrub == sender)
            ShowScrub(ScrubFraction(e));
    }

    private void Seek_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_scrub != sender)
            return;
        double fraction = ScrubFraction(e);
        EndScrub();
        Seek?.Invoke(fraction);
        e.Handled = true;
    }

    /// <summary>Перемотку прервали (Esc, другое окно забрало мышь) — возвращаем полосу на место трека.</summary>
    private void Seek_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_scrub != sender)
            return;
        EndScrub();
        if (_track != null)
            UpdateProgress(_track.PositionAt(DateTimeOffset.Now));
    }

    private double ScrubFraction(MouseEventArgs e) =>
        _scrub is { ActualWidth: > 0 } bar ? Math.Clamp(e.GetPosition(bar).X / bar.ActualWidth, 0, 1) : 0;

    private void ShowScrub(double fraction)
    {
        if (_track is not { } track)
            return;
        var position = track.Duration * fraction;
        ProgressScale.ScaleX = MiniScale.ScaleX = fraction;
        PositionText.Text = FormatTime(position, track.Duration);
        // На панели задач строки с временем нет — время на месте исполнителя
        if (OnTaskbar)
            SubtitleText.Text = $"{FormatTime(position, track.Duration)} / {FormatTime(track.Duration)}";
    }

    private void EndScrub()
    {
        var bar = _scrub;
        _scrub = null;
        if (bar?.IsMouseCaptured == true)
            bar.ReleaseMouseCapture();
        if (!_volumeTimer.IsEnabled)
            SubtitleText.Text = _track?.Subtitle ?? "";
    }
}
