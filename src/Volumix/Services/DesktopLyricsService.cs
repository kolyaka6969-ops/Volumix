using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Volumix.Views;

namespace Volumix.Services;

/// <summary>
/// Песня и её текст на рабочем столе вместо обоев, пока играет Spotify или Яндекс Музыка (в том числе в браузере).
/// Окно работает в своём потоке: Explorer связывает очередь ввода дочернего окна со своей, и любое подвисание
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

    private readonly MediaSessionService _media;
    private readonly DispatcherTimer _fetchTimer;
    private Dispatcher? _desktop;
    // Живут в потоке окна, трогать только через Post
    private DesktopLyricsView? _view;
    private DesktopLayer? _layer;
    private CancellationTokenSource? _fetch;
    private string? _lyricsKey;
    // Ключ ролика YouTube, для которого текст нашёлся: только такие ролики и попадают на обои
    private string? _music;
    // Разобранные название и исполнитель для подтверждённой песни из YouTube
    private NowPlaying? _musicTrack;
    private bool _enabled;
    private bool _failed;

    public DesktopLyricsService(MediaSessionService media, Dispatcher dispatcher)
    {
        _media = media;
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

    private void Sync()
    {
        var track = _enabled ? _media.NowPlaying : null;
        // Обычное видео YouTube не должно висеть на обоях: пока текст не подтвердил, что это песня, прячем.
        // Обложка у YouTube берётся только из каталога песен — если она нашлась, это песня, и текст можно
        // не ждать: он подгрузится позже.
        bool allowed = track != null &&
                       (track.Source != MusicSource.YouTube || track.Key == _music || track.Cover != null);
        // Для подтверждённой песни из YouTube показываем разобранные название и исполнителя,
        // а не заголовок ролика с названием канала.
        var shown = allowed && _musicTrack != null && track!.Key == _music ? _musicTrack : track;
        if (track == null || track.Key != _music)
            _musicTrack = null;
        if (track != null || _desktop != null)
        {
            EnsureThread();
            Post(() =>
            {
                if (!allowed)
                {
                    _view!.Stop();
                    _layer!.Visible = false;
                }
                else
                {
                    _view!.ShowTrack(shown!);
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
            Lyrics? found = IsMusic(result) ? result : null;
            (string Title, string? Artist)? best = found != null ? candidates[index] : null;

            if (found == null)
            {
                // Текст мог не найтись из-за недоступной базы текстов. Тогда пускаем песню на обои,
                // если её знает каталог обложек: случайные ролики и влоги там не находятся.
                var art = await CoverArtService.FindAsync(
                    MediaSessionService.Candidates(track.Title, track.Artist).Take(4).ToList(), cts.Token);
                if (cts.IsCancellationRequested)
                    return;
                if (art != null)
                {
                    _music = track.Key;
                    _musicTrack = track;
                    Post(() =>
                    {
                        _view!.ShowTrack(track);
                        _view.ShowLyrics(track.Key, Lyrics.NotFound);
                        _layer!.Visible = true;
                    });
                    if (failed)
                    {
                        _fetchTimer.Interval = RetryDelay;
                        _fetchTimer.Start();
                    }
                    return;
                }
                Post(() =>
                {
                    _view!.Stop();
                    _layer!.Visible = false;
                });
                if (failed)
                {
                    _fetchTimer.Interval = RetryDelay;
                    _fetchTimer.Start();
                }
                return;
            }

            _music = track.Key;
            var shown = best is { } parsed
                ? track with { Title = parsed.Title, Artist = parsed.Artist ?? track.Artist }
                : track;
            _musicTrack = shown;
            Post(() =>
            {
                _view!.ShowTrack(shown);
                _view.ShowLyrics(shown.Key, found);
                _layer!.Visible = true;
            });
            return;
        }

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

    /// <summary>Текст нашёлся — значит ролик с песней, а не случайное видео.</summary>
    private static bool IsMusic(Lyrics lyrics) =>
        lyrics.Status is LyricsStatus.Synced or LyricsStatus.Plain or LyricsStatus.Instrumental;

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
        _media.Changed -= Sync;
        _media.TimelineChanged -= Sync;
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
