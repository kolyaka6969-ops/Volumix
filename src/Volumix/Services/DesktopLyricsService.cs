using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using Volumix.Views;
using Forms = System.Windows.Forms;

namespace Volumix.Services;

/// <summary>
/// Песня и её текст на рабочем столе вместо обоев, пока играет Spotify или Яндекс Музыка (в том числе в браузере).
/// Окна работают в своём потоке: Explorer связывает очередь ввода дочернего окна со своей, и любое подвисание
/// интерфейса Volumix подвесило бы рабочий стол.
/// </summary>
public sealed class DesktopLyricsService : IDisposable
{
    /// <summary>
    /// Плеер присылает название раньше длительности, а без неё текст ищется хуже — ждём, пока всё придёт.
    /// Сам трек на обоях появляется сразу, задерживается только поиск текста.
    /// </summary>
    private static readonly TimeSpan FetchDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);
    /// <summary>
    /// Сколько держать заглушку, пока новый ролик YouTube проверяется. Проверка — поиск текста — занимает секунду-две,
    /// а Chrome при смене ролика ещё и на пару секунд выдаёт его за трек другого сайта.
    /// </summary>
    private static readonly TimeSpan HoldLimit = TimeSpan.FromSeconds(8);

    /// <summary>Как часто пересчитываем, где на экране кнопки: окно могли пересоздать, экран — сменить.</summary>
    private static readonly TimeSpan TargetsInterval = TimeSpan.FromSeconds(1);

    private readonly MediaSessionService _media;
    private readonly Dispatcher _main;
    private readonly DispatcherTimer _fetchTimer;
    private Dispatcher? _desktop;
    private CancellationTokenSource? _fetch;
    private string? _lyricsKey;
    private string? _trackKey;
    // Ключ ролика YouTube, для которого текст нашёлся: только такие ролики и попадают на обои
    private string? _music;
    // Разобранные название и исполнитель для подтверждённой песни из YouTube
    private (string Title, string? Artist)? _musicName;
    // Ролик YouTube, который сменил на обоях другую песню с YouTube (джем, плейлист): пока он проверяется,
    // обои не гаснут — иначе они мигали бы на каждом треке
    private string? _pending;
    private bool _showingYouTube;
    private bool _enabled;
    private bool _failed;
    // Обои сейчас на экране (с песней или заглушкой) — так считает поток интерфейса
    private bool _visible;
    // Заглушка уже отстояла своё — больше не ждём, прячем
    private bool _holdExpired;
    private readonly DispatcherTimer _holdTimer;
    private double _marqueeSpeed = AppSettings.DefaultMarqueeSpeed;
    private int _marqueeFps = AppSettings.DefaultMarqueeFps;
    private string _screen = AppSettings.AllScreens;
    private bool _controls = true;

    // Живут в потоке окон, трогать только через Post
    private readonly List<ScreenView> _screens = new();
    private NowPlaying? _shownTrack;
    private (string Key, Lyrics Lyrics)? _shownLyrics;
    private bool _shown;
    private readonly DesktopPointer _pointer = new();
    private DispatcherTimer? _targetsTimer;

    public DesktopLyricsService(MediaSessionService media, Dispatcher dispatcher)
    {
        _media = media;
        _main = dispatcher;
        _pointer.Pointer += (target, pressed, fraction) => Post(() =>
        {
            foreach (var screen in _screens)
                screen.View.SetPointer(target is { } t && t.Owner == screen.View ? t.Control : null, pressed, fraction);
        });
        _pointer.Click += (target, fraction) => _main.BeginInvoke(() =>
        {
            switch (target.Control)
            {
                case DesktopControl.Previous: _media.Previous(); break;
                case DesktopControl.PlayPause: _media.TogglePlayPause(); break;
                case DesktopControl.Next: _media.Next(); break;
                case DesktopControl.Seek: _media.Seek(_media.Duration * fraction); break;
            }
        });
        _holdTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = HoldLimit };
        _holdTimer.Tick += (_, _) =>
        {
            // Трек так и не подтвердился — значит, это не песня: убираем заглушку
            _holdTimer.Stop();
            _holdExpired = true;
            Sync();
        };
        _fetchTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _fetchTimer.Tick += (_, _) =>
        {
            _fetchTimer.Stop();
            _ = FetchAsync();
        };
        _media.Changed += Sync;
        _media.TimelineChanged += Sync;
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            Sync();
        }
    }

    /// <summary>Скорость бегущей строки названия; окна получают её в своём потоке.</summary>
    public double MarqueeSpeed
    {
        get => _marqueeSpeed;
        set
        {
            if (_marqueeSpeed == value)
                return;
            _marqueeSpeed = value;
            Post(() => _screens.ForEach(s => s.View.MarqueeSpeed = value));
        }
    }

    /// <summary>Кадров в секунду у бегущей строки названия.</summary>
    public int MarqueeFps
    {
        get => _marqueeFps;
        set
        {
            if (_marqueeFps == value)
                return;
            _marqueeFps = value;
            Post(() => _screens.ForEach(s => s.View.MarqueeFps = value));
        }
    }

    /// <summary>Кнопки «назад», «играть/пауза», «вперёд» на обоях.</summary>
    public bool Controls
    {
        get => _controls;
        set
        {
            if (_controls == value)
                return;
            _controls = value;
            Post(() =>
            {
                _screens.ForEach(s => s.View.ControlsVisible = value);
                UpdatePointer();
            });
        }
    }

    /// <summary>На каких мониторах показывать: все, основной или один по имени устройства (\\.\DISPLAY2).</summary>
    public string Screen
    {
        get => _screen;
        set
        {
            if (_screen == value)
                return;
            _screen = value;
            Post(() => Rebuild(value));
        }
    }

    private void Sync()
    {
        var track = _enabled ? _media.NowPlaying : null;
        if (track?.Key != _trackKey)
        {
            _trackKey = track?.Key;
            _pending = track?.Source == MusicSource.YouTube && _showingYouTube ? track.Key : null;
            if (track == null || track.Key != _music)
                _musicName = null;
        }
        // Обычное видео YouTube не должно висеть на обоях: пока текст не подтвердил, что это песня, прячем.
        // Обложка из каталога песен значит, что это песня, и текст можно не ждать: он подгрузится позже.
        // Кадр ролика (его ставят, когда в каталогах пусто) такого не значит — он есть и у влога.
        bool allowed = track != null &&
                       (track.Source != MusicSource.YouTube || track.Key == _music || track.Key == _pending ||
                        (track.Cover != null && !track.CoverIsFrame));
        _showingYouTube = allowed && track!.Source == MusicSource.YouTube;
        var shown = allowed ? Shown(track!) : null;
        if (shown != null)
        {
            _holdTimer.Stop();
            _holdExpired = false;
            _visible = true;
            EnsureThread();
            Post(() => ShowOnDesktop(shown));
        }
        // Обои на экране, а следующий трек ещё проверяется (или Chrome на миг потерял сеанс между роликами):
        // вместо рабочего стола — заглушка, пока трек не подтвердится
        else if (_enabled && _visible && !_holdExpired && (track == null || track.Source == MusicSource.YouTube))
        {
            if (!_holdTimer.IsEnabled)
            {
                _holdTimer.Start();
                Post(PlaceholderOnDesktop);
            }
        }
        else
            HideNow();

        // Длительность входит в ключ: по ней lrclib отличает версии песни
        var key = track == null ? null : $"{track.Key}\n{(int)track.Duration.TotalSeconds}";
        if (key == _lyricsKey)
            return;
        _lyricsKey = key;
        _fetch?.Cancel();
        _fetchTimer.Stop();
        if (key != null)
        {
            _fetchTimer.Interval = FetchDelay;
            _fetchTimer.Start();
        }
    }

    /// <summary>
    /// Для подтверждённой песни из YouTube — разобранные название и исполнитель, а не заголовок ролика
    /// с названием канала. Обложка, позиция и пауза всегда свежие, от плеера.
    /// </summary>
    private NowPlaying Shown(NowPlaying track) =>
        track.Key == _music && _musicName is { } name ? track with { Title = name.Title, Artist = name.Artist } : track;

    private async Task FetchAsync()
    {
        var track = _enabled ? _media.NowPlaying : null;
        if (track == null)
            return;
        var cts = new CancellationTokenSource();
        _fetch = cts;

        if (track.Source == MusicSource.YouTube)
        {
            // Из YouTube приходят и клипы, и лекции, и влоги — на обои пускаем только то, что нашлось в базе текстов.
            // Название ролика редко совпадает с названием песни, поэтому ищутся сразу все варианты прочтения.
            var candidates = MediaSessionService.Candidates(track.Title, track.Artist).Take(4).ToList();
            var (result, index) = await LyricsService.FindVideoAsync(candidates, track.Duration, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            bool failed = result.Status == LyricsStatus.Error;

            if (!IsMusic(result))
            {
                // Текст мог не найтись из-за недоступной базы текстов. Тогда пускаем песню на обои,
                // если её знает каталог обложек: случайные ролики и влоги там не находятся.
                var art = await CoverArtService.FindAsync(candidates, cts.Token);
                if (cts.IsCancellationRequested)
                    return;
                // Неофициальный клип или кавер: ни текста, ни обложки в каталогах, но по названию видно, что это музыка.
                // На обоях будет кадр ролика
                if (art != null || (!failed && IsMusicVideo(track.VideoTitle ?? track.Title)))
                {
                    Confirm(track, null, Lyrics.NotFound);
                }
                // Базы не ответили, а ролик пришёл следом за песней — оставляем на обоях до повторной проверки
                else if (!(failed && track.Key == _pending))
                {
                    _pending = null;
                    _showingYouTube = false;
                    HideNow();
                }
                if (failed)
                {
                    _fetchTimer.Interval = RetryDelay;
                    _fetchTimer.Start();
                }
                return;
            }

            var (title, artist) = candidates[index];
            Confirm(track, (title, artist ?? track.Artist), result);
            return;
        }

        var lyrics = await LyricsService.FindAsync(track.Title, track.Artist, track.Album, track.Duration, cts.Token);
        if (cts.IsCancellationRequested)
            return;

        Post(() => LyricsOnDesktop(track.Key, lyrics));
        if (lyrics.Status == LyricsStatus.Error)
        {
            _fetchTimer.Interval = RetryDelay;
            _fetchTimer.Start();
        }
    }

    /// <summary>Убрать обои сразу, без заглушки.</summary>
    private void HideNow()
    {
        _holdTimer.Stop();
        _holdExpired = false;
        _visible = false;
        Post(HideOnDesktop);
    }

    /// <summary>Ролик YouTube оказался песней: показываем его на обоях.</summary>
    private void Confirm(NowPlaying track, (string Title, string? Artist)? name, Lyrics lyrics)
    {
        _music = track.Key;
        _musicName = name;
        _pending = null;
        _showingYouTube = true;
        _holdTimer.Stop();
        _holdExpired = false;
        _visible = true;
        // Пока искали текст, плеер мог прислать обложку из каталога и новую позицию — берём свежие
        var current = _media.NowPlaying is { } now && now.Key == track.Key ? now : track;
        var shown = Shown(current);
        Post(() =>
        {
            ShowOnDesktop(shown);
            LyricsOnDesktop(shown.Key, lyrics);
        });
    }

    /// <summary>Текст нашёлся — значит ролик с песней, а не случайное видео.</summary>
    private static bool IsMusic(Lyrics lyrics) =>
        lyrics.Status is LyricsStatus.Synced or LyricsStatus.Plain or LyricsStatus.Instrumental;

    /// <summary>Пометки, которые ставят только у музыки: у влогов и лекций их не бывает.</summary>
    private static readonly string[] MusicVideoMarks =
    {
        "клип", "clip", "fan made", "fanmade", "fan video", "amv", "official video", "official music video", "music video", "official audio", "lyric video", "lyrics",
        "текст песни", "mv", "m/v", "cover", "кавер", "remix", "ремикс", "visualizer", "slowed", "sped up", "nightcore",
        "премьера трека", "премьера песни",
    };

    /// <summary>Название ролика похоже на песню: «Исполнитель - Песня (Fan Made Clip)», «Песня [кавер]».</summary>
    internal static bool IsMusicVideo(string title)
    {
        var lower = title.ToLowerInvariant();
        return MusicVideoMarks.Any(mark => Regex.IsMatch(lower,
            $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(mark)}(?![\p{{L}}\p{{N}}])"));
    }

    private void EnsureThread()
    {
        if (_desktop != null || _failed)
            return;
        var screen = _screen;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                // Explorer может работать с другим режимом DPI — без этого Windows не даст вставить окно
                NativeMethods.SetThreadDpiHostingBehavior(NativeMethods.DPI_HOSTING_BEHAVIOR_MIXED);
                var dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.UnhandledException += (_, e) =>
                {
                    App.LogError(e.Exception);
                    e.Handled = true;
                };
                Rebuild(screen);
                _desktop = dispatcher;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                ready.Set();
            }
            if (_desktop != null)
                Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Volumix desktop",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        _failed = _desktop == null;
        if (!_failed)
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    // Подключили или сняли монитор, сменили разрешение
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        var screen = _screen;
        Post(() => Rebuild(screen));
    }

    /// <summary>Мониторы, на которых должны быть обои: имена устройств, null — основной.</summary>
    internal static IReadOnlyList<string?> Devices(string setting)
    {
        var all = Forms.Screen.AllScreens;
        if (setting == AppSettings.AllScreens && all.Length > 0)
            return all.Select(s => (string?)s.DeviceName).ToList();
        if (setting != AppSettings.PrimaryScreen && all.Any(s => s.DeviceName == setting))
            return new string?[] { setting };
        // Выбранный монитор отключён — показываем на основном
        return new string?[] { null };
    }

    // ---------- Дальше — только в потоке окон ----------

    /// <summary>Своё окно на каждый нужный монитор: макет у каждого подгоняется под его экран.</summary>
    private void Rebuild(string setting)
    {
        var devices = Devices(setting);
        for (int i = _screens.Count - 1; i >= 0; i--)
        {
            if (devices.Contains(_screens[i].Device))
                continue;
            _screens[i].View.Stop();
            _screens[i].Layer.Dispose();
            _screens.RemoveAt(i);
        }
        foreach (var device in devices)
        {
            if (_screens.Any(s => s.Device == device))
                continue;
            var view = new DesktopLyricsView { MarqueeSpeed = _marqueeSpeed, MarqueeFps = _marqueeFps, ControlsVisible = _controls };
            var screen = new ScreenView(device, view, new DesktopLayer(view, device));
            _screens.Add(screen);
            if (!_shown || _shownTrack == null)
                continue;
            // Новый монитор подхватывает то, что уже показано на остальных
            view.ShowTrack(_shownTrack);
            if (_shownLyrics is { } lyrics)
                view.ShowLyrics(lyrics.Key, lyrics.Lyrics);
            screen.Layer.Visible = true;
        }
        UpdatePointer();
    }

    /// <summary>Мышь слушаем, только пока обои с кнопками на экране.</summary>
    private void UpdatePointer()
    {
        bool on = _shown && _controls;
        if (on)
        {
            if (_targetsTimer == null)
            {
                _targetsTimer = new DispatcherTimer { Interval = TargetsInterval };
                _targetsTimer.Tick += (_, _) => UpdateTargets();
            }
            _targetsTimer.Start();
            // Окно только что показано и ещё не разложено — кнопки посчитаем после раскладки
            _desktop?.BeginInvoke(UpdateTargets, DispatcherPriority.Loaded);
        }
        else
        {
            _targetsTimer?.Stop();
            _pointer.SetTargets(Array.Empty<DesktopPointer.Target>());
        }
        _pointer.Enabled = on;
    }

    private void UpdateTargets() =>
        _pointer.SetTargets(_screens
            .Where(s => s.Layer.Visible)
            .SelectMany(s => s.View.ControlBounds().Select(c => new DesktopPointer.Target(c.Bounds, s.View, c.Control)))
            .ToArray());

    private void ShowOnDesktop(NowPlaying track)
    {
        if (_shownTrack?.Key != track.Key)
            _shownLyrics = null;
        _shownTrack = track;
        _shown = true;
        foreach (var screen in _screens)
        {
            screen.View.ShowTrack(track);
            screen.Layer.Visible = true;
        }
        UpdatePointer();
    }

    /// <summary>Заглушка между треками: обои остаются, пока следующий трек проверяется.</summary>
    private void PlaceholderOnDesktop()
    {
        if (!_shown)
            return;
        _shownTrack = null;
        _shownLyrics = null;
        foreach (var screen in _screens)
            screen.View.ShowPlaceholder();
    }

    private void HideOnDesktop()
    {
        _shown = false;
        foreach (var screen in _screens)
        {
            screen.View.Stop();
            screen.Layer.Visible = false;
        }
        UpdatePointer();
    }

    private void LyricsOnDesktop(string key, Lyrics lyrics)
    {
        if (_shownTrack?.Key != key)
            return;
        _shownLyrics = (key, lyrics);
        foreach (var screen in _screens)
            screen.View.ShowLyrics(key, lyrics);
    }

    private void Post(Action action) => _desktop?.BeginInvoke(action);

    public void Dispose()
    {
        _media.Changed -= Sync;
        _media.TimelineChanged -= Sync;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _fetchTimer.Stop();
        _fetch?.Cancel();
        if (_desktop is not { } desktop)
            return;
        try
        {
            // Окна закрываем в их потоке и дожидаемся: при выходе Explorer должен вернуть обычные обои
            desktop.Invoke(() =>
                {
                    _pointer.Dispose();
                    _screens.ForEach(s => s.Layer.Dispose());
                },
                DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
        }
        catch (Exception) { }
        desktop.InvokeShutdown();
    }

    private sealed record ScreenView(string? Device, DesktopLyricsView View, DesktopLayer Layer);
}
