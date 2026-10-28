using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Volumix.Views;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Volumix.Services;

/// <summary>Что показывает островок. Позиция — на момент <see cref="PositionTime"/>, во время игры досчитывается по часам.</summary>
public sealed record IslandTrack(string Title, string? Subtitle, BitmapSource? Cover, bool IsVideo, bool IsPlaying,
    TimeSpan Position, DateTimeOffset PositionTime, TimeSpan Duration, bool CanPrevious, bool CanNext, bool CanSeek)
{
    public TimeSpan PositionAt(DateTimeOffset now) => MediaSessionService.Extrapolate(Position, PositionTime, IsPlaying, Duration, now);
}

/// <summary>
/// Островок вверху экрана: что играет, сколько прошло и кнопки плеера. В отличие от обоев с текстом здесь любой
/// плеер — музыка, ролики, фильмы: всё, что Windows показывает у себя в панели громкости. Пока на переднем
/// плане игра или окно на весь экран, островок спрятан.
/// </summary>
public sealed class MediaIslandService : IDisposable
{
    /// <summary>Как часто двигаем полосу прогресса и проверяем, не запущена ли игра.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(500);
    /// <summary>Шаг громкости колёсиком мыши над островком.</summary>
    private const double VolumeStep = 2;

    private readonly Dispatcher _dispatcher;
    private readonly MediaSessionService? _media;
    private readonly DispatcherTimer _tick;
    private readonly GameDetector _games = new();
    private readonly NativeMethods.WinEventProc _foregroundProc;
    private IntPtr _foregroundHook;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private MediaIslandWindow? _window;
    private IslandTrack? _track;
    private string? _coverKey;
    private BitmapSource? _cover;
    private bool _coverFromMedia;
    private DateTimeOffset? _pausedSince;
    private int _refreshVersion;
    private bool _enabled;
    private TimeSpan? _pausedLimit = TimeSpan.FromSeconds(AppSettings.DefaultIslandPauseHide);
    private double _width = MediaIslandWindow.DefaultIslandWidth;
    private double _scale = 1;
    private double _taskbarWidth = MediaIslandWindow.DefaultTaskbarWidth;
    private IslandPlacement _placement = IslandPlacement.Default;
    private bool _docked;
    private TimeSpan? _autoHide;
    private bool _started;
    private bool _disposed;

