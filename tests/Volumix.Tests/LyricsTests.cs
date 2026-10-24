using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Volumix.Services;
using Volumix.Views;

namespace Volumix.Tests;

public class LyricsTests
{
    [Fact]
    public void ParsesLrc()
    {
        var lines = LyricsService.ParseLrc("""
            [ar:Linkin Park]
            [ti:Numb]
            [offset:+500]
            [00:21.58]I'm tired of being what you want me to be
            [00:25.6]Feeling so <00:26.10>faithless
            [01:02.123][00:30.61]Припев дважды
            [00:40.00]
            [00:41.00]
            [00:45]После паузы
            """);

        Assert.Equal(new[] { 21.08, 25.1, 30.11, 39.5, 44.5, 61.623 }, lines.Select(l => Math.Round(l.Time.TotalSeconds, 3)));
        Assert.Equal("I'm tired of being what you want me to be", lines[0].Text);
        Assert.Equal("Feeling so faithless", lines[1].Text);  // пословные метки убраны
        Assert.Equal("Припев дважды", lines[2].Text);
        Assert.Equal("", lines[3].Text);                       // две пустые подряд схлопнулись в одну паузу
        Assert.Equal("Припев дважды", lines[5].Text);
    }

    [Fact]
    public void PlainTextWithoutTimestamps()
    {
        var lyrics = Lyrics.FromLrc("[ar:Кто-то]\n\nПервая строка\n\nВторая\n");
        Assert.Equal(LyricsStatus.Plain, lyrics.Status);
        Assert.Equal(new[] { "Первая строка", "", "Вторая" }, lyrics.Lines.Select(l => l.Text));
    }

    /// <summary>Так на lrclib лежит, например, «Этою весной» nyan.mp3: весь текст одной строкой с меткой 00:00.</summary>
    [Fact]
    public void SingleTimestampIsNotSynced()
    {
        var lyrics = Lyrics.FromLrc("[00:00.00]Мяу, nyan.mp3 Просыпаюсь, не хочу вылазить из-под одеяла Комната холодная, " +
                                    "уведомления экрана Свет теплее, чем вчера, жаль, не прям тут рабочая зона Так бы никуда не шёл");
        Assert.Equal(LyricsStatus.Plain, lyrics.Status);
        Assert.Equal(new[]
        {
            "Мяу, nyan.mp3 Просыпаюсь, не хочу вылазить из-под одеяла",
            "Комната холодная, уведомления экрана",
            "Свет теплее, чем вчера, жаль, не прям тут рабочая зона",
            "Так бы никуда не шёл",
        }, lyrics.Lines.Select(l => l.Text));
    }

