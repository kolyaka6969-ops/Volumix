using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Volumix.Services;

/// <summary>Что играет в Spotify. Позиция — на момент <see cref="PositionTime"/>, во время игры её досчитываем по часам.</summary>
public sealed record NowPlaying(string Title, string? Artist, string? Album, byte[]? Cover, bool IsPlaying,
    TimeSpan Position, DateTimeOffset PositionTime, TimeSpan Duration)
{
    /// <summary>Один и тот же трек, даже если сменились позиция или обложка.</summary>
    public string Key => $"{Artist}\n{Title}\n{Album}";

    public TimeSpan PositionAt(DateTimeOffset now) => SpotifyService.Extrapolate(Position, PositionTime, IsPlaying, Duration, now);
}

/// <summary>
/// Управление Spotify через системные медиа-сеансы Windows (те же, что у кнопок в панели громкости Windows).
/// События сеансов приходят из пула потоков, наружу всё отдаётся в потоке интерфейса.
/// </summary>
public sealed class SpotifyService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _coverKey;
    private int _refreshVersion;
    private bool _acceptAnyTimeline = true;
    private bool _disposed;

    public SpotifyService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool IsAvailable => _session != null;
    public bool IsPlaying { get; private set; }
    public string? Title { get; private set; }
    public string? Artist { get; private set; }
    public string? Album { get; private set; }
    /// <summary>Обложка как её отдаёт Spotify (PNG или JPEG).</summary>
    public byte[]? Cover { get; private set; }
    public TimeSpan Duration { get; private set; }
    /// <summary>Позиция на момент <see cref="PositionTime"/>; во время игры к ней прибавляется прошедшее время.</summary>
    public TimeSpan Position { get; private set; }
    public DateTimeOffset PositionTime { get; private set; }

    public NowPlaying? NowPlaying => _session == null || Title == null ? null
        : new(Title, Artist, Album, Cover, IsPlaying, Position, PositionTime, Duration);

    public event Action? Changed;

    /// <summary>Сменилась позиция в треке: перемотка или очередная сверка (Spotify присылает её раз в несколько секунд).</summary>
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

    private static bool IsSpotify(GlobalSystemMediaTransportControlsSession s) =>
        s.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase);

    private void AttachSession()
    {
        if (_disposed || _manager == null)
            return;

        GlobalSystemMediaTransportControlsSession? found;
        try { found = _manager.GetSessions().FirstOrDefault(IsSpotify); }
        catch (Exception) { found = null; }

        // Переподписываемся всегда: после перезапуска Spotify сеанс новый, хоть и с тем же именем.
        Unsubscribe();
        _session = found;
        if (_session != null)
        {
            _session.PlaybackInfoChanged += OnSessionChanged;
            _session.MediaPropertiesChanged += OnSessionChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }
        _acceptAnyTimeline = true;
        _ = RefreshAsync();
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
                // Spotify закрылся посреди запроса — следующий SessionsChanged всё поправит.
            }
        }
        // Пока ждали ответа, пришло событие поновее — его обновление и победит
        if (_disposed || session != _session || version != _refreshVersion)
            return;

        var key = $"{artist}\n{title}\n{album}";
        bool newTrack = key != _coverKey;
        // Spotify иногда присылает обложку чуть позже названия
        if (newTrack || (Cover == null && thumbnail != null))
        {
            var cover = thumbnail == null ? null : await ReadAsync(thumbnail);
            if (_disposed || session != _session || version != _refreshVersion)
                return;
            _coverKey = key;
            Cover = cover;
        }

        if (playing != IsPlaying)
        {
            // Spotify не всегда сообщает позицию при паузе и продолжении — замираем и продолжаем от своей оценки
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
        Unsubscribe();
        _session = null;
    }
}
