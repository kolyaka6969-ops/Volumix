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
        Skip.If(lyrics.Status is LyricsStatus.Error or LyricsStatus.Plain, "lrclib.net недоступен или перегружен");

        Assert.Equal(LyricsStatus.Synced, lyrics.Status);
        Skip.If(watch.Elapsed >= TimeSpan.FromSeconds(10), $"поиск занял {watch.Elapsed.TotalSeconds:N1} с (задержка сети)");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"поиск занял {watch.Elapsed.TotalSeconds:N1} с");
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

    [Fact]
    public void SecondTierPrefersSyncedNetEase()
    {
        var synced = Lyrics.FromLrc(SyncedLrc);
        var plain = Lyrics.FromPlain("раз\nдва");
        Assert.Same(synced, LyricsService.PickSecondTier(plain, synced));
        Assert.Same(plain, LyricsService.PickSecondTier(plain, Lyrics.FromPlain("другой")));
        Assert.Equal(LyricsStatus.Plain, LyricsService.PickSecondTier(Lyrics.Error, plain).Status);
        Assert.Equal(LyricsStatus.NotFound, LyricsService.PickSecondTier(Lyrics.Error, Lyrics.NotFound).Status);
        Assert.Equal(LyricsStatus.NotFound, LyricsService.PickSecondTier(Lyrics.Error, Lyrics.Error).Status);
        Assert.Equal(LyricsStatus.Error, LyricsService.PickSecondTier(Lyrics.Error, Lyrics.Error, Lyrics.Error).Status);
        // Русский сайт важнее текста NetEase без меток
        var site = Lyrics.FromPlain("с сайта");
        Assert.Same(site, LyricsService.PickSecondTier(Lyrics.NotFound, plain, site));
    }

    [Fact]
    public void ParsesTextLyricsPage()
    {
        const string html = """
            <nav>меню</nav><div id="entry_content" style="font-size:18px;">[Текст песни «Прощаться»]<br /><br />Когда затянутся раны неба<br />И&nbsp;злая гроза погибнет<br /><br /><div id="ad"></div>Мой храм падет</div><div id="fixed_cblock"></div>
            """;
        Assert.Equal("Когда затянутся раны неба\nИ злая гроза погибнет\n\nМой храм падет", LyricsService.ParseTextLyricsPage(html));
        Assert.Null(LyricsService.ParseTextLyricsPage("<html></html>"));
    }

    [SkippableFact]
    public async Task FindsRussianLyricsOnTextLyrics()
    {
        var lyrics = await LyricsService.FetchTextLyricsAsync("Прощаться", "Грель", CancellationToken.None);
        Skip.If(lyrics.Status == LyricsStatus.Error, "text-lyrics.ru недоступен");

        Assert.Equal(LyricsStatus.Plain, lyrics.Status);
        Assert.Equal("Когда затянутся раны неба", lyrics.Lines[0].Text);
        Assert.Contains(lyrics.Lines, l => l.Text == "Я привыкла прощаться с тобой");
        Assert.Equal(LyricsStatus.NotFound,
            (await LyricsService.FetchTextLyricsAsync("Прощаться", "Абракадабра Несуществующая", CancellationToken.None)).Status);

        // Весь путь: первая очередь проверяет text-lyrics.ru параллельно для русских песен
        var found = await LyricsService.FindAsync("Прощаться", "Грель", null, TimeSpan.FromSeconds(183), CancellationToken.None);
        Assert.Equal(LyricsStatus.Plain, found.Status);
        Assert.Equal("Когда затянутся раны неба", found.Lines[0].Text);
    }

    [SkippableFact]
    public async Task RussianTrackSearchedQuickly()
    {
        LyricsCache.Folder = Path.Combine(Path.GetTempPath(), "volumix-tests-" + Guid.NewGuid().ToString("N"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lyrics = await LyricsService.FindAsync("Прощаться", "Грель", null, TimeSpan.FromSeconds(183), CancellationToken.None);
        sw.Stop();
        Skip.If(lyrics.Status == LyricsStatus.Error, "Сервис недоступен");

        Assert.Equal(LyricsStatus.Plain, lyrics.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3.5), $"Поиск занял слишком много времени: {sw.Elapsed.TotalSeconds:N1} с");
    }

    [SkippableFact]
    public async Task FindsSyncedLyricsOnNetEase()
    {
        var lyrics = await LyricsService.FetchNetEaseAsync("Numb", "Linkin Park", 187, 3, CancellationToken.None);
        Skip.If(lyrics.Status == LyricsStatus.Error, "music.163.com недоступен");

        Assert.Equal(LyricsStatus.Synced, lyrics.Status);
        // Строки «作词 : …» и «作曲 : …» с авторами отброшены — первой идёт песня
        // Неразрывные пробелы NetEase заменены обычными, иначе строка не переносится
        Assert.Equal("I'm tired of being what you want me to be", lyrics.Lines[0].Text);
        Assert.DoesNotContain(lyrics.Lines, l => l.Text.Contains("作词") || l.Text.Contains("作曲"));
        // Длительность не та (live, клип) — текст есть, но без меток, иначе он бы врал
        Assert.Equal(LyricsStatus.Plain,
            (await LyricsService.FetchNetEaseAsync("Numb", "Linkin Park", 260, 3, CancellationToken.None)).Status);
        Assert.Equal(LyricsStatus.Instrumental,
            (await LyricsService.FetchNetEaseAsync("River Flows In You", "Yiruma", 0, 3, CancellationToken.None)).Status);
        Assert.Equal(LyricsStatus.NotFound,
            (await LyricsService.FetchNetEaseAsync("Numb", "Ёлка", 187, 3, CancellationToken.None)).Status);
    }

    /// <summary>Рисует обои вне экрана и сохраняет картинку в %TEMP%\volumix-desktop.png, чтобы на неё посмотреть.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RendersDesktopView(bool video)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { Render(video); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }

    /// <param name="video">Клип без текста: кадр 16:9 крупно вместо текста.</param>
    private static void Render(bool video)
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
        var track = video
            ? new NowPlaying("Numb", "Linkin Park", null, MakeCover(480, 270), true, TimeSpan.FromSeconds(31), now,
                TimeSpan.FromSeconds(214), MusicSource.YouTube, "Linkin Park - Numb (Fan Made Video)", CoverIsFrame: true)
            : new NowPlaying("Numb", "Linkin Park", "Meteora", MakeCover(300, 300), true,
                TimeSpan.FromSeconds(31), now, TimeSpan.FromSeconds(186));
        view.ShowTrack(track);
        view.ShowLyrics(track.Key, video ? Lyrics.NotFound : Lyrics.FromLrc("""
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
        using (var file = File.Create(Path.Combine(Path.GetTempPath(), video ? "volumix-desktop-video.png" : "volumix-desktop.png")))
            encoder.Save(file);

        window.Close();
        view.Stop();
    }

    /// <summary>
    /// Короткое название стоит на месте и без затухания по краям, даже если до него играла песня с длинным;
    /// длинное — едет. Проверяется по живому окну: ширина текста известна только после раскладки.
    /// </summary>
    [Fact]
    public void ShortTitleStaysStill()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { CheckMarquee(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }

    private static void CheckMarquee()
    {
        var view = new DesktopLyricsView();
        var window = new Window
        {
            Content = view, Width = 1536, Height = 864, Left = -20000, Top = -20000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        var now = DateTimeOffset.Now;
        NowPlaying Track(string title, string artist) =>
            new(title, artist, null, null, true, TimeSpan.Zero, now, TimeSpan.FromSeconds(200));
        var titleBox = (FrameworkElement)view.TitleText.Parent;

        // Чуть длиннее поля: прокрутка займёт несколько секунд и закончится уже после смены трека
        view.ShowTrack(Track("Nowadays (feat. Landon)", "Lil Skies"));
        // Пауза перед прокруткой 2,5 с; под нагрузкой параллельных тестов кадры приходят позже
        for (int i = 0; i < 80 && view.TitleShift.X > -1; i++)
            Pump(0.1);
        Assert.True(view.TitleShift.X < -1, $"длинное название не поехало: X = {view.TitleShift.X}");

        view.ShowTrack(Track("Nowadays", "Lil Skies"));
        Pump(0.1);
        Assert.Equal(0, view.TitleShift.X);
        Assert.Null(titleBox.OpacityMask);
        for (int i = 0; i < 10; i++)
        {
            Pump(1);
            Assert.Equal(0, view.TitleShift.X);
            Assert.Null(titleBox.OpacityMask);
        }

        // Хвост «y» не должен срезаться: ниже поля названия (58 точек) в столбце текста есть его пиксели
        var page = view.Page;
        var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var origin = titleBox.TranslatePoint(new System.Windows.Point(0, 0), page);
        var pixels = new byte[440 * 12 * 4];
        bitmap.CopyPixels(new Int32Rect((int)origin.X, (int)origin.Y + 58, 440, 12), pixels, 440 * 4, 0);
        var background = new byte[440 * 12 * 4];
        bitmap.CopyPixels(new Int32Rect((int)origin.X, (int)origin.Y + 58 + 30, 440, 12), background, 440 * 4, 0);
        Assert.True(pixels.Max() > background.Max() + 40, "хвост буквы срезан");

        window.Close();
        view.Stop();
    }

    /// <summary>Скорость из настроек меняет прокрутку, в том числе посреди пути.</summary>
    [Fact]
    public void MarqueeSpeedIsAdjustable()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { CheckMarqueeSpeed(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }

    private static void CheckMarqueeSpeed()
    {
        // Сколько уехало за полторы секунды после начала прокрутки
        double Shift(double speed, double? switchTo = null)
        {
            var view = new DesktopLyricsView { MarqueeSpeed = speed };
            var window = new Window
            {
                Content = view, Width = 1536, Height = 864, Left = -20000, Top = -20000,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            view.ShowTrack(new NowPlaying("Nowadays (feat. Landon Cube)", "Lil Skies", null, null, true,
                TimeSpan.Zero, DateTimeOffset.Now, TimeSpan.FromSeconds(200)));
            for (int i = 0; i < 80 && view.TitleShift.X > -0.01; i++)
                Pump(0.1);
            if (switchTo is { } fast)
                view.MarqueeSpeed = fast;
            Pump(1.5);
            double x = -view.TitleShift.X;
            window.Close();
            view.Stop();
            return x;
        }

        double slow = Shift(5), fast = Shift(100), switched = Shift(5, 100);
        Assert.True(fast > 50, $"быстрая строка уехала на {fast:N1}");
        Assert.True(slow < fast / 5, $"медленная {slow:N1}, быстрая {fast:N1}");
        Assert.True(switched > 50, $"после смены скорости уехала на {switched:N1}");

        // Сколько раз за секунду прокрутки строка сдвинулась — столько кадров анимации
        int Frames(int fps)
        {
            var view = new DesktopLyricsView { MarqueeSpeed = 20, MarqueeFps = fps };
            var window = new Window
            {
                Content = view, Width = 1536, Height = 864, Left = -20000, Top = -20000,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            view.ShowTrack(new NowPlaying("Nowadays (feat. Landon Cube) Remix Edition", "Lil Skies", null, null, true,
                TimeSpan.Zero, DateTimeOffset.Now, TimeSpan.FromSeconds(200)));
            for (int i = 0; i < 80 && view.TitleShift.X > -0.01; i++)
                Pump(0.1);
            var seen = new HashSet<double>();
            EventHandler sample = (_, _) => seen.Add(view.TitleShift.X);
            System.Windows.Media.CompositionTarget.Rendering += sample;
            Pump(1);
            System.Windows.Media.CompositionTarget.Rendering -= sample;
            window.Close();
            view.Stop();
            return seen.Count;
        }

        int low = Frames(15), high = Frames(60);
        Assert.True(low <= 20, $"при 15 FPS сдвигов за секунду: {low}");
        if (high >= 25)
            Assert.True(high > low * 2, $"15 FPS — {low} сдвигов, 60 FPS — {high}");
    }

    // Запись lrclib «The 7th Element — Vitas»: транслит, и куплет уложен в три секунды
    private const string BrokenTranslitLrc =
        "[00:29.18] Ya prishol daty etu piesnyu\n[00:36.34] Ya prishol daty etu piesnyu\n[00:58.99] Iz mira gryoz.\n" +
        "[00:59.43] Ya prishol daty etu piesnyu\n[01:00.18] Iz khrustalynih slyoz.\n[01:00.81] Ya prishol daty etu piesnyu\n" +
        "[01:01.46] Dlya lyubvi.\n[01:02.15] Ya prishol daty etu piesnyu.";

    private const string CyrillicLrc =
        "[00:15.70] Я пришёл дать эту песню\n[00:22.85] Я пришёл дать эту песню\n[01:44.41] Из мира грёз\n[01:48.71] Из хрустальных слёз";

    [Fact]
    public void DetectsTransliteration()
    {
        Assert.True(LyricsService.IsTransliteration(new[] { "Ya prishol daty etu piesnyu", "Iz mira gryoz.", "Iz khrustalynih slyoz.", "Dlya lyubvi." }));
        Assert.True(LyricsService.IsTransliteration(new[] { "Zhit' kak khochetsya, tebya lyublyu", "Ya ne znayu, chto skazat'" }));
        Assert.False(LyricsService.IsTransliteration(new[] { "I'm tired of being what you want me to be", "Feeling so faithless, lost under the surface" }));
        Assert.False(LyricsService.IsTransliteration(new[] { "Yo te quiero, ya lo sé", "Cuando la noche llega a mi corazón", "No puedo vivir sin ti, mi amor" }));
        Assert.False(LyricsService.IsTransliteration(new[] { "Я пришёл дать эту песню", "Ya" }));
    }

    [Fact]
    public void DetectsBrokenSync()
    {
        Assert.True(LyricsService.IsBrokenSync(LyricsService.ParseLrc(BrokenTranslitLrc)));
        Assert.False(LyricsService.IsBrokenSync(LyricsService.ParseLrc(CyrillicLrc)));
        // Короткие паузы (пустые строки) — не битые метки
        Assert.False(LyricsService.IsBrokenSync(LyricsService.ParseLrc("[00:10.00]раз\n[00:12.00]\n[00:12.30]два\n[00:12.60]\n[00:15.00]три")));
    }

    /// <summary>Транслит с битыми метками у первого прочтения проигрывает синхронному тексту на кириллице у второго.</summary>
    [Fact]
    public void VideoPrefersCyrillicOverBrokenTransliteration()
    {
        var results = new List<LyricsService.LrcLibTrack>?[]
        {
            new() { new() { Duration = 250, SyncedLyrics = BrokenTranslitLrc } },
            new() { new() { Duration = 248, SyncedLyrics = CyrillicLrc } },
        };
        var picked = LyricsService.PickVideo(results, 249);
        Assert.NotNull(picked);
        Assert.Equal(1, picked.Value.Index);
        Assert.Equal(LyricsStatus.Synced, picked.Value.Lyrics.Status);
        Assert.Equal("Я пришёл дать эту песню", picked.Value.Lyrics.Lines[0].Text);
    }

    /// <summary>Есть только транслит с битыми метками — показываем его как есть, но без меток, а не со скачущими строками.</summary>
    [Fact]
    public void BrokenSyncShownAsPlain()
    {
        var results = new List<LyricsService.LrcLibTrack>?[] { new() { new() { Duration = 250, SyncedLyrics = BrokenTranslitLrc } } };
        var picked = LyricsService.PickVideo(results, 249);
        Assert.NotNull(picked);
        Assert.Equal(LyricsStatus.Plain, picked.Value.Lyrics.Status);
        Assert.Equal("Ya prishol daty etu piesnyu", picked.Value.Lyrics.Lines[0].Text);
        Assert.True(LyricsService.Rank(picked.Value.Lyrics) >= LyricsService.TranslitPenalty);
    }

    [Fact]
    public void SearchVariantsDropLeadingArticle()
    {
        var variants = LyricsService.Variants("The 7th Element", "VITAS");
        Assert.Contains(("7th Element", (string?)"VITAS"), variants);
        Assert.Equal(variants.Count, variants.Distinct().Count());
        Assert.Single(LyricsService.Variants("Numb", "Linkin Park"));
    }

    [Fact]
    public void LyricsCacheRoundTrips()
    {
        var synced = Lyrics.FromLrc(CyrillicLrc);
        var back = LyricsCache.Parse(LyricsCache.Format(synced, 2));
        Assert.NotNull(back);
        Assert.Equal(2, back.Value.Index);
        Assert.Equal(LyricsStatus.Synced, back.Value.Lyrics.Status);
        Assert.Equal(synced.Lines, back.Value.Lyrics.Lines);

        var plain = Lyrics.FromPlain("[Chorus]\nраз\n\nдва");
        Assert.Equal(plain.Lines, LyricsCache.Parse(LyricsCache.Format(plain, 0))!.Value.Lyrics.Lines);
        Assert.Same(Lyrics.NotFound, LyricsCache.Parse(LyricsCache.Format(Lyrics.NotFound, 0))!.Value.Lyrics);
        Assert.Null(LyricsCache.Parse("мусор"));
    }

    /// <summary>Живой lrclib: у «The 7th Element» на нём только транслит с битыми метками, а нужен текст на кириллице.</summary>
    [SkippableFact]
    public async Task FindsCyrillicLyricsForVitas()
    {
        LyricsCache.Folder = Path.Combine(Path.GetTempPath(), "volumix-tests-" + Guid.NewGuid().ToString("N"));
        var candidates = MediaSessionService.Candidates("The 7th Element", "VITAS").Take(4).ToList();
        var (video, _) = await LyricsService.FindVideoAsync(candidates, TimeSpan.FromSeconds(249), CancellationToken.None);
        var track = await LyricsService.FindAsync("The 7th Element", "VITAS", null, TimeSpan.FromSeconds(249), CancellationToken.None);
        Skip.If(video.Status == LyricsStatus.Error || track.Status == LyricsStatus.Error, "lrclib.net недоступен");

        foreach (var lyrics in new[] { video, track })
        {
            Assert.Equal(LyricsStatus.Synced, lyrics.Status);
            Assert.Contains(lyrics.Lines, l => l.Text.Contains("песню"));
        }
    }

    [Fact]
    public void PickTrackRejectsWrongArtistEvenIfDurationMatches()
    {
        var found = new List<LyricsService.LrcLibTrack>
        {
            new()
            {
                TrackName = "Ride Or Die",
                ArtistName = "SMACK",
                Duration = 184,
                SyncedLyrics = SyncedLrc
            }
        };

        // Ищем песню Big Baby Tape, а в результатах только SMACK с той же длительностью
        var picked = LyricsService.PickTrack(found, 184, 12, "Ride Or Die", "Big Baby Tape");
        Assert.Null(picked);
    }

    [Fact]
    public void PickTrackAcceptsMatchingArtist()
    {
        var found = new List<LyricsService.LrcLibTrack>
        {
            new()
            {
                TrackName = "Ride Or Die",
                ArtistName = "SMACK",
                Duration = 184,
                SyncedLyrics = SyncedLrc
            },
            new()
            {
                TrackName = "Ride Or Die",
                ArtistName = "Big Baby Tape",
                Duration = 184,
                SyncedLyrics = CyrillicLrc
            }
        };

        var picked = LyricsService.PickTrack(found, 184, 12, "Ride Or Die", "Big Baby Tape");
        Assert.NotNull(picked);
        Assert.Equal("Я пришёл дать эту песню", picked.Value.Lyrics.Lines[0].Text);
    }

    [Fact]
    public void CandidatesCleansYouTubeArtist()
    {
        var candidates = MediaSessionService.Candidates("Ride Or Die", "Big Baby Tape - Topic");
        Assert.Equal("Big Baby Tape", candidates[0].Artist);
        Assert.Equal("Ride Or Die", candidates[0].Title);

        var mareux = MediaSessionService.Candidates("The Perfect Girl", "Mareux - Topic");
        Assert.Equal("Mareux", mareux[0].Artist);
        Assert.Equal("The Perfect Girl", mareux[0].Title);
    }

    [Fact]
    public void ParsesAmalgamaPage()
    {
        const string html = """
            <div id="click_area">
            <div class="string_container"><div class="original">I'm tired of being what you want me to be</div><div class="translate">Перевод 1</div></div>
            <div class="string_container"><div class="original">Feeling so faithless lost under the surface</div><div class="translate">Перевод 2</div></div>
            <div class="empty_container"><div class="original"><br /></div><div class="translate"><br /></div></div>
            <div class="string_container"><div class="original">Don't know what you're expecting of me</div><div class="translate">Перевод 3</div></div>
            <div class="string_container"><div class="original"><strong>Numb (второй перевод)</strong></div></div>
            <div class="string_container"><div class="original">Второй перевод строки</div></div>
            </div>
            """;
        var parsed = LyricsService.ParseAmalgamaPage(html);
        Assert.Equal("I'm tired of being what you want me to be\nFeeling so faithless lost under the surface\n\nDon't know what you're expecting of me", parsed);
        Assert.Null(LyricsService.ParseAmalgamaPage("<html>нет текста</html>"));
    }

    [Fact]
    public void GeneratesAmalgamaSlug()
    {
        Assert.Equal("linkin_park", LyricsService.Slug("Linkin Park"));
        Assert.Equal("beatles", LyricsService.Slug("The Beatles"));
        Assert.Equal("don_t_stop_believin", LyricsService.Slug("Don't Stop Believin'"));
        Assert.Equal("numb", LyricsService.Slug("Numb (feat. Jay-Z)"));
    }

    [SkippableFact]
    public async Task FindsLyricsOnAmalgama()
    {
        var lyrics = await LyricsService.FetchAmalgamaAsync("Numb", "Linkin Park", CancellationToken.None);
        Skip.If(lyrics.Status == LyricsStatus.Error, "amalgama-lab.com недоступен");

        Assert.Equal(LyricsStatus.Plain, lyrics.Status);
        Assert.Equal("I'm tired of being what you want me to be", lyrics.Lines[0].Text);
        Assert.Contains(lyrics.Lines, l => l.Text.Contains("undertow", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LyricsStatus.NotFound,
            (await LyricsService.FetchAmalgamaAsync("UnknownNonExistingSong123", "UnknownArtist123", CancellationToken.None)).Status);
    }

    [SkippableFact]
    public async Task FindsSyncedLyricsOnKugou()
    {
        var lyrics = await LyricsService.FetchKugouAsync("Numb", "Linkin Park", 187, 3, CancellationToken.None);
        Skip.If(lyrics.Status == LyricsStatus.Error, "kugou.com недоступен");

        Assert.Equal(LyricsStatus.Synced, lyrics.Status);
        Assert.Contains(lyrics.Lines, l => l.Text.Contains("tired of being", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task FindsRussianSyncedLyricsOnKugou()
    {
        var lyrics = await LyricsService.FetchKugouAsync("Группа крови", "Кино", 284, 5, CancellationToken.None);
        Skip.If(lyrics.Status == LyricsStatus.Error, "kugou.com недоступен");

        Assert.Equal(LyricsStatus.Synced, lyrics.Status);
        Assert.Contains(lyrics.Lines, l => l.Text.Contains("крови", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task TimedTierPrefersSyncedLyrics()
    {
        var (synced, _) = await LyricsService.FetchTimedAsync("Группа крови", "Кино", null, TimeSpan.FromSeconds(284), CancellationToken.None);
        Skip.If(synced == null || synced.Status == LyricsStatus.Error, "Сервисы недоступны");

        Assert.NotNull(synced);
        Assert.Equal(LyricsStatus.Synced, synced.Status);
    }

    private static void Pump(double seconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Яркая «обложка», чтобы было видно размытый фон.</summary>
    internal static byte[] MakeCover(int width = 300, int height = 300)
    {
        using var bmp = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Point(0, 0), new System.Drawing.Point(width, height),
                   System.Drawing.Color.FromArgb(220, 60, 40), System.Drawing.Color.FromArgb(30, 60, 160)))
        {
            g.FillRectangle(brush, 0, 0, width, height);
            g.FillEllipse(System.Drawing.Brushes.Gold, width / 2 - 60, height / 2 - 60, 120, 120);
        }
        using var png = new MemoryStream();
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        return png.ToArray();
    }
}
