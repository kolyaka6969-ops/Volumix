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

    public DesktopLyricsView()
    {
        InitializeComponent();
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
        }
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
        UpdateBigCover();

        _current = int.MinValue;
        _jump = true;
        Tick();
    }

    /// <summary>Окно скрыто — ничего не пересчитываем.</summary>
    public void Stop() => _timer.Stop();

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

    /// <summary>
    /// Неофициальный клип или просто видео: текста нет — показываем обложку крупно вместо надписи «не найден»,
    /// а маленькую слева убираем, чтобы не повторялась.
    /// </summary>
    private void UpdateBigCover()
    {
        bool big = _track is { IsVideo: true } && _cover != null && _lyrics.Status is LyricsStatus.NotFound or LyricsStatus.Error;
        BigCover.Visibility = big ? Visibility.Visible : Visibility.Collapsed;
        CoverBox.Visibility = big ? Visibility.Collapsed : Visibility.Visible;
        if (big)
        {
            Status.Visibility = Visibility.Collapsed;
            double aspect = Aspect(_cover!);
            double width = BigCoverWidth, height = width / aspect;
            if (height > BigCoverHeight)
                (width, height) = (BigCoverHeight * aspect, BigCoverHeight);
            BigCover.Width = width;
            BigCover.Height = height;
        }
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

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
