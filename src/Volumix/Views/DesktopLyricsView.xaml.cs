using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Volumix.Services;

namespace Volumix.Views;

/// <summary>Кнопки плеера на обоях.</summary>
public enum DesktopControl { Previous, PlayPause, Next, Seek }

/// <summary>Песня и её текст во весь экран — то, что показывается на рабочем столе вместо обоев.</summary>
public partial class DesktopLyricsView : UserControl
{
    /// <summary>Где по высоте стоит текущая строка (доля высоты области текста).</summary>
    private const double Anchor = 0.38;
    private const double CurrentOpacity = 1, NearOpacity = 0.42, FarOpacity = 0.22, PlainOpacity = 0.7;
    /// <summary>Строку подсвечиваем чуть раньше: пока она доедет до места, певец как раз начнёт.</summary>
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MinTick = TimeSpan.FromMilliseconds(30);
    private static readonly Duration ScrollTime = TimeSpan.FromMilliseconds(550);
    private static readonly Duration FadeTime = TimeSpan.FromMilliseconds(350);
    private const double CoverSize = 440;
    /// <summary>Место под крупную обложку клипа в макете 1920×1080.</summary>
    private const double BigCoverWidth = 790, BigCoverHeight = 760;

    private readonly DispatcherTimer _timer = new();
    private readonly List<TextBlock> _lines = new();
    private NowPlaying? _track;
    private BitmapSource? _cover;
    private Lyrics _lyrics = Lyrics.Loading;
    private int _current = int.MinValue;
    private bool _jump;
    private readonly Marquee _titleMarquee;
    private readonly Marquee _artistMarquee;
    // Доля трека под мышью, пока тянут полосу прогресса; null — полоса показывает настоящую позицию
    private double? _scrub;
    // Полосу отпустили: до ответа плеера держим её там, где отпустили, иначе она на миг отскочила бы назад
    private DateTime _scrubHold;

    public DesktopLyricsView()
    {
        InitializeComponent();
        _titleMarquee = new Marquee(TitleText, TitleShift);
        _artistMarquee = new Marquee(ArtistText, ArtistShift);
        _timer.Tick += (_, _) => Tick();
        LinesCanvas.SizeChanged += (_, _) =>
        {
            LinesMask.EndPoint = new Point(0, LinesCanvas.ActualHeight);
            _jump = true;
            _current = int.MinValue;
            Tick();
        };
    }

