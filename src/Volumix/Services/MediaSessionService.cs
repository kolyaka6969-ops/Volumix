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
        "official", "video", "audio", "lyric", "клип", "премьер", "premiere", "официальн", "видео", "текст песни",
        "visualizer", "hd", "4k", "8k", "m/v", "mv",
    };

    private static readonly Regex Brackets = new(@"[\(\[\{][^\)\]\}]*[\)\]\}]");
    private static readonly Regex FeatTail = new(@"\s+(?:ft|feat|featuring)\.?\s.*$", RegexOptions.IgnoreCase);

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
    private int _refreshVersion;
    private bool _acceptAnyTimeline = true;
    private bool _disposed;

    public MediaSessionService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
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
        if (session != null && IsBrowser(session.SourceAppUserModelId) && _mode == MusicSourceMode.Auto)
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
            (title, artist, album) = CleanMetadata(title, artist, album, _source);

        var key = $"{artist}\n{title}\n{album}";
        bool newTrack = key != _coverKey;
        if (_source == MusicSource.YouTube)
        {
            // Браузер присылает логотип сайта или кадр ролика — на обоях это выглядит как ошибка,
            // поэтому у YouTube обложка берётся только из каталога, а до неё стоит заглушка.
            if (newTrack)
            {
                _coverKey = key;
                Cover = null;
            }
        }
        else if (newTrack || sourceChanged || (Cover == null && thumbnail != null))
        {
            // Плеер иногда присылает обложку чуть позже названия
            var cover = thumbnail == null ? null : await ReadAsync(thumbnail);
            if (_disposed || session != _session || version != _refreshVersion)
                return;
            _coverKey = key;
            Cover = cover;
        }

        // YouTube отдаёт в медиа-сеансе фавикон сайта, а на обоях нужна обложка трека — ищем её в каталогах.
        if (_source == MusicSource.YouTube && title != null && (newTrack || sourceChanged || _catalogKey != key))
        {
            var variants = Candidates(title, artist).Take(4).ToList();
            var art = await CoverArtService.FindAsync(variants);
            if (_disposed || session != _session || version != _refreshVersion)
                return;
            _catalogKey = key;
            Cover = art;
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
        Changed?.Invoke();
    }

    /// <summary>
    /// Браузеры присылают «Исполнитель — Название» одной строкой, часто с названием сайта в конце.
    /// У YouTube в названии ещё и «(Official Video)», а в исполнителе — канал. Приводим к виду,
    /// который понимают lrclib и экран на рабочем столе.
    /// </summary>
    internal static (string Title, string? Artist, string? Album) CleanMetadata(
        string title, string? artist, string? album, MusicSource source)
    {
        title = title.Trim();
        foreach (var suffix in new[]
                 {
                     " — Яндекс Музыка", " | Яндекс Музыка", " - Яндекс Музыка", " — Yandex Music", " | Yandex Music",
                     " - YouTube", " | YouTube",
                 })
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                title = title[..^suffix.Length].Trim();

        if (source is MusicSource.YouTube or MusicSource.YouTubeMusic)
        {
            title = StripVideoNoise(title);
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

    /// <summary>«Кино - Группа крови (Official Video)» → «Кино - Группа крови».</summary>
    internal static string StripVideoNoise(string title)
    {
        var cleaned = Brackets.Replace(title, match => HasVideoNoise(match.Value) ? " " : match.Value);
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim(' ', '-', '—', '–', '|');
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

    public void TogglePlayPause() => Run(s => s.TryTogglePlayPauseAsync().AsTask());
    public void Next() => Run(s => s.TrySkipNextAsync().AsTask());
    public void Previous() => Run(s => s.TrySkipPreviousAsync().AsTask());

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
        Unsubscribe();
        UnwatchAll();
        _session = null;
    }

    private readonly record struct Candidate(
        GlobalSystemMediaTransportControlsSession Session, MusicSource Kind, bool Browser, bool Playing, DateTimeOffset Updated);
}
