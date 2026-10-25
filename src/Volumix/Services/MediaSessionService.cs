using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Volumix.Services;

/// <summary>Плеер, из которого пришла песня.</summary>
public enum MusicSource { None, Spotify, YandexMusic, YouTubeMusic, YouTube }

/// <summary>Какие плееры слушает Volumix.</summary>
public enum MusicSourceMode
{
    /// <summary>Spotify, Яндекс Музыка и YouTube — в том числе открытые в браузере.</summary>
    Auto,
    Spotify,
    /// <summary>Яндекс Музыка, а также любой плеер в браузере: сайт в медиа-сеансе не виден, поэтому здесь браузер доверяем целиком.</summary>
    YandexMusic,
    /// <summary>YouTube, а также любой плеер в браузере.</summary>
    YouTube,
}

/// <summary>Что играет. Позиция — на момент <see cref="PositionTime"/>, во время игры её досчитываем по часам.</summary>
public sealed record NowPlaying(string Title, string? Artist, string? Album, byte[]? Cover, bool IsPlaying,
    TimeSpan Position, DateTimeOffset PositionTime, TimeSpan Duration, MusicSource Source = MusicSource.None)
{
    /// <summary>Один и тот же трек, даже если сменились позиция или обложка.</summary>
    public string Key => $"{Artist}\n{Title}\n{Album}";

    public TimeSpan PositionAt(DateTimeOffset now) => MediaSessionService.Extrapolate(Position, PositionTime, IsPlaying, Duration, now);
}