    /// <summary>Кнопки под полосой прогресса.</summary>
    public bool ControlsVisible
    {
        get => Controls.Visibility == Visibility.Visible;
        set
        {
            Controls.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value)
                SetPointer(null, false, 0);
        }
    }

    /// <summary>
    /// Кнопки в физических пикселях экрана, как их видит хук мыши. Окно лежит под значками рабочего стола
    /// и щелчков само не получает — их ловит DesktopPointer.
    /// </summary>
    internal List<(System.Drawing.Rectangle Bounds, DesktopControl Control)> ControlBounds()
    {
        var list = new List<(System.Drawing.Rectangle, DesktopControl)>();
        if (!ControlsVisible || !IsVisible || PresentationSource.FromVisual(this) == null)
            return list;
        try
        {
            list.Add((ScreenBounds(ProgressArea), DesktopControl.Seek));
            list.Add((ScreenBounds(PreviousButton), DesktopControl.Previous));
            list.Add((ScreenBounds(PlayButton), DesktopControl.PlayPause));
            list.Add((ScreenBounds(NextButton), DesktopControl.Next));
        }
        catch (InvalidOperationException)
        {
            list.Clear(); // окно как раз пересоздаётся
        }
        return list;
    }

    private static System.Drawing.Rectangle ScreenBounds(FrameworkElement element)
    {
        var a = element.PointToScreen(new Point(0, 0));
        var b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return System.Drawing.Rectangle.FromLTRB((int)Math.Round(Math.Min(a.X, b.X)), (int)Math.Round(Math.Min(a.Y, b.Y)),
            (int)Math.Round(Math.Max(a.X, b.X)), (int)Math.Round(Math.Max(a.Y, b.Y)));
    }

    /// <summary>
    /// Подсветка кнопки под мышью; pressed — кнопка зажата. Над полосой прогресса виден кружок,
    /// а пока её тянут, полоса и время показывают место под мышью (fraction — доля трека).
    /// </summary>
    internal void SetPointer(DesktopControl? control, bool pressed, double fraction)
    {
        ProgressKnob.Visibility = control == DesktopControl.Seek ? Visibility.Visible : Visibility.Collapsed;
        double? scrub = control == DesktopControl.Seek && pressed ? Math.Clamp(fraction, 0, 1) : null;
        if (scrub != _scrub)
        {
            _scrub = scrub;
            if (scrub is { } f && _track is { } track)
            {
                ShowProgress(f);
                ElapsedText.Text = Format(track.Duration * f);
            }
            else
            {
                // Отпустили. Новую позицию пришлёт ShowTrack, а если перемотка не вышла — через секунду вернёмся к настоящей
                _scrubHold = DateTime.UtcNow.AddSeconds(1);
                Tick();
            }
        }
        else if (_track != null)
            ShowProgress(ProgressScale.ScaleX); // кружок появился — ставим его на конец полосы

        HighlightButton(PreviousButton, control == DesktopControl.Previous, pressed, dim: true);
        HighlightButton(PlayButton, control == DesktopControl.PlayPause, pressed, dim: false);
        HighlightButton(NextButton, control == DesktopControl.Next, pressed, dim: true);
    }

    private static void HighlightButton(FrameworkElement button, bool hover, bool pressed, bool dim)
    {
        double scale = hover ? (pressed ? 0.94 : 1.06) : 1;
        if (button.RenderTransform is ScaleTransform t)
            t.ScaleX = t.ScaleY = scale;
        if (dim)
            button.Opacity = hover ? 1 : 0.75;
    }

    /// <summary>Скорость бегущей строки названия и исполнителя, точек макета 1920×1080 в секунду.</summary>
    public double MarqueeSpeed
    {
        get => _titleMarquee.Speed;
        set
        {
            _titleMarquee.Speed = value;
            _artistMarquee.Speed = value;
        }
    }

    /// <summary>Кадров в секунду у бегущей строки.</summary>
    public int MarqueeFps
    {
        get => _titleMarquee.Fps;
        set
        {
            _titleMarquee.Fps = value;
            _artistMarquee.Fps = value;
        }
    }

    public void ShowTrack(NowPlaying track)
    {
        var old = _track;
        _track = track;
        _scrubHold = default;
        if (old?.Key != track.Key)
        {
            _scrub = null; // тянули полосу прошлого трека
            TitleText.Text = track.ShownTitle ?? track.Title;
            ArtistText.Text = track.ShownArtist ?? track.Artist ?? "";
            AlbumText.Text = track.Album != null && track.Album != track.Title ? track.Album : "";
            ShowLyrics(track.Key, Lyrics.Loading);
            Page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)));
            _titleMarquee.Restart();
            _artistMarquee.Restart();
        }
        _titleMarquee.Running = true;
        _artistMarquee.Running = true;
        if (old == null || !ReferenceEquals(old.Cover, track.Cover))
            SetCover(track.Cover);
        PlayGlyph.Text = track.IsPlaying ? "" : "";
        // Треугольник «играть» визуально левее середины, у паузы сдвиг не нужен
        PlayGlyph.Margin = new Thickness(track.IsPlaying ? 0 : 3, 0, 0, 0);
        // Длительности нет у заглушки и у прямых эфиров — время тогда не показываем
        DurationText.Text = track.Duration > TimeSpan.Zero ? Format(track.Duration) : "";
        Tick();
    }

    /// <summary>
    /// Заглушка между треками: следующий ролик YouTube ещё проверяется, а мигать рабочим столом не хочется.
    /// Без обложки и длительности: «Загрузка…», а справа «Ищу текст песни…».
    /// </summary>
    public void ShowPlaceholder() =>
        ShowTrack(new NowPlaying("Загрузка…", null, null, null, true, TimeSpan.Zero, DateTimeOffset.Now, TimeSpan.Zero));

    public void ShowLyrics(string trackKey, Lyrics lyrics)
    {
        if (_track is not { } track || track.Key != trackKey)
            return;
        _lyrics = lyrics;
        Lines.Children.Clear();
        _lines.Clear();
        if (lyrics.Status is LyricsStatus.Synced or LyricsStatus.Plain)
            foreach (var line in lyrics.Lines)
            {
                var block = new TextBlock
                {
                    // Пустая строка в синхронном тексте — проигрыш
                    Text = line.Text.Length > 0 ? line.Text : "♪",
                    FontSize = 42,
                    FontWeight = FontWeights.SemiBold,
                    LineHeight = 54,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 26),
                    Opacity = lyrics.Status == LyricsStatus.Plain ? PlainOpacity : FarOpacity,
                };
                _lines.Add(block);
                Lines.Children.Add(block);
            }

        Badge.Visibility = lyrics.Status == LyricsStatus.Plain ? Visibility.Visible : Visibility.Collapsed;
        (StatusText.Text, StatusHint.Text) = lyrics.Status switch
        {
            LyricsStatus.Loading => ("Ищу текст песни…", ""),
            LyricsStatus.Instrumental => ("Инструментальная композиция", ""),
            LyricsStatus.NotFound => ("Текст не найден",
                $"Свой текст можно положить файлом\n{Path.Combine(LyricsService.Folder, LyricsService.SafeFileName(track.Artist == null ? track.Title : $"{track.Artist} - {track.Title}"))}.lrc"),
            LyricsStatus.Error => ("Не удалось загрузить текст", "Нет связи с lrclib.net — попробую ещё раз через минуту"),
            _ => ("", ""),
        };
        Status.Visibility = StatusText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBigCover();

        _current = int.MinValue;
        _jump = true;
        Tick();
    }

    /// <summary>Окно скрыто — ничего не пересчитываем.</summary>
    public void Stop()
    {
        _timer.Stop();
        _titleMarquee.Running = false;
        _artistMarquee.Running = false;
    }

    /// <summary>
    /// Бегущая строка, как в Spotify: длинный текст стоит, уезжает влево до конца, стоит и возвращается.
    /// В паузах анимации нет (их отсчитывает таймер) — WPF не перерисовывает обои зря.
    /// </summary>
    private sealed class Marquee
    {
        private static readonly TimeSpan StartPause = TimeSpan.FromSeconds(2.5);
        private static readonly TimeSpan EndPause = TimeSpan.FromSeconds(1.5);

        private readonly FrameworkElement _text;
        private readonly TranslateTransform _shift;
        private readonly DispatcherTimer _pause = new();
        private bool _running;
        private bool _forward;
        private double _overflow;
        /// <summary>
        /// Снятая анимация всё равно присылает Completed, когда доходит до конца. Без счётчика прокрутка
        /// прежнего длинного названия «оживала» у нового короткого и прижимала его к правому краю.
        /// </summary>
        private int _generation;
        private double _speed = AppSettings.DefaultMarqueeSpeed;
        private bool _moving;
        private int _fps = AppSettings.DefaultMarqueeFps;

        /// <summary>Кадров в секунду. Меняется и посреди прокрутки — с текущего места.</summary>
        public int Fps
        {
            get => _fps;
            set
            {
                value = Math.Clamp(value, AppSettings.MinMarqueeFps, AppSettings.MaxMarqueeFps);
                if (_fps == value)
                    return;
                _fps = value;
                if (_moving)
                {
                    _generation++;
                    Scroll();
                }
            }
        }

        /// <summary>Скорость в точках макета 1920×1080 в секунду. Меняется и посреди прокрутки — с текущего места.</summary>
        public double Speed
        {
            get => _speed;
            set
            {
                value = Math.Clamp(value, AppSettings.MinMarqueeSpeed, AppSettings.MaxMarqueeSpeed);
                if (_speed == value)
                    return;
                _speed = value;
                if (_moving)
                {
                    _generation++; // Completed прежней анимации больше не наш
                    Scroll();
                }
            }
        }

        public Marquee(FrameworkElement text, TranslateTransform shift)
        {
            _text = text;
            _shift = shift;
            // Обрезаем только по бокам: хвосты «y», «g», «р» выходят ниже строки и не должны срезаться
            if (text.Parent is FrameworkElement box)
            {
                box.ClipToBounds = false;
                box.Clip = new RectangleGeometry(new Rect(0, -box.Height, box.Width, box.Height * 3));
            }
            _text.SizeChanged += (_, _) => Restart();
            _pause.Tick += (_, _) =>
            {
                _pause.Stop();
                Scroll();
            };
        }

        public bool Running
        {
            set
            {
                if (_running == value)
                    return;
                _running = value;
                Restart();
            }
        }

        /// <summary>Новый текст: снова с начала строки.</summary>
        public void Restart()
        {
            _pause.Stop();
            _generation++;
            _moving = false;
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            _shift.X = 0;
            _forward = true;
            double width = (_text.Parent as FrameworkElement)?.ActualWidth ?? 0;
            _overflow = width > 0 ? _text.ActualWidth - width : 0;
            if (_running && _overflow > 1)
                Wait(StartPause);
        }

        private void Wait(TimeSpan delay)
        {
            _pause.Interval = delay;
            _pause.Start();
        }

        private void Scroll()
        {
            // Помещается целиком — стоит на месте
            if (!_running || _overflow <= 1)
                return;
            int generation = _generation;
            double target = _forward ? -_overflow : 0;
            // Путь считается от текущего места: при смене скорости посреди прокрутки остаётся только хвост
            double distance = Math.Abs(target - _shift.X);
            var animation = new DoubleAnimation(target, TimeSpan.FromSeconds(Math.Max(0.5, distance / _speed)))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            // Частота из настроек (по умолчанию 60: при 30 медленная строка заметно дёргалась).
            // Анимация идёт только в пути, в паузах кадров нет
            Timeline.SetDesiredFrameRate(animation, _fps);
            animation.Completed += (_, _) =>
            {
                if (!_running || generation != _generation)
                    return;
                _moving = false;
                _forward = !_forward;
                Wait(_forward ? StartPause : EndPause);
            };
            _moving = true;
            _shift.BeginAnimation(TranslateTransform.XProperty, animation);
        }
    }

    private void Tick()
    {
        _timer.Stop();
        if (_track is not { } track)
            return;

        var pos = track.PositionAt(DateTimeOffset.Now);
        if (_scrub == null && DateTime.UtcNow >= _scrubHold)
        {
            ElapsedText.Text = track.Duration > TimeSpan.Zero ? Format(pos) : "";
            ShowProgress(track.Duration > TimeSpan.Zero ? Math.Clamp(pos / track.Duration, 0, 1) : 0);
        }

        // Просыпаемся к следующей секунде на часах или к следующей строке — что раньше
        var next = TimeSpan.FromMilliseconds(1000 - pos.Milliseconds + 15);
        if (_lyrics.Status == LyricsStatus.Synced)
        {
            var lines = _lyrics.Lines;
            int current = -1;
            while (current + 1 < lines.Count && lines[current + 1].Time <= pos + Lead)
                current++;
            if (current != _current)
                Highlight(current);
            if (current + 1 < lines.Count && lines[current + 1].Time - Lead - pos + MinTick < next)
                next = lines[current + 1].Time - Lead - pos + MinTick;
        }
        else if (_lyrics.Status == LyricsStatus.Plain)
        {
            ScrollPlain(pos, track.Duration);
        }

        if (track.IsPlaying)
        {
            _timer.Interval = next < MinTick ? MinTick : next;
            _timer.Start();
        }
    }

    private void Highlight(int current)
    {
        _current = current;
        for (int i = 0; i < _lines.Count; i++)
        {
            double target = current < 0 ? (i == 0 ? NearOpacity : FarOpacity)
                : i == current ? CurrentOpacity
                : Math.Abs(i - current) == 1 ? NearOpacity
                : FarOpacity;
            if (Math.Abs(_lines[i].Opacity - target) > 0.005)
                _lines[i].BeginAnimation(OpacityProperty, new DoubleAnimation(target, FadeTime));
        }

        if (_lines.Count == 0 || !Measure())
            return;
        var line = _lines[Math.Max(current, 0)];
        double top = line.TranslatePoint(new Point(), Lines).Y;
        Shift(LinesCanvas.ActualHeight * Anchor - top - line.ActualHeight / 2, _jump ? null : ScrollTime, ease: true);
        _jump = false;
    }

    /// <summary>Текст без меток времени листаем равномерно по ходу песни.</summary>
    private void ScrollPlain(TimeSpan pos, TimeSpan duration)
    {
        if (!Measure())
            return;
        double height = LinesCanvas.ActualHeight;
        double range = Math.Max(0, Lines.ActualHeight - height * 0.5);
        double share = duration > TimeSpan.Zero ? Math.Clamp(pos / duration, 0, 1) : 0;
        Shift(height * 0.15 - share * range, _jump ? null : TimeSpan.FromSeconds(1), ease: false);
        _jump = false;
    }

    private bool Measure()
    {
        if (LinesCanvas.ActualHeight <= 0)
            return false; // ещё не на экране — пересчитаем, когда появится размер
        // Только что добавленные строки ещё не измерены, а сама панель об этом не знает — пересчитываем всегда
        Lines.UpdateLayout();
        return true;
    }

    private void Shift(double y, Duration? time, bool ease)
    {
        if (time is not { } t)
        {
            LinesShift.BeginAnimation(TranslateTransform.YProperty, null);
            LinesShift.Y = y;
            return;
        }
        var animation = new DoubleAnimation(y, t);
        if (ease)
            animation.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        LinesShift.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    /// <summary>
    /// Неофициальный клип или просто видео: текста нет — показываем обложку крупно вместо надписи «не найден»,
    /// а маленькую слева убираем, чтобы не повторялась.
    /// </summary>
    private void UpdateBigCover()
    {
        bool big = _track is { Source: MusicSource.YouTube } && _cover != null &&
                   _lyrics.Status is LyricsStatus.NotFound or LyricsStatus.Error;
        BigCover.Visibility = big ? Visibility.Visible : Visibility.Collapsed;
        CoverBox.Visibility = big ? Visibility.Collapsed : Visibility.Visible;
        if (!big)
            return;
        Status.Visibility = Visibility.Collapsed;
        double aspect = Aspect(_cover!);
        double width = BigCoverWidth, height = width / aspect;
        if (height > BigCoverHeight)
            (width, height) = (BigCoverHeight * aspect, BigCoverHeight);
        BigCover.Width = width;
        BigCover.Height = height;
    }

    /// <summary>Пропорции обложки: кадр из клипа шире квадрата, но не шире 16:9.</summary>
    private static double Aspect(BitmapSource image) =>
        Math.Clamp(image.PixelHeight > 0 ? (double)image.PixelWidth / image.PixelHeight : 1, 1, 16 / 9.0);

    private void SetCover(byte[]? data)
    {
        var image = Decode(data, 0);
        _cover = image;
        CoverBrush.ImageSource = image;
        BigCoverBrush.ImageSource = image;
        CoverGlyph.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
        // Почти квадратную обложку оставляем квадратной, кадр из клипа — во всю ширину
        CoverBox.Height = image == null || Aspect(image) < 1.15 ? CoverSize : CoverSize / Aspect(image);
        UpdateBigCover();

        // Для фона хватает крошечной копии: растянутая, она уже почти размыта, и яркие детали обложки
        // не превращаются в пятна под текстом. Новый фон проявляется поверх старого.
        var backdrop = Decode(data, 16);
        BackdropNext.Source = backdrop;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900));
        fade.Completed += (_, _) =>
        {
            if (BackdropNext.Source != backdrop)
                return;
            Backdrop.Source = backdrop;
            BackdropNext.BeginAnimation(OpacityProperty, null);
            BackdropNext.Opacity = 0;
        };
        BackdropNext.BeginAnimation(OpacityProperty, fade);
    }

    /// <param name="width">Ширина при декодировании, 0 — как есть.</param>
    private static BitmapImage? Decode(byte[]? data, int width)
    {
        if (data == null)
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = width;
            image.StreamSource = new MemoryStream(data);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // битая или незнакомая картинка — останется заглушка
        }
    }

    private void ShowProgress(double fraction)
    {
        ProgressScale.ScaleX = fraction;
        KnobShift.X = fraction * ProgressArea.ActualWidth - ProgressKnob.Width / 2;
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
