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

    private readonly DispatcherTimer _timer = new();
    private readonly List<TextBlock> _lines = new();
    private NowPlaying? _track;
    private Lyrics _lyrics = Lyrics.Loading;
    private int _current = int.MinValue;
    private bool _jump;
    private readonly Marquee _titleMarquee;
    private readonly Marquee _artistMarquee;

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

    public void ShowTrack(NowPlaying track)
    {
        var old = _track;
        _track = track;
        if (old?.Key != track.Key)
        {
            TitleText.Text = track.Title;
            ArtistText.Text = track.Artist ?? "";
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
        PauseGlyph.Visibility = track.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        DurationText.Text = Format(track.Duration);
        Tick();
    }

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
        /// <summary>Скорость в точках макета 1920×1080.</summary>
        private const double Speed = 22;
        private static readonly TimeSpan StartPause = TimeSpan.FromSeconds(2.5);
        private static readonly TimeSpan EndPause = TimeSpan.FromSeconds(1.5);

        // Мягкий край там, где текст продолжается: справа в начале, с обеих сторон в пути, слева в конце
        private static readonly Brush FadeRight = Fade(1, 0);
        private static readonly Brush FadeBoth = Fade(0, 0);
        private static readonly Brush FadeLeft = Fade(0, 1);

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
            _shift.BeginAnimation(TranslateTransform.XProperty, null);
            _shift.X = 0;
            _forward = true;
            var box = _text.Parent as FrameworkElement;
            double width = box?.ActualWidth ?? 0;
            _overflow = width > 0 ? _text.ActualWidth - width : 0;
            if (box != null)
                box.OpacityMask = _overflow > 1 ? FadeRight : null;
            if (_running && _overflow > 1)
                Wait(StartPause);
        }

        private static Brush Fade(double left, double right)
        {
            const double edge = 0.06;
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * left), 0, 0, 0), 0));
            brush.GradientStops.Add(new GradientStop(Colors.Black, edge));
            brush.GradientStops.Add(new GradientStop(Colors.Black, 1 - edge));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * right), 0, 0, 0), 1));
            brush.Freeze();
            return brush;
        }

        private void SetMask(Brush mask)
        {
            if (_text.Parent is FrameworkElement box)
                box.OpacityMask = mask;
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
            var animation = new DoubleAnimation(_forward ? -_overflow : 0, TimeSpan.FromSeconds(Math.Max(1, _overflow / Speed)))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            // При 30 кадрах медленная строка заметно дёргалась; анимация идёт только в пути, в паузах кадров нет
            Timeline.SetDesiredFrameRate(animation, 60);
            animation.Completed += (_, _) =>
            {
                if (!_running || generation != _generation)
                    return;
                _forward = !_forward;
                SetMask(_forward ? FadeRight : FadeLeft);
                Wait(_forward ? StartPause : EndPause);
            };
            SetMask(FadeBoth);
            _shift.BeginAnimation(TranslateTransform.XProperty, animation);
        }
    }

    private void Tick()
    {
        _timer.Stop();
        if (_track is not { } track)
            return;

        var pos = track.PositionAt(DateTimeOffset.Now);
        ElapsedText.Text = Format(pos);
        ProgressScale.ScaleX = track.Duration > TimeSpan.Zero ? Math.Clamp(pos / track.Duration, 0, 1) : 0;

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

    private void SetCover(byte[]? data)
    {
        var image = Decode(data, 0);
        CoverBrush.ImageSource = image;
        CoverGlyph.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;

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

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