/// <summary>
/// Песня и обложка из системных медиа-сеансов Windows (те же, что у кнопок в панели громкости).
/// Так играет Spotify, Яндекс Музыка и браузеры — Windows показывает их единым списком.
/// События сеансов приходят из пула потоков, наружу всё отдаётся в потоке интерфейса.
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    /// <summary>Как часто проверяем, не открылась ли музыка в браузере.</summary>
    private static readonly TimeSpan BrowserTick = TimeSpan.FromSeconds(4);

    private static readonly string[] BrowserIds =
    {
        "chrome", "chromium", "msedge", "firefox", "opera", "brave", "vivaldi", "browser", "yandexbrowser",
    };

    /// <summary>Шум в названиях роликов YouTube: «(Official Video)», «[MV]», «(Lyrics)» и подобное.</summary>
    private static readonly string[] VideoNoise =
    {
        "official", "video", "audio", "lyric", "клип", "премьер", "premiere", "официальн", "видео", "текст",
        "visualizer", "hd", "4k", "8k", "m/v", "mv", "kbps", "320", "slowed", "reverb", "sped up", "speed up",
        "bass boost", "nightcore", "full", "creditless", "fps", "1080", "tv size", "romaji", "eng sub",
    };

    private static readonly Regex Brackets = new(@"[\(\[\{][^\)\]\}]*[\)\]\}]");
    /// <summary>Приписка после названия без скобок: «Песня + Текст», «Песня | Lyrics», «Песня - текст песни».</summary>
    private static readonly Regex NoiseTail = new(
        @"\s*[+|/•–—-]\s*(?:текст(?:\s+песни)?|с\s+текстом|lyrics?|караоке|karaoke)\s*$", RegexOptions.IgnoreCase);
    // «starring» — так YouTube-каналы пишут гостя в клипах: «STORM starring Yung Lean»
    private static readonly Regex FeatTail = new(@"\s+(?:ft|feat|featuring|starring)\.?\s.*$", RegexOptions.IgnoreCase);

    private readonly Dispatcher _dispatcher;
    private readonly BrowserMusicDetector _browser = new();
    private readonly DispatcherTimer _browserTimer;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    // Все сеансы, у которых слушаем смену «играет/пауза»: заиграл другой плеер — пора выбрать заново
    private readonly List<GlobalSystemMediaTransportControlsSession> _watched = new();
    private MusicSourceMode _mode = MusicSourceMode.Auto;
    private MusicSource _source;
    private string? _coverKey;
    private string? _catalogKey;
    // Обложка взята из каталога, а не от плеера
    private bool _catalogCover;
    // Картинка, которую прислал плеер для текущего и прошлого трека, и альбом прошлого трека
    private byte[]? _thumb, _previousThumb;
    private string? _thumbKey, _thumbAlbum, _previousAlbum;
    // Картинку отложили как оставшуюся от прошлого трека: пригодится, если в каталогах ничего не найдётся
    private byte[]? _suspectCover;
    private int _refreshVersion;
    private bool _acceptAnyTimeline = true;
    private bool _disposed;
    /// <summary>Каталоги обложек не ответили (лимит запросов, таймаут) — спросим ещё раз.</summary>
    private static readonly TimeSpan CoverRetry = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _coverRetry;

    public MediaSessionService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _coverRetry = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = CoverRetry };
        _coverRetry.Tick += (_, _) =>
        {
            _coverRetry.Stop();
            _ = RefreshAsync();
        };
        _browserTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = BrowserTick };
        // Браузер не сообщает о смене вкладки никаким событием — приходится заглядывать самим.
        // Таймер работает, только пока в браузере есть медиа-сеанс (см. UpdateWatch).
        _browserTimer.Tick += (_, _) => AttachSession();
    }

    public MusicSourceMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;
            _mode = value;
            AttachSession();
        }
    }

    public bool IsAvailable => _session != null;
    public MusicSource Source => _source;
    public bool IsPlaying { get; private set; }
    public string? Title { get; private set; }
    public string? Artist { get; private set; }
    public string? Album { get; private set; }
    /// <summary>Обложка как её отдаёт плеер (PNG или JPEG).</summary>
    public byte[]? Cover { get; private set; }
    public TimeSpan Duration { get; private set; }
    /// <summary>Позиция на момент <see cref="PositionTime"/>; во время игры к ней прибавляется прошедшее время.</summary>
    public TimeSpan Position { get; private set; }
    public DateTimeOffset PositionTime { get; private set; }

    public NowPlaying? NowPlaying => _session == null || Title == null ? null
        : new(Title, Artist, Album, Cover, IsPlaying, Position, PositionTime, Duration, _source);

    public event Action? Changed;

    /// <summary>Сменилась позиция в треке: перемотка или очередная сверка (плеер присылает её раз в несколько секунд).</summary>
    public event Action? TimelineChanged;

    public async void Start()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed)
                return;
            _manager.SessionsChanged += (_, _) => _dispatcher.BeginInvoke(AttachSession);
            AttachSession();
        }
        catch (Exception)
        {
            // Медиа-сеансы недоступны (урезанная сборка Windows) — кнопки просто не появятся.
        }
    }

    /// <summary>
    /// Опознаёт плеер по AUMID. AUMID браузера — просто имя программы: «Chrome», «MSEdge», «YandexBrowser».
    /// Сайт, открытый во вкладке, по AUMID не виден — его подсказывает <see cref="BrowserMusicDetector"/>.
    /// </summary>
    internal static MusicSource Classify(string? aumid, MusicSourceMode mode, BrowserMusic browser)
    {
        if (IsSpotify(aumid))
            return mode == MusicSourceMode.YandexMusic ? MusicSource.None : MusicSource.Spotify;
        if (IsYandexMusicApp(aumid))
            return mode == MusicSourceMode.Spotify ? MusicSource.None : MusicSource.YandexMusic;
        if (!IsBrowser(aumid))
            return MusicSource.None;
        return mode switch
        {
            MusicSourceMode.Spotify => MusicSource.None,
            // Явный выбор источника — запасной путь: сайт опознать не всегда выходит, а песня играет.
            MusicSourceMode.YandexMusic => MusicSource.YandexMusic,
            MusicSourceMode.YouTube => MusicSource.YouTube,
            _ => ToSource(browser),
        };
    }

    internal static MusicSource ToSource(BrowserMusic browser) => browser switch
    {
        BrowserMusic.YandexMusic => MusicSource.YandexMusic,
        BrowserMusic.YouTubeMusic => MusicSource.YouTubeMusic,
        BrowserMusic.YouTube => MusicSource.YouTube,
        _ => MusicSource.None,
    };

    internal static BrowserMusic ToBrowser(MusicSource source) => source switch
    {
        MusicSource.YandexMusic => BrowserMusic.YandexMusic,
        MusicSource.YouTubeMusic => BrowserMusic.YouTubeMusic,
        MusicSource.YouTube => BrowserMusic.YouTube,
        _ => BrowserMusic.None,
    };

    internal static bool IsSpotify(string? aumid) =>
        !string.IsNullOrEmpty(aumid) && aumid.Contains("spotify", StringComparison.OrdinalIgnoreCase);

    internal static bool IsYandexMusicApp(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid))
            return false;
        bool yandex = aumid.Contains("yandex", StringComparison.OrdinalIgnoreCase) ||
                      aumid.Contains("яндекс", StringComparison.OrdinalIgnoreCase);
        bool music = aumid.Contains("music", StringComparison.OrdinalIgnoreCase) ||
                     aumid.Contains("музык", StringComparison.OrdinalIgnoreCase);
        return yandex && music;
    }

    internal static bool IsBrowser(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid))
            return false;
        var id = aumid.Contains('\\') || aumid.Contains('/') ? Path.GetFileNameWithoutExtension(aumid) : aumid;
        return BrowserIds.Any(b => id.Contains(b, StringComparison.OrdinalIgnoreCase));
    }

    private void AttachSession()
    {
        if (_disposed || _manager == null)
            return;

        var (found, kind) = Pick();

        // Переподписываемся только на новый сеанс: после перезапуска плеера сеанс новый, хоть и с тем же именем.
        if (ReferenceEquals(found, _session))
        {
            if (_source == kind)
                return;
            // Поменялась настройка источника: тот же сеанс стал другим плеером.
            _source = kind;
            _ = RefreshAsync();
            return;
        }

        Unsubscribe();
        _session = found;
        _source = kind;
        if (_session != null)
        {
            _session.PlaybackInfoChanged += OnSessionChanged;
            _session.MediaPropertiesChanged += OnSessionChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }
        _acceptAnyTimeline = true;
        _coverKey = null;
        _ = RefreshAsync();
    }

    private (GlobalSystemMediaTransportControlsSession? Session, MusicSource Kind) Pick()
    {
        try
        {
            var sessions = _manager!.GetSessions();
            bool anyBrowser = sessions.Any(s => IsBrowser(s.SourceAppUserModelId));
            UpdateWatch(sessions, anyBrowser);
            // Заголовки окон перебираем, только когда в браузере вообще что-то играет: это самая дорогая проверка.
            var site = anyBrowser && _mode == MusicSourceMode.Auto ? _browser.Check() : BrowserMusic.None;

            var candidates = new List<Candidate>();
            foreach (var session in sessions)
            {
                var kind = Classify(session.SourceAppUserModelId, _mode, site);
                if (kind == MusicSource.None)
                    continue;
                bool playing = false;
                var updated = DateTimeOffset.MinValue;
                try
                {
                    playing = session.GetPlaybackInfo()?.PlaybackStatus ==
                              GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    updated = session.GetTimelineProperties().LastUpdatedTime;
                }
                catch (Exception)
                {
                    // Сеанс закрылся между вызовами — не беда.
                }
                bool browser = IsBrowser(session.SourceAppUserModelId);
                // Пока браузерный сеанс считается музыкой, продлеваем память о вкладке: она может
                // уйти в фон, а песня — играть дальше.
                if (browser)
                    _browser.Touch(ToBrowser(kind));
                candidates.Add(new Candidate(session, kind, browser, playing, updated));
            }

            if (candidates.Count == 0)
                return (null, MusicSource.None);

            var playingNow = candidates.Where(c => c.Playing).ToList();
            // Ничего не играет — остаёмся на том, что играло последним: поставили YouTube на паузу,
            // и «играть» должно продолжить YouTube, а не Spotify
            if (playingNow.Count == 0 && candidates.FirstOrDefault(c => ReferenceEquals(c.Session, _session)) is { Session: not null } last)
                return (last.Session, last.Kind);
            // Приостановленную вкладку браузера берём, только если она уже играла у нас: Chrome держит сеанс
            // от любого видео и после закрытия ролика, и старый заголовок иначе подменил бы песню.
            var pool = playingNow.Count > 0
                ? playingNow
                : candidates.Where(c => !c.Browser || ReferenceEquals(c.Session, _session)).ToList();
            if (pool.Count == 0)
                pool = candidates;

            var best = pool
                // Обычный ролик YouTube уступает настоящему плееру: если играют оба, на обоях должна быть музыка.
                .OrderByDescending(c => c.Kind == MusicSource.YouTube ? 0 : 1)
                .ThenByDescending(c => c.Updated)
                .First();
            return (best.Session, best.Kind);
        }
        catch (Exception)
        {
            return (null, MusicSource.None);
        }
    }

    /// <summary>
    /// Раньше раз в 4 секунды заново перебирались все сеансы — чтобы заметить, что заиграл другой плеер.
    /// Теперь это делают события сеансов, а опрос нужен только для браузера: смену вкладки он не сообщает.
    /// </summary>
    private void UpdateWatch(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions, bool anyBrowser)
    {
        for (int i = _watched.Count - 1; i >= 0; i--)
        {
            if (sessions.Any(s => ReferenceEquals(s, _watched[i])))
                continue;
            try { _watched[i].PlaybackInfoChanged -= OnAnyPlaybackChanged; } catch (Exception) { }
            _watched.RemoveAt(i);
        }
        foreach (var session in sessions)
        {
            if (_watched.Any(s => ReferenceEquals(s, session)))
                continue;
            try
            {
                session.PlaybackInfoChanged += OnAnyPlaybackChanged;
                _watched.Add(session);
            }
            catch (Exception) { }
        }

        // Заголовки окон браузера нужны только в автоматическом режиме: в остальных браузер доверяется целиком
        bool poll = anyBrowser && _mode == MusicSourceMode.Auto;
        if (poll != _browserTimer.IsEnabled)
        {
            if (poll) _browserTimer.Start();
            else _browserTimer.Stop();
        }
    }

    private void OnAnyPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _dispatcher.BeginInvoke(AttachSession);

    private void UnwatchAll()
    {
        foreach (var session in _watched)
            try { session.PlaybackInfoChanged -= OnAnyPlaybackChanged; } catch (Exception) { }
        _watched.Clear();
    }

    private void Unsubscribe()
    {
        if (_session == null)
            return;
        _session.PlaybackInfoChanged -= OnSessionChanged;
        _session.MediaPropertiesChanged -= OnSessionChanged;
        _session.TimelinePropertiesChanged -= OnTimelineChanged;
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _dispatcher.BeginInvoke(() => _ = RefreshAsync());

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || sender != _session)
                return;
            ReadTimeline(sender);
            TimelineChanged?.Invoke();
        });

    private async Task RefreshWhenResolvedAsync(string video)
    {
        var session = _session;
        if (await AnimeThemeService.ResolveAsync(video) != null && !_disposed && session == _session)
            await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var session = _session;
        int version = ++_refreshVersion;
        bool playing = false;
        string? title = null, artist = null, album = null;
        IRandomAccessStreamReference? thumbnail = null;
        if (session != null)
        {
            try
            {
                playing = session.GetPlaybackInfo()?.PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var props = await session.TryGetMediaPropertiesAsync();
                title = string.IsNullOrWhiteSpace(props?.Title) ? null : props.Title;
                artist = string.IsNullOrWhiteSpace(props?.Artist) ? null : props.Artist;
                album = string.IsNullOrWhiteSpace(props?.AlbumTitle) ? null : props.AlbumTitle;
                thumbnail = props?.Thumbnail;
            }
            catch (Exception)
            {
                // Плеер закрылся посреди запроса — следующий SessionsChanged всё поправит.
            }
        }
        // Пока ждали ответа, пришло событие поновее — его обновление и победит
        if (_disposed || session != _session || version != _refreshVersion)
            return;

        // Сайт в медиа-сеансе не виден. Если название песни есть в заголовке окна, значит вкладка на виду —
        // уточняем, что это за сайт: Яндекс Музыка, YouTube Music или обычный ролик YouTube.
        // Сверяем с названием как есть: после очистки «[Official Video]» его в заголовке окна уже не найти.
        bool sourceChanged = false;
        bool browser = session != null && IsBrowser(session.SourceAppUserModelId);
        if (browser && _mode == MusicSourceMode.Auto)
        {
            var matched = _browser.Match(title);
            if (matched != BrowserMusic.None)
            {
                var refined = ToSource(matched);
                if (refined != _source)
                {
                    _source = refined;
                    sourceChanged = true;
                }
            }
        }

        if (title != null)
        {
            var video = _source == MusicSource.YouTube ? VideoTitle(title) : null;
            (title, artist, album) = CleanMetadata(title, artist, album, _source);
            // Опенинг аниме: чего нет в названии ролика (исполнителя, а то и самой песни), узнаём у AnimeThemes.
            // Отвечает она секунд по пять — не ждём: пока показываем прочитанное из названия, а с ответом обновимся
            if (video != null)
            {
                if (AnimeThemeService.TryGetResolved(video, out var anime))
                {
                    if (anime is { } song)
                        (title, artist) = song;
                }
                else
                    _ = RefreshWhenResolvedAsync(video);
            }
        }

        var key = $"{artist}\n{title}\n{album}";
        bool newTrack = key != _coverKey;
        if (key != _thumbKey)
        {
            // Не по newTrack: прерванное обновление уже могло записать картинку нового трека
            _thumbKey = key;
            _previousThumb = _thumb;
            _previousAlbum = _thumbAlbum;
            _thumbAlbum = album;
            _thumb = null;
            _suspectCover = null;
        }
        // Браузер меняет картинку в сеансе позже названия: сначала у новой песни висит картинка прошлой вкладки
        // (кадр ролика YouTube у трека Яндекс Музыки). Поэтому у браузера картинку перечитываем при каждом
        // событии и подхватываем, как только она сменилась.
        byte[]? raw = null;
        bool thumbChanged = false;
        if (browser || newTrack || sourceChanged || (Cover == null && thumbnail != null))
        {
            raw = thumbnail == null ? null : await ReadAsync(thumbnail);
            if (_disposed || session != _session || version != _refreshVersion)
                return;
            thumbChanged = !SameBytes(raw, _thumb);
            _thumb = raw;
        }
        if (_source == MusicSource.YouTube)
        {
            // Браузер присылает логотип сайта или кадр ролика — на обоях это выглядит как ошибка,
            // поэтому у YouTube обложка берётся только из каталога, а до неё стоит заглушка.
            if (newTrack)
            {
                _coverKey = key;
                Cover = null;
                _catalogCover = false;
            }
        }
        else if (newTrack || sourceChanged || thumbChanged)
        {
            var cover = raw;
            // Если сайт не дал обложку (так бывает у Яндекс Музыки в Chrome), браузер подставляет свой значок
            if (cover != null && browser && IsAppIcon(cover))
                cover = null;
            // Картинка прошлого трека с другого альбома — браузер ещё не успел сменить её
            if (cover != null && browser && newTrack && IsLeftover(cover, _previousThumb, _previousAlbum, album))
            {
                _suspectCover = cover;
                cover = null;
            }
            _coverKey = key;
            if (newTrack || sourceChanged)
            {
                Cover = cover;
                _catalogCover = false;
            }
            // Плеер прислал обложку позже названия. Чёткую из каталога мелкой от браузера не заменяем.
            else if (cover != null && !(_catalogCover && PixelSize(cover) < SharpCover))
            {
                Cover = cover;
                _catalogCover = false;
                _catalogKey = null;
            }
        }

        if (playing != IsPlaying)
        {
            // Плеер не всегда сообщает позицию при паузе и продолжении — замираем и продолжаем от своей оценки
            var now = DateTimeOffset.Now;
            Position = Extrapolate(Position, PositionTime, IsPlaying, Duration, now);
            PositionTime = now;
            IsPlaying = playing;
        }
        if (newTrack)
        {
            Position = TimeSpan.Zero;
            PositionTime = DateTimeOffset.Now;
            _acceptAnyTimeline = true;
        }
        Title = title;
        Artist = artist;
        Album = album;
        if (session != null)
            ReadTimeline(session);
        // Трек показываем сразу, не дожидаясь каталогов: обложка подменится, когда найдётся
        Changed?.Invoke();

        // YouTube отдаёт в медиа-сеансе фавикон сайта, а на обоях нужна обложка трека — ищем её в каталогах.
        // Так же с другими сайтами в браузере, если вместо обложки пришёл значок браузера,
        // и с любым плеером, если обложка мелкая: Chrome ужимает её до 150×150, Spotify отдаёт 300×300,
        // а на обоях она растягивается до 550 и больше.
        if (title != null && (newTrack || sourceChanged || _catalogKey != key) &&
            (_source == MusicSource.YouTube || (browser && Cover == null) || (Cover != null && PixelSize(Cover) < SharpCover)))
        {
            var variants = Candidates(title, artist).Take(4).ToList();
            var (art, failed) = await CoverArtService.LookUpAsync(variants);
            if (_disposed || session != _session || version != _refreshVersion)
                return;
            // Каталоги не ответили — трек не считаем проверенным, иначе он остался бы без обложки до конца.
            // За долгий микс на YouTube запросов набирается много, и лимиты каталогов срабатывают
            if (failed)
                _coverRetry.Start();
            else
                _catalogKey = key;
            // Не нашлось в каталогах — остаётся то, что прислал плеер
            if (art != null || _source == MusicSource.YouTube)
            {
                Cover = art;
                _catalogCover = art != null;
                Changed?.Invoke();
            }
            else if (Cover == null && _suspectCover != null)
            {
                // В каталогах нет, а браузер так и не сменил картинку — значит, она всё-таки этого трека
                Cover = _suspectCover;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Браузеры присылают «Исполнитель — Название» одной строкой, часто с названием сайта в конце.
    /// У YouTube в названии ещё и «(Official Video)», а в исполнителе — канал. Приводим к виду,
    /// который понимают lrclib и экран на рабочем столе.
    /// </summary>
    internal static (string Title, string? Artist, string? Album) CleanMetadata(
        string title, string? artist, string? album, MusicSource source)
    {
        title = WithoutSiteName(title);

        if (source is MusicSource.YouTube or MusicSource.YouTubeMusic)
        {
            title = StripVideoNoise(title);
            // Опенинг аниме: песня и исполнитель внутри названия, а канал — просто тот, кто залил
            if (source == MusicSource.YouTube && AnimeThemeService.FromTitle(title) is { } anime)
                return (anime.Title, anime.Artist, album);
            artist = CleanChannel(artist);
        }

        if (string.IsNullOrWhiteSpace(artist) && source != MusicSource.Spotify)
        {
            foreach (var separator in new[] { " — ", " – ", " - ", " | " })
            {
                int i = title.IndexOf(separator, StringComparison.Ordinal);
                if (i <= 0 || i + separator.Length >= title.Length)
                    continue;
                artist = title[..i].Trim();
                title = title[(i + separator.Length)..].Trim();
                break;
            }
        }
        // На YouTube исполнитель нередко уже в названии: «Кино - Группа крови» при канале «Кино - Topic».
        if (source is MusicSource.YouTube or MusicSource.YouTubeMusic && !string.IsNullOrWhiteSpace(artist))
        {
            foreach (var separator in new[] { " — ", " – ", " - ", " | " })
            {
                var prefix = artist + separator;
                if (title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && title.Length > prefix.Length)
                {
                    title = title[prefix.Length..].Trim();
                    break;
                }
                // Канал «Wiz Khalifa Music», «Мот TV», «Artist Records»: имя канала начинается с исполнителя из названия
                int i = title.IndexOf(separator, StringComparison.Ordinal);
                if (i >= 3 && i + separator.Length < title.Length)
                {
                    var left = title[..i].Trim();
                    if (artist.Length > left.Length && artist.StartsWith(left + " ", StringComparison.OrdinalIgnoreCase))
                    {
                        artist = left;
                        title = title[(i + separator.Length)..].Trim();
                        break;
                    }
                }
            }
        }

        return (title, string.IsNullOrWhiteSpace(artist) ? null : artist, album);
    }

    private static string WithoutSiteName(string title)
    {
        title = title.Trim();
        foreach (var suffix in new[]
                 {
                     " — Яндекс Музыка", " | Яндекс Музыка", " - Яндекс Музыка", " — Yandex Music", " | Yandex Music",
                     " - YouTube", " | YouTube",
                 })
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                title = title[..^suffix.Length].Trim();
        return title;
    }

    /// <summary>Название ролика YouTube без названия сайта и пометок вроде «(Official Video)».</summary>
    internal static string VideoTitle(string title) => StripVideoNoise(WithoutSiteName(title));

    /// <summary>«Кино - Группа крови (Official Video)» → «Кино - Группа крови».</summary>
    internal static string StripVideoNoise(string title)
    {
        var cleaned = Brackets.Replace(title, match => HasVideoNoise(match.Value) ? " " : match.Value);
        cleaned = NoiseTail.Replace(cleaned, "");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim(' ', '-', '—', '–', '|', '+');
        return cleaned.Length == 0 ? title.Trim() : cleaned;
    }

    /// <summary>Канал YouTube — не всегда исполнитель: у «Кино - Topic» и «KinoVEVO» имя трека в конце.</summary>
    internal static string? CleanChannel(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return null;
        var name = artist.Trim();
        var stripped = false;
        foreach (var suffix in new[] { " - Topic", " – Topic", "VEVO", " Official" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
            {
                name = name[..^suffix.Length].Trim();
                stripped = true;
            }
        // «TheCranberriesVEVO» → «TheCranberries» → «The Cranberries»
        if (stripped && !name.Contains(' '))
            name = Regex.Replace(name, @"(?<=[\p{Ll}\d])(?=\p{Lu})", " ");
        return name.Length == 0 ? artist : name;
    }

    /// <summary>
    /// Варианты пары «название — исполнитель» для поиска в каталогах. У YouTube в названии часто
    /// «Песня - Исполнитель (пояснение)», а в поле исполнителя — канал, поэтому одного варианта мало:
    /// перебираем прочтения, начиная с самого вероятного.
    /// </summary>
    internal static IReadOnlyList<(string Title, string? Artist)> Candidates(string title, string? artist)
    {
        var list = new List<(string Title, string? Artist)>();
        void Add(string? candidateTitle, string? candidateArtist)
        {
            candidateTitle = candidateTitle?.Trim(' ', '-', '—', '–', '|');
            candidateArtist = string.IsNullOrWhiteSpace(candidateArtist) ? null : candidateArtist.Trim();
            if (string.IsNullOrWhiteSpace(candidateTitle) || candidateTitle.Length < 2)
                return;
            foreach (var item in list)
            {
                if (string.Equals(item.Title, candidateTitle, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Artist ?? "", candidateArtist ?? "", StringComparison.OrdinalIgnoreCase))
                    return;
            }
            list.Add((candidateTitle, candidateArtist));
        }

        Add(title, artist);
        // Скобки — обычно пояснение к ролику, а не часть названия песни
        var stripped = WithoutBrackets(title);
        // «See You Again ft. Charlie Puth Furious 7 Soundtrack» → «See You Again»: в каталогах гость не в названии
        var core = WithoutFeat(stripped);
        if (core != stripped)
        {
            if (artist != null)
                Add(core, artist);
            Split(core, Add);
        }
        Split(stripped, Add);
        Add(stripped, artist);
        Add(stripped, null);
        Split(title, Add);
        return list;
    }

    /// <summary>Без приглашённого исполнителя и всего, что после него: «Песня ft. Гость …» → «Песня».</summary>
    internal static string WithoutFeat(string title)
    {
        var cleaned = FeatTail.Replace(title, "").Trim(' ', '-', '—', '–', '|');
        return cleaned.Length < 2 ? title : cleaned;
    }

    /// <summary>Название без пояснений в скобках: «Песня (Official Video)» → «Песня».</summary>
    internal static string WithoutBrackets(string title)
    {
        var cleaned = Brackets.Replace(title, " ");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim(' ', '-', '—', '–', '|');
        return cleaned.Length == 0 ? title.Trim() : cleaned;
    }

    private static void Split(string title, Action<string?, string?> add)
    {
        foreach (var separator in new[] { " — ", " – ", " - ", " | ", ":" })
        {
            int i = title.IndexOf(separator, StringComparison.Ordinal);
            if (i <= 0 || i + separator.Length >= title.Length)
                continue;
            var left = title[..i].Trim();
            var right = title[(i + separator.Length)..].Trim();
            // В роликах «Песня - Исполнитель», а после двоеточия наоборот: «Исполнитель: Песня»
            if (separator == ":")
            {
                add(right, left);
                add(left, right);
            }
            else
            {
                add(left, right);
                add(right, left);
            }
            return;
        }
    }

    private static bool HasVideoNoise(string bracket)
    {
        var lower = bracket.ToLowerInvariant();
        return VideoNoise.Any(word => lower.Contains(word));
    }

    private void ReadTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var t = session.GetTimelineProperties();
            var duration = t.EndTime - t.StartTime;
            var at = t.LastUpdatedTime;
            var now = DateTimeOffset.Now;
            if (at > now || at.Year < 2000)
                at = now;
            // Сведения старше нашей оценки (снятой при паузе) только сбили бы позицию назад
            if (at < PositionTime && !_acceptAnyTimeline)
                return;
            _acceptAnyTimeline = false;
            Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
            Position = t.Position - t.StartTime;
            PositionTime = at;
        }
        catch (Exception)
        {
            // Сеанс закрылся — позиция просто не обновится.
        }
    }

    internal static TimeSpan Extrapolate(TimeSpan position, DateTimeOffset at, bool playing, TimeSpan duration, DateTimeOffset now)
    {
        var pos = playing ? position + (now - at) : position;
        if (pos < TimeSpan.Zero)
            return TimeSpan.Zero;
        return duration > TimeSpan.Zero && pos > duration ? duration : pos;
    }

    private static async Task<byte[]?> ReadAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 16 * 1024 * 1024)
                return null;
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Значок приложения (Chrome, Edge) — картинка с полностью прозрачными углами. Обложки альбомов
    /// непрозрачны (у Spotify углы бывают полупрозрачными, но не пустыми), а JPEG прозрачным не бывает вовсе.
    /// </summary>
    internal static bool IsAppIcon(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(ms,
                System.Windows.Media.Imaging.BitmapCreateOptions.None,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            if (frame.Format.BitsPerPixel < 32 || frame.PixelWidth < 2 || frame.PixelHeight < 2)
                return false;
            var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixel = new byte[4];
            foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
            {
                bgra.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
                if (pixel[3] >= 16)
                    return false;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Картинка осталась от прошлого трека: те же байты, а альбом другой. У треков одного альбома
    /// обложка общая, поэтому одинаковая картинка там — норма.
    /// </summary>
    internal static bool IsLeftover(byte[] cover, byte[]? previousThumb, string? previousAlbum, string? album) =>
        previousAlbum != album && SameBytes(cover, previousThumb);

    private static bool SameBytes(byte[]? a, byte[]? b) =>
        a == null ? b == null : b != null && a.AsSpan().SequenceEqual(b);

    /// <summary>Обложка меньше этого размера на обоях выглядит мыльной.</summary>
    private const int SharpCover = 400;

    /// <summary>Меньшая сторона картинки в пикселях; 0, если её не прочитать. Читается только заголовок файла.</summary>
    internal static int PixelSize(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(ms,
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None).Frames[0];
            return Math.Min(frame.PixelWidth, frame.PixelHeight);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public void TogglePlayPause() => Run(s => s.TryTogglePlayPauseAsync().AsTask());

    /// <summary>
    /// Следующий трек. У обычного ролика YouTube Chrome не даёт «вперёд» (только в плейлистах и миксах), зато даёт
    /// перемотку: доматываем ролик до конца, и YouTube сам включает следующий.
    /// </summary>
    public void Next() => Run(async s =>
    {
        var info = s.GetPlaybackInfo();
        if (info.Controls.IsNextEnabled || !info.Controls.IsPlaybackPositionEnabled)
            return await s.TrySkipNextAsync();
        var t = s.GetTimelineProperties();
        if (t.EndTime <= t.StartTime)
            return false;
        bool done = await s.TryChangePlaybackPositionAsync((t.EndTime - TimeSpan.FromSeconds(0.3)).Ticks);
        if (info.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            await s.TryPlayAsync(); // на паузе ролик так и стоял бы в самом конце
        return done;
    });

    /// <summary>Предыдущий трек; где «назад» нет (обычный ролик YouTube) — к началу трека.</summary>
    public void Previous() => Run(async s =>
    {
        var info = s.GetPlaybackInfo();
        if (info.Controls.IsPreviousEnabled || !info.Controls.IsPlaybackPositionEnabled)
            return await s.TrySkipPreviousAsync();
        return await s.TryChangePlaybackPositionAsync(s.GetTimelineProperties().StartTime.Ticks);
    });

    /// <summary>Перемотка. Позицию показываем сразу, не дожидаясь, пока плеер подтвердит.</summary>
    public void Seek(TimeSpan position)
    {
        if (_session == null || Duration <= TimeSpan.Zero)
            return;
        position = position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position;
        Position = position;
        PositionTime = DateTimeOffset.Now;
        _acceptAnyTimeline = true; // ответ плеера о новой позиции может прийти со временем чуть раньше нашего
        TimelineChanged?.Invoke();
        Run(s => s.TryChangePlaybackPositionAsync((s.GetTimelineProperties().StartTime + position).Ticks).AsTask());
    }

    private async void Run(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        if (_session is not { } session)
            return;
        try { await action(session); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _disposed = true;
        _browserTimer.Stop();
        _coverRetry.Stop();
        Unsubscribe();
        UnwatchAll();
        _session = null;
    }

    private readonly record struct Candidate(
        GlobalSystemMediaTransportControlsSession Session, MusicSource Kind, bool Browser, bool Playing, DateTimeOffset Updated);
}