    [Fact]
    public void ExtrapolatesPosition()
    {
        var at = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var track = new NowPlaying("t", "a", null, null, true, TimeSpan.FromSeconds(10), at, TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(15), track.PositionAt(at.AddSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(60), track.PositionAt(at.AddMinutes(5)));          // не дальше конца трека
        Assert.Equal(TimeSpan.FromSeconds(10), (track with { IsPlaying = false }).PositionAt(at.AddSeconds(5)));
    }

    [SkippableFact]
    public async Task FindsSyncedLyricsOnLrcLib()
    {
        var lyrics = await LyricsService.FindAsync("Numb", "Linkin Park", "Meteora", TimeSpan.FromSeconds(187), CancellationToken.None);
        Skip.If(lyrics.Status == LyricsStatus.Error, "lrclib.net недоступен");

        Assert.Equal(LyricsStatus.Synced, lyrics.Status);
        Assert.Contains(lyrics.Lines, l => l.Text.Contains("numb", StringComparison.OrdinalIgnoreCase));
    }

    private const string SyncedLrc = "[00:01.00]раз\n[00:05.00]два\n[00:09.00]три";

    /// <summary>Клип длиннее трека на пару секунд: синхронный текст всё равно важнее обычного у первого прочтения.</summary>
    [Fact]
    public void VideoPrefersSyncedLyricsWithLooseDuration()
    {
        var results = new List<LyricsService.LrcLibTrack>?[]
        {
            new() { new() { Duration = 149, PlainLyrics = "раз\nдва" } },       // другая версия, только текст
            null,                                                              // запрос не удался
            new() { new() { Duration = 191, SyncedLyrics = SyncedLrc } },      // ролик 195 с, трек 191 с
        };
        var picked = LyricsService.PickVideo(results, 195);
        Assert.NotNull(picked);
        Assert.Equal(LyricsStatus.Synced, picked.Value.Lyrics.Status);
        Assert.Equal(2, picked.Value.Index);
    }

    /// <summary>Длительность не сошлась ни у кого — текст показывается, но без меток времени (иначе он бы врал).</summary>
    [Fact]
    public void VideoFallsBackToPlainWhenDurationDiffers()
    {
        var results = new List<LyricsService.LrcLibTrack>?[] { new() { new() { Duration = 150, SyncedLyrics = SyncedLrc } } };
        var picked = LyricsService.PickVideo(results, 240);
        Assert.NotNull(picked);
        Assert.Equal(LyricsStatus.Plain, picked.Value.Lyrics.Status);
        Assert.Null(LyricsService.PickVideo(new List<LyricsService.LrcLibTrack>?[] { new(), null }, 240));
    }

    [SkippableFact]
    public async Task FindsSyncedLyricsForYouTubeVideo()
    {
        // Ролик «GONE.Fludd - FATHER» на YouTube идёт 3:15, а синхронный текст на lrclib — у версии на 3:11
        var candidates = MediaSessionService.Candidates("FATHER", "GONE.Fludd").Take(4).ToList();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (lyrics, _) = await LyricsService.FindVideoAsync(candidates, TimeSpan.FromSeconds(195), CancellationToken.None);
        Skip.If(lyrics.Status == LyricsStatus.Error, "lrclib.net недоступен");

        Assert.Equal(LyricsStatus.Synced, lyrics.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"поиск занял {watch.Elapsed.TotalSeconds:N1} с");
    }

    [Fact]
    public void ParsesGeniusPage()
    {
        const string html = """
            <div id="lyrics-root"><div data-lyrics-container="true" class="Lyrics__Container">
            <div data-exclude-from-selection="true" class="LyricsHeader"><button><span>107 Contributors</span></button>
            <div class="Dropdown"><span>Translations</span><div>English</div></div></div>
            [Intro: Quavo]<br/>Yeah, <i>yeah</i><br><a href="/1"><span>One more ride, one more race</span></a><br/>
            </div><div class="Ad"></div><div data-lyrics-container="true">[Chorus]<br/>Rock &amp; roll<br/></div></div>
            """;
        Assert.Equal("Yeah, yeah\nOne more ride, one more race\n\nRock & roll", LyricsService.ParseGeniusPage(html));
        Assert.Null(LyricsService.ParseGeniusPage("<html>нет текста</html>"));
    }

    [SkippableFact]
    public async Task FindsLyricsOnGenius()
    {
        var lyrics = await LyricsService.FetchGeniusAsync("Go Off", "Lil Uzi Vert", CancellationToken.None);
        Skip.If(lyrics.Status == LyricsStatus.Error, "genius.com недоступен");

        // Сохраняем, чтобы глазами проверить, что со страницы не попало лишнего
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "volumix-genius.txt"), lyrics.Lines.Select(l => l.Text));
        Assert.Equal(LyricsStatus.Plain, lyrics.Status);
        Assert.Contains(lyrics.Lines, l => l.Text.Contains("one more ride", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lyrics.Lines, l => l.Text.Contains("Contributors") || l.Text.StartsWith("["));
        // Песня с таким же названием, но другого исполнителя — не наша
        Assert.Equal(LyricsStatus.NotFound,
            (await LyricsService.FetchGeniusAsync("Numb", "Ёлка", CancellationToken.None)).Status);
    }

    /// <summary>Рисует обои вне экрана и сохраняет картинку в %TEMP%\volumix-desktop.png, чтобы на неё посмотреть.</summary>
    [Fact]
    public void RendersDesktopView()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { Render(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }

    private static void Render()
    {
        const int width = 1536, height = 864; // 1920×1080 при масштабе 125 %
        var view = new DesktopLyricsView();
        var window = new Window
        {
            Content = view,
            Width = width,
            Height = height,
            Left = -20000,
            Top = -20000,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        window.Show();

        var now = DateTimeOffset.Now;
        var track = new NowPlaying("Numb", "Linkin Park", "Meteora", MakeCover(), true,
            TimeSpan.FromSeconds(31), now, TimeSpan.FromSeconds(186));
        view.ShowTrack(track);
        view.ShowLyrics(track.Key, Lyrics.FromLrc("""
            [00:21.58]I'm tired of being what you want me to be
            [00:25.62]Feeling so faithless, lost under the surface
            [00:30.61]Don't know what you're expecting of me
            [00:34.09]Put under the pressure of walking in your shoes
            [00:38.00]
            [00:39.31](Caught in the undertow, just caught in the undertow)
            [00:43.82]Every step that I take is another mistake to you
            [00:48.33](Caught in the undertow, just caught in the undertow)
            [00:53.00]I've become so numb, I can't feel you there
            """));

        // Даём отыграть появлению и прокрутке
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);

        var bitmap = new RenderTargetBitmap(1920, 1080, 120, 120, PixelFormats.Pbgra32);
        bitmap.Render(view);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(Path.GetTempPath(), "volumix-desktop.png")))
            encoder.Save(file);

        window.Close();
        view.Stop();
    }

    /// <summary>Яркая «обложка», чтобы было видно размытый фон.</summary>
    private static byte[] MakeCover()
    {
        using var bmp = new System.Drawing.Bitmap(300, 300);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Point(0, 0), new System.Drawing.Point(300, 300),
                   System.Drawing.Color.FromArgb(220, 60, 40), System.Drawing.Color.FromArgb(30, 60, 160)))
        {
            g.FillRectangle(brush, 0, 0, 300, 300);
            g.FillEllipse(System.Drawing.Brushes.Gold, 90, 90, 120, 120);
        }
        using var png = new MemoryStream();
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        return png.ToArray();
    }
}
