using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Volumix.Views;

namespace Volumix.Services;

/// <summary>
/// Песня и её текст на рабочем столе вместо обоев, пока открыт Spotify.
/// Окно работает в своём потоке: Explorer связывает очередь ввода дочернего окна со своей, и любое подвисание
/// интерфейса Volumix подвесило бы рабочий стол.
/// </summary>
public sealed class DesktopLyricsService : IDisposable
{
    /// <summary>Spotify присылает название раньше длительности, а без неё текст ищется хуже — ждём, пока всё придёт.</summary>
    private static readonly TimeSpan FetchDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly SpotifyService _spotify;
    private readonly DispatcherTimer _fetchTimer;
    private Dispatcher? _desktop;
    // Живут в потоке окна, трогать только через Post
    private DesktopLyricsView? _view;
    private DesktopLayer? _layer;
    private CancellationTokenSource? _fetch;
    private string? _lyricsKey;
    private bool _enabled;
    private bool _failed;

    public DesktopLyricsService(SpotifyService spotify, Dispatcher dispatcher)
    {
        _spotify = spotify;
        _fetchTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
        _fetchTimer.Tick += (_, _) =>
        {
            _fetchTimer.Stop();
            _ = FetchAsync();
        };
        _spotify.Changed += Sync;
        _spotify.TimelineChanged += Sync;
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

    private void Sync()
    {
        var track = _enabled ? _spotify.NowPlaying : null;
        if (track != null || _desktop != null)
        {
            EnsureThread();
            Post(() =>
            {
                if (track == null)
                {
                    _view!.Stop();
                    _layer!.Visible = false;
                }
                else
                {
                    _view!.ShowTrack(track);
                    _layer!.Visible = true;
                }
            });
        }

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

    private async Task FetchAsync()
    {
        var track = _enabled ? _spotify.NowPlaying : null;
        if (track == null)
            return;
        var cts = new CancellationTokenSource();
        _fetch = cts;
        var lyrics = await LyricsService.FindAsync(track.Title, track.Artist, track.Album, track.Duration, cts.Token);
        if (cts.IsCancellationRequested)
            return;
        Post(() => _view!.ShowLyrics(track.Key, lyrics));
        if (lyrics.Status == LyricsStatus.Error)
        {
            _fetchTimer.Interval = RetryDelay;
            _fetchTimer.Start();
        }
    }

    private void EnsureThread()
    {
        if (_desktop != null || _failed)
            return;
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
                _view = new DesktopLyricsView();
                _layer = new DesktopLayer(_view);
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
    }

    private void Post(Action action) => _desktop?.BeginInvoke(action);

    public void Dispose()
    {
        _spotify.Changed -= Sync;
        _spotify.TimelineChanged -= Sync;
        _fetchTimer.Stop();
        _fetch?.Cancel();
        if (_desktop is not { } desktop)
            return;
        try
        {
            // Окно закрываем в его потоке и дожидаемся: при выходе Explorer должен вернуть обычные обои
            desktop.Invoke(() => _layer?.Dispose(), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(2));
        }
        catch (Exception) { }
        desktop.InvokeShutdown();
    }
}