    public MediaIslandService(Dispatcher dispatcher, MediaSessionService? media = null)
    {
        _dispatcher = dispatcher;
        _media = media;
        _tick = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TickInterval };
        _tick.Tick += (_, _) => OnTick();
        // Переключились в игру — прячемся сразу, не дожидаясь таймера
        _foregroundProc = (_, _, _, _, _, _, _) => _dispatcher.BeginInvoke(UpdateVisibility);
        if (_media != null)
            _media.Changed += OnMediaChanged;
    }

    private void OnMediaChanged()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _track == null || _session == null)
                return;
            if ((_cover == null || _coverFromMedia) && _media?.Cover != null)
            {
                if (SameSession(_session, _media.Session) ||
                    (!string.IsNullOrWhiteSpace(_media.Title) && !string.IsNullOrWhiteSpace(_track.Title) &&
                     (_track.Title.Contains(_media.Title, StringComparison.OrdinalIgnoreCase) ||
                      _media.Title.Contains(_track.Title, StringComparison.OrdinalIgnoreCase))))
                {
                    var cover = DecodeCover(_media.Cover);
                    if (cover != null)
                    {
                        _cover = cover;
                        _coverFromMedia = true;
                        _track = _track with { Cover = cover };
                        Window().ShowTrack(_track);
                    }
                }
            }
        });
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            if (value)
                Start();
            else
                Stop();
        }
    }

    /// <summary>
    /// Сколько островок висит на паузе; null — не прятать вовсе. Браузер держит сеанс закрытого ролика,
    /// и без предела островок с ним не уходил бы никогда.
    /// </summary>
    public TimeSpan? PausedLimit
    {
        get => _pausedLimit;
        set
        {
            if (_pausedLimit == value)
                return;
            _pausedLimit = value;
            UpdateVisibility();
        }
    }

    /// <summary>Ширина островка в точках, без учёта масштаба.</summary>
    public double IslandWidth
    {
        get => _width;
        set
        {
            _width = value;
            if (_window != null)
                _window.IslandWidth = value;
        }
    }

    public double IslandScale
    {
        get => _scale;
        set
        {
            _scale = value;
            if (_window != null)
                _window.IslandScale = value;
        }
    }

    /// <summary>Ширина островка на панели задач.</summary>
    public double TaskbarWidth
    {
        get => _taskbarWidth;
        set
        {
            _taskbarWidth = value;
            if (_window != null)
                _window.TaskbarWidth = value;
        }
    }

    /// <summary>Где стоит островок: место на экране или на панели задач.</summary>
    public IslandPlacement Placement
    {
        get => _placement;
        set
        {
            _placement = value;
            _window?.SetPlacement(value);
        }
    }

    /// <summary>Островок спрятан в край экрана и выезжает, когда к краю подводят курсор.</summary>
    public bool Docked
    {
        get => _docked;
        set
        {
            _docked = value;
            if (_window != null)
                _window.Docked = value;
        }
    }

    /// <summary>Через сколько островок сам прячется в край; null — не прячется.</summary>
    public TimeSpan? AutoHide
    {
        get => _autoHide;
        set
        {
            _autoHide = value;
            if (_window != null)
                _window.AutoHide = value;
        }
    }

    /// <summary>Островок растянули за край: ширина и масштаб на экране и ширина на панели задач — их стоит запомнить.</summary>
    public event Action<double, double, double>? Resized;

    /// <summary>Островок передвинули: на экране или на панель задач.</summary>
    public event Action<IslandPlacement>? Moved;

    /// <summary>Островок спрятали в край или достали жестом либо из меню.</summary>
    public event Action<bool>? DockedChanged;

    private async void Start()
    {
        if (_foregroundHook == IntPtr.Zero)
            _foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _foregroundProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _tick.Start();
        if (_started)
        {
            Attach();
            return;
        }
        _started = true;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed)
                return;
            _manager.SessionsChanged += (_, _) => _dispatcher.BeginInvoke(Attach);
            _manager.CurrentSessionChanged += (_, _) => _dispatcher.BeginInvoke(Attach);
            Attach();
        }
        catch (Exception)
        {
            // Медиа-сеансы недоступны (урезанная сборка Windows) — островка не будет.
        }
    }

    private void Stop()
    {
        _tick.Stop();
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }
        SetSession(null);
        _track = null;
        _window?.HideIsland();
    }

    /// <summary>
    /// Сеанс, который показать: тот, что играет, а из играющих — выбранный Windows (его же она показывает в панели
    /// громкости). Если не играет ничего, остаёмся на прежнем — чтобы с островка можно было снять паузу.
    /// </summary>
    private void Attach() => Attach(true);

    /// <param name="refresh">Перечитать сведения о треке, даже если сеанс тот же.</param>
    private void Attach(bool refresh)
    {
        if (!_enabled || _disposed || _manager == null)
            return;
        GlobalSystemMediaTransportControlsSession? best = null;
        try
        {
            var sessions = _manager.GetSessions();
            var current = _manager.GetCurrentSession();
            var playing = sessions.Where(IsPlaying).ToList();
            best = playing.FirstOrDefault(s => SameSession(s, current))
                   ?? playing.FirstOrDefault(s => SameSession(s, _session))
                   ?? playing.FirstOrDefault()
                   ?? sessions.FirstOrDefault(s => SameSession(s, _session))
                   ?? current;
        }
        catch (Exception)
        {
            // Сеанс закрылся посреди перебора — следующее событие всё поправит.
        }
        if (!SameSession(best, _session))
            SetSession(best);
        else if (!refresh)
            return;
        _ = RefreshAsync();
    }

    private static bool SameSession(GlobalSystemMediaTransportControlsSession? a, GlobalSystemMediaTransportControlsSession? b) =>
        a != null && b != null && (ReferenceEquals(a, b) || a.SourceAppUserModelId == b.SourceAppUserModelId);

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SetSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session != null)
        {
            try
            {
                _session.PlaybackInfoChanged -= OnSessionChanged;
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.TimelinePropertiesChanged -= OnSessionChanged;
            }
            catch (Exception) { }
        }
        _session = session;
        _coverKey = null;
        _cover = null;
        if (session == null)
            return;
        try
        {
            session.PlaybackInfoChanged += OnSessionChanged;
            session.MediaPropertiesChanged += OnSessionChanged;
            session.TimelinePropertiesChanged += OnSessionChanged;
        }
        catch (Exception) { }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _dispatcher.BeginInvoke(() =>
        {
            // Поставили на паузу — вдруг заиграл другой плеер; заиграло — вдруг это уже не тот сеанс
            if (args is PlaybackInfoChangedEventArgs)
                Attach();
            else
                _ = RefreshAsync();
        });

    private async Task RefreshAsync()
    {
        var session = _session;
        int version = ++_refreshVersion;
        if (session == null)
        {
            _track = null;
            UpdateVisibility();
            return;
        }

        IslandTrack? track = null;
        try
        {
            var info = session.GetPlaybackInfo();
            var props = await session.TryGetMediaPropertiesAsync();
            if (_disposed || session != _session || version != _refreshVersion)
                return;
            var title = props?.Title?.Trim();
            if (!string.IsNullOrEmpty(title))
            {
                bool video = props!.PlaybackType == MediaPlaybackType.Video || info?.PlaybackType == MediaPlaybackType.Video;
                var subtitle = !string.IsNullOrWhiteSpace(props.Artist) ? props.Artist.Trim() : AppName(session.SourceAppUserModelId);

                var key = $"{session.SourceAppUserModelId}\n{props.Artist}\n{title}\n{props.AlbumTitle}";
                if (key != _coverKey || _cover == null)
                {
                    _coverFromMedia = false;
                    var cover = props.Thumbnail == null ? null : await ReadCoverAsync(props.Thumbnail);
                    if (_disposed || session != _session || version != _refreshVersion)
                        return;

                    // Браузер прислал значок программы вместо обложки или не прислал ничего — пробуем взять обложку у MediaSessionService
                    if (cover == null && _media?.Cover != null)
                    {
                        if (SameSession(session, _media.Session) ||
                            (!string.IsNullOrWhiteSpace(_media.Title) && (title.Contains(_media.Title, StringComparison.OrdinalIgnoreCase) || _media.Title.Contains(title, StringComparison.OrdinalIgnoreCase))))
                        {
                            cover = DecodeCover(_media.Cover);
                            if (cover != null)
                                _coverFromMedia = true;
                        }
                    }

                    _coverKey = key;
                    _cover = cover;

                    // Если обложки всё ещё нет (видео или трек в браузере), ищем обложку в фоне
                    if (_cover == null)
                    {
                        _ = ResolveCoverAsync(session, title, props.Artist, version);
                    }
                }

                var timeline = session.GetTimelineProperties();
                var duration = timeline.EndTime - timeline.StartTime;
                var at = timeline.LastUpdatedTime;
                var now = DateTimeOffset.Now;
                if (at > now || at.Year < 2000)
                    at = now;
                bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var controls = info?.Controls;
                track = new IslandTrack(title, subtitle, _cover, video, playing,
                    timeline.Position - timeline.StartTime, at, duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
                    controls?.IsPreviousEnabled ?? true, controls?.IsNextEnabled ?? true,
                    controls?.IsPlaybackPositionEnabled == true && duration > TimeSpan.Zero);
            }
        }
        catch (Exception)
        {
            // Плеер закрылся посреди запроса — следующий SessionsChanged всё поправит.
        }
        if (_disposed || session != _session || version != _refreshVersion)
            return;

        // Пауза одной и той же позиции не сбивает счётчик: плеер присылает события и на паузе
        if (track is { IsPlaying: false })
            _pausedSince ??= DateTimeOffset.Now;
        else
            _pausedSince = null;
        _track = track;
        if (track != null)
            Window().ShowTrack(track);
        UpdateVisibility();
    }

    private int _idleTicks;

    private void OnTick()
    {
        // Ничего не играет, а сеанс на паузе — проверим, не заиграл ли другой плеер (раз в 2.5 сек)
        if (_track is not { IsPlaying: true })
        {
            if (++_idleTicks % 5 == 0)
                Attach(false);
        }
        else
        {
            _idleTicks = 0;
        }

        UpdateVisibility();
        if (_window is { IsShown: true } window && _track != null)
            window.UpdateProgress(_track.PositionAt(DateTimeOffset.Now));
    }

    private void UpdateVisibility()
    {
        if (_disposed)
            return;
        bool show = _enabled && _track != null &&
                    !(_pausedSince is { } since && _pausedLimit is { } limit && DateTimeOffset.Now - since > limit) &&
                    !_games.IsGameInFront();
        if (show)
        {
            Window().ShowIsland();
            // Сменилось активное окно — возможно, щёлкнули по панели задач, и она перекрыла островок
            _window!.KeepOnTop();
        }
        else
            _window?.HideIsland();
    }

    private MediaIslandWindow Window()
    {
        if (_window != null)
            return _window;
        _window = new MediaIslandWindow
        {
            IslandWidth = _width,
            IslandScale = _scale,
            TaskbarWidth = _taskbarWidth,
            Docked = _docked,
            AutoHide = _autoHide,
        };
        _window.SetPlacement(_placement);
        // Островок, вшитый в панель задач, — её дочернее окно: перезапуск Проводника уничтожает его вместе с панелью.
        // Тогда создаём островок заново, он встанет в новую панель
        _window.Closed += (_, _) =>
        {
            _window = null;
            if (_disposed)
                return;
            if (_track != null)
                Window().ShowTrack(_track);
            UpdateVisibility();
        };
        _window.Resized += (width, scale, taskbarWidth) =>
        {
            (_width, _scale, _taskbarWidth) = (width, scale, taskbarWidth);
            Resized?.Invoke(width, scale, taskbarWidth);
        };
        _window.Moved += placement =>
        {
            _placement = placement;
            Moved?.Invoke(placement);
        };
        _window.DockedChanged += docked =>
        {
            _docked = docked;
            DockedChanged?.Invoke(docked);
        };
        _window.Previous += () => Run(async s =>
        {
            var info = s.GetPlaybackInfo();
            if (info.Controls.IsPreviousEnabled || !info.Controls.IsPlaybackPositionEnabled)
                return await s.TrySkipPreviousAsync();
            return await s.TryChangePlaybackPositionAsync(s.GetTimelineProperties().StartTime.Ticks);
        });
        _window.PlayPause += () => Run(s => s.TryTogglePlayPauseAsync().AsTask());
        _window.Next += () => Run(s => s.TrySkipNextAsync().AsTask());
        _window.Seek += fraction =>
        {
            if (_track is not { CanSeek: true } track)
                return;
            var position = track.Duration * Math.Clamp(fraction, 0, 1);
            // Позицию показываем сразу, не дожидаясь, пока плеер подтвердит
            _track = track with { Position = position, PositionTime = DateTimeOffset.Now };
            _window!.UpdateProgress(position);
            Run(s => s.TryChangePlaybackPositionAsync((s.GetTimelineProperties().StartTime + position).Ticks).AsTask());
        };
        _window.Wheel += up => ChangeVolume(up);
        return _window;
    }

    /// <summary>Громкость плеера колёсиком мыши — через микшер, если плеер в нём нашёлся.</summary>
    private void ChangeVolume(bool up)
    {
        if (_session == null || _window == null)
            return;
        App.Mixer.Refresh();
        var app = App.Mixer.FindApp(ProcessKey(_session.SourceAppUserModelId));
        if (app == null)
            return;
        if (up)
            app.IsMuted = false;
        app.Volume += up ? VolumeStep : -VolumeStep;
        _window.ShowVolume(app.IsMuted ? 0 : app.Volume);
    }

    /// <summary>Имя exe плеера по AUMID: «Spotify.exe» → «Spotify», «MSEdge» → «MSEdge», «C:\…\vlc.exe» → «vlc».</summary>
    internal static string? ProcessKey(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
            return null;
        // Приложение из Microsoft Store: «Издатель.Имя_хеш!App» — exe по нему не узнать
        if (aumid.Contains('!'))
            return null;
        var name = aumid.Contains('\\') || aumid.Contains('/') ? Path.GetFileName(aumid) : aumid;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Имя плеера вместо исполнителя: у фильмов и роликов его часто нет.</summary>
    internal static string? AppName(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
            return null;
        try
        {
            var info = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid);
            var name = info?.DisplayInfo?.DisplayName;
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch (Exception)
        {
            // Не приложение из магазина — имя возьмём из AUMID
        }
        var key = ProcessKey(aumid);
        if (key == null)
            return null;
        return key.ToLowerInvariant() switch
        {
            "chrome" => "Google Chrome",
            "msedge" => "Microsoft Edge",
            "firefox" or "308046b0af4a39cb" => "Firefox",
            "opera" => "Opera",
            "brave" => "Brave",
            "vivaldi" => "Vivaldi",
            "browser" or "yandexbrowser" => "Яндекс Браузер",
            "vlc" => "VLC",
            _ => key,
        };
    }

    private static async Task<BitmapSource?> ReadCoverAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 16 * 1024 * 1024)
                return null;
            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }
            if (MediaSessionService.IsAppIcon(bytes))
                return null;
            return DecodeCover(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task ResolveCoverAsync(GlobalSystemMediaTransportControlsSession session, string title, string? artist, int version)
    {
        try
        {
            // 1. Поиск по музыкальным каталогам (Deezer, iTunes, Genius)
            var candidates = MediaSessionService.Candidates(title, artist).Take(3).ToList();
            var (art, _) = await CoverArtService.LookUpAsync(candidates);
            if (_disposed || session != _session || version != _refreshVersion)
                return;

            // 2. Если в каталогах нет, а это браузер или видеоролик — берём кадр ролика с YouTube
            if (art == null && (MediaSessionService.IsBrowser(session.SourceAppUserModelId) || _track?.IsVideo == true))
            {
                var cleanTitle = MediaSessionService.VideoTitle(title);
                var channel = MediaSessionService.CleanChannel(artist);
                art = await YouTubeService.FindCoverAsync(cleanTitle, channel, null);
                if (_disposed || session != _session || version != _refreshVersion)
                    return;
            }

            if (art != null)
            {
                var bitmap = DecodeCover(art);
                if (bitmap != null && _track != null && session == _session && version == _refreshVersion)
                {
                    _cover = bitmap;
                    _coverFromMedia = false;
                    _track = _track with { Cover = bitmap };
                    Window().ShowTrack(_track);
                }
            }
        }
        catch (Exception)
        {
            // Фоновая подгрузка обложки не должна ронять островок
        }
    }

    private static BitmapSource? DecodeCover(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
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
        if (_disposed)
            return;
        if (_media != null)
            _media.Changed -= OnMediaChanged;
        Stop();
        _disposed = true;
        _window?.Close();
    }
}
