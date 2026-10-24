using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Volumix.Services;

/// <summary>
/// Управление Spotify через системные медиа-сеансы Windows (те же, что у кнопок в панели громкости Windows).
/// События сеансов приходят из пула потоков, наружу всё отдаётся в потоке интерфейса.
/// </summary>
public sealed class SpotifyService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    public SpotifyService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool IsAvailable => _session != null;
    public bool IsPlaying { get; private set; }
    public string? Title { get; private set; }
    public string? Artist { get; private set; }

    public event Action? Changed;

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
        if (_session != null)
        {
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.MediaPropertiesChanged -= OnSessionChanged;
        }
        _session = found;
        if (_session != null)
        {
            _session.PlaybackInfoChanged += OnSessionChanged;
            _session.MediaPropertiesChanged += OnSessionChanged;
        }
        _ = RefreshAsync();
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _dispatcher.BeginInvoke(() => _ = RefreshAsync());

    private async Task RefreshAsync()
    {
        var session = _session;
        bool playing = false;
        string? title = null, artist = null;
        if (session != null)
        {
            try
            {
                playing = session.GetPlaybackInfo()?.PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var props = await session.TryGetMediaPropertiesAsync();
                title = string.IsNullOrWhiteSpace(props?.Title) ? null : props.Title;
                artist = string.IsNullOrWhiteSpace(props?.Artist) ? null : props.Artist;
            }
            catch (Exception)
            {
                // Spotify закрылся посреди запроса — следующий SessionsChanged всё поправит.
            }
        }
        if (_disposed || session != _session)
            return;

        IsPlaying = playing;
        Title = title;
        Artist = artist;
        Changed?.Invoke();
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
        if (_session != null)
        {
            _session.PlaybackInfoChanged -= OnSessionChanged;
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session = null;
        }
    }
}
