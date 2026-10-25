using Volumix.Services;

namespace Volumix.Tests;

/// <summary>Опознание плеера, сайта в браузере и чистка названий — без запущенных плееров и окон.</summary>
[Collection("DPI")]
public class MediaSessionTests
{
    [Theory]
    [InlineData("Spotify.exe")]
    [InlineData("spotify")]
    [InlineData("C:\\Users\\vikto\\AppData\\Roaming\\Spotify\\Spotify.exe")]
    public void RecognizesSpotify(string aumid) =>
        Assert.Equal(MusicSource.Spotify, MediaSessionService.Classify(aumid, MusicSourceMode.Auto, BrowserMusic.None));

    [Theory]
    [InlineData("Chrome")]
    [InlineData("MSEdge")]
    [InlineData("Firefox")]
    [InlineData("YandexBrowser")]
    [InlineData("C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe")]
    public void RecognizesBrowserAsYandexMusicInYandexMode(string aumid) =>
        Assert.Equal(MusicSource.YandexMusic,
            MediaSessionService.Classify(aumid, MusicSourceMode.YandexMusic, BrowserMusic.None));

    [Theory]
    [InlineData("Chrome")]
    [InlineData("MSEdge")]
    public void RecognizesBrowserAsYouTubeInYouTubeMode(string aumid) =>
        Assert.Equal(MusicSource.YouTube, MediaSessionService.Classify(aumid, MusicSourceMode.YouTube, BrowserMusic.None));

    [Fact]
    public void BrowserNeedsDetectedSiteInAutoMode()
    {
        Assert.Equal(MusicSource.None, MediaSessionService.Classify("Chrome", MusicSourceMode.Auto, BrowserMusic.None));
        Assert.Equal(MusicSource.YandexMusic,
            MediaSessionService.Classify("Chrome", MusicSourceMode.Auto, BrowserMusic.YandexMusic));
        Assert.Equal(MusicSource.YouTubeMusic,
            MediaSessionService.Classify("Chrome", MusicSourceMode.Auto, BrowserMusic.YouTubeMusic));
        Assert.Equal(MusicSource.YouTube, MediaSessionService.Classify("Chrome", MusicSourceMode.Auto, BrowserMusic.YouTube));
    }

    [Fact]
    public void SourceModeFiltersPlayers()
    {
        Assert.Equal(MusicSource.None,
            MediaSessionService.Classify("Spotify.exe", MusicSourceMode.YandexMusic, BrowserMusic.None));
        Assert.Equal(MusicSource.None, MediaSessionService.Classify("Chrome", MusicSourceMode.Spotify, BrowserMusic.YouTube));
        Assert.Equal(MusicSource.YandexMusic,
            MediaSessionService.Classify("YandexMusic.exe", MusicSourceMode.Auto, BrowserMusic.None));
        Assert.Equal(MusicSource.None, MediaSessionService.Classify("Discord.exe", MusicSourceMode.Auto, BrowserMusic.YouTube));
    }

    [Theory]
    [InlineData("Яндекс Музыка", true)]
    [InlineData("Яндекс.Музыка — мой плейлист", true)]
    [InlineData("Yandex Music — player", true)]
    [InlineData("Вкладка — Яндекс Музыка — Google Chrome", true)]
    [InlineData("GitHub - kolyaka6969-ops/Volumix", false)]
    [InlineData("Музыка вконтакте", false)]
    [InlineData("", false)]
    public void DetectsYandexMusicTitle(string title, bool expected) =>
        Assert.Equal(expected, BrowserMusicDetector.LooksLikeYandexMusic(title));

    [Theory]
    [InlineData("Кино - Группа крови - YouTube", true)]
    [InlineData("Кино - Группа крови — YouTube Music", true)]
    [InlineData("GitHub - kolyaka6969-ops/Volumix", false)]
    [InlineData("", false)]
    public void DetectsYouTubeTitle(string title, bool expected) =>
        Assert.Equal(expected, BrowserMusicDetector.LooksLikeYouTube(title));

    [Theory]
    [InlineData("Кино - Группа крови — YouTube Music", true)]
    [InlineData("Кино - Группа крови — YouTube", false)]
    [InlineData("", false)]
    public void DetectsYouTubeMusicTitle(string title, bool expected) =>
        Assert.Equal(expected, BrowserMusicDetector.LooksLikeYouTubeMusic(title));

    [Theory]
    [InlineData("Кино - Группа крови — YouTube Music", BrowserMusic.YouTubeMusic)]
    [InlineData("Кино - Группа крови — YouTube", BrowserMusic.YouTube)]
    [InlineData("Яндекс Музыка — главная", BrowserMusic.YandexMusic)]
    [InlineData("GitHub - kolyaka6969-ops/Volumix", BrowserMusic.None)]
    [InlineData("", BrowserMusic.None)]
    public void ClassifiesBrowserTabTitles(string title, BrowserMusic expected) =>
        Assert.Equal(expected, BrowserMusicDetector.ClassifyTitle(title));

    [Fact]
    public void RanksMusicSitesAboveYouTube() =>
        Assert.True(BrowserMusicDetector.Rank(BrowserMusic.YandexMusic) > BrowserMusicDetector.Rank(BrowserMusic.YouTubeMusic));
    [Fact]
    public void RanksYouTubeMusicAboveYouTube() =>
        Assert.True(BrowserMusicDetector.Rank(BrowserMusic.YouTubeMusic) > BrowserMusicDetector.Rank(BrowserMusic.YouTube));

    [Fact]
    public void SplitsArtistFromBrowserTitle()
    {
        var (title, artist, _) = MediaSessionService.CleanMetadata("Кино — Группа крови", null, null, MusicSource.YandexMusic);
        Assert.Equal("Группа крови", title);
        Assert.Equal("Кино", artist);
    }

    [Fact]
    public void KeepsBrowserArtistWhenGiven()
    {
        var (title, artist, _) = MediaSessionService.CleanMetadata("Группа крови", "Кино", null, MusicSource.YandexMusic);
        Assert.Equal("Группа крови", title);
        Assert.Equal("Кино", artist);
    }

    [Fact]
    public void StripsSiteSuffix()
    {
        var (title, _, _) = MediaSessionService.CleanMetadata("Группа крови — Яндекс Музыка", "Кино", null,
            MusicSource.YandexMusic);
        Assert.Equal("Группа крови", title);
    }

    [Fact]
    public void StripsYouTubeSuffix()
    {
        var (title, _, _) = MediaSessionService.CleanMetadata("Кино - Группа крови - YouTube", null, null, MusicSource.YouTube);
        Assert.Equal("Группа крови", title);
    }

    [Fact]
    public void LeavesSpotifyMetadataAlone()
    {
        var (title, artist, _) = MediaSessionService.CleanMetadata("AC/DC - Back in Black", null, null, MusicSource.Spotify);
        Assert.Equal("AC/DC - Back in Black", title);
        Assert.Null(artist);
    }

    [Theory]
    [InlineData("Кино - Группа крови (Official Video)", "Кино - Группа крови")]
    [InlineData("Группа крови [MV]", "Группа крови")]
    [InlineData("Группа крови (Lyrics)", "Группа крови")]
    [InlineData("Группа крови (Official Audio)", "Группа крови")]
    [InlineData("Группа крови (Live)", "Группа крови (Live)")]
    [InlineData("ПОШЛАЯ МОЛЛИ - МОЛЛИ [320 kbps]+ Текст", "ПОШЛАЯ МОЛЛИ - МОЛЛИ")]
    [InlineData("Группа крови | Lyrics", "Группа крови")]
    [InlineData("Группа крови (slowed + reverb)", "Группа крови")]
    [InlineData("Мой текст", "Мой текст")]
    public void StripsVideoNoise(string title, string expected) =>
        Assert.Equal(expected, MediaSessionService.StripVideoNoise(title));

    [Fact]
    public void CleansYouTubeChannel()
    {
        Assert.Equal("Кино", MediaSessionService.CleanChannel("Кино - Topic"));
        Assert.Equal("Kino", MediaSessionService.CleanChannel("KinoVEVO"));
        Assert.Equal("The Cranberries", MediaSessionService.CleanChannel("TheCranberriesVEVO"));
        Assert.Equal("Daft Punk", MediaSessionService.CleanChannel("DaftPunkVEVO"));
        Assert.Null(MediaSessionService.CleanChannel(null));
        Assert.Null(MediaSessionService.CleanChannel("  "));
    }

    /// <summary>Канал «Wiz Khalifa Music» — это «Wiz Khalifa» плюс приписка; гость и хвост после «ft.» в поиск не идут.</summary>
    [Fact]
    public void ReadsChannelWithSuffixAndFeaturing()
    {
        var (title, artist, _) = MediaSessionService.CleanMetadata(
            "Wiz Khalifa - See You Again ft. Charlie Puth [Official Video] Furious 7 Soundtrack", "Wiz Khalifa Music", null,
            MusicSource.YouTube);
        Assert.Equal("Wiz Khalifa", artist);
        Assert.Equal("See You Again ft. Charlie Puth Furious 7 Soundtrack", title);

        var candidates = MediaSessionService.Candidates(title, artist).Take(4).ToList();
        Assert.Equal(("See You Again", "Wiz Khalifa"), (candidates[1].Title, candidates[1].Artist));
    }

    /// <summary>Ролики из джема, которые не узнавались: приписка «+ Текст» и гость через «starring».</summary>
    [Theory]
    [InlineData("ПОШЛАЯ МОЛЛИ - МОЛЛИ [320 kbps]+ Текст", "ХАРТ_hack", "МОЛЛИ", "ПОШЛАЯ МОЛЛИ")]
    [InlineData("GENER8ION - STORM starring Yung Lean", "GENER8ION", "STORM", "GENER8ION")]
    public void ReadsNoisyJamVideos(string video, string channel, string song, string singer)
    {
        var (title, artist, _) = MediaSessionService.CleanMetadata(video, channel, null, MusicSource.YouTube);
        var candidates = MediaSessionService.Candidates(title, artist).Take(4).ToList();
        Assert.Contains((song, (string?)singer), candidates);
    }

    [Theory]
    [InlineData("Chainsaw Man - Opening | KICK BACK", "KICK BACK", null)]
    [InlineData("Demon Slayer - Opening Full 『Gurenge』 by LiSA (Lyrics)", "Gurenge", "LiSA")]
    [InlineData("TVアニメ「呪術廻戦」第2期「渋谷事変」ノンクレジットOP｜King Gnu「SPECIALZ」", "SPECIALZ", "King Gnu")]
    [InlineData("Spy x Family Opening - Mixed Nuts by Official HIGE DANdism", "Mixed Nuts", "Official HIGE DANdism")]
    [InlineData("Naruto Shippuden Opening 16 | Silhouette (HD)", "Silhouette", null)]
    [InlineData("Tokyo Ghoul OP - Unravel [1080p]", "Unravel", null)]
    public void ReadsAnimeOpenings(string video, string song, string? singer)
    {
        var (title, artist, _) = MediaSessionService.CleanMetadata(video, "Crunchyroll", null, MusicSource.YouTube);
        Assert.Equal(song, title);
        Assert.Equal(singer, artist);
    }

    [Fact]
    public void ReadsOpeningWithoutSong()
    {
        var parsed = AnimeThemeService.Parse(MediaSessionService.VideoTitle("Jujutsu Kaisen - Opening 2 | 4K | 60FPS | Creditless |"));
        Assert.NotNull(parsed);
        Assert.Equal(("Jujutsu Kaisen", "OP", 2, (string?)null), (parsed!.Anime, parsed.Kind, parsed.Number, parsed.Song));
        Assert.True(parsed.Strong);

        var found = new[]
        {
            new AnimeThemeService.Anime("Jujutsu Kaisen 2nd Season", new[] { "Jujutsu Kaisen Season 2" }, new[]
            {
                new AnimeThemeService.Theme("OP", 1, "Ao no Sumika", "Tatsuya Kitani"),
                new AnimeThemeService.Theme("OP", 2, "SPECIALZ", "King Gnu"),
            }),
        };
        Assert.Equal(("SPECIALZ", (string?)"King Gnu"), AnimeThemeService.Pick(parsed, found));
    }

    /// <summary>Обычные песни со словами «Ending», «Opening» и именем «Ed» опенингами не считаем.</summary>
    [Theory]
    [InlineData("Mika - Happy Ending", "Happy Ending", "Mika")]
    [InlineData("Isak Danielson - Ending", "Ending", "Isak Danielson")]
    [InlineData("Ed Sheeran - Perfect (Official Music Video)", "Perfect", "Ed Sheeran")]
    public void KeepsSongsAboutEndings(string video, string song, string singer)
    {
        var (title, artist, _) = MediaSessionService.CleanMetadata(video, singer, null, MusicSource.YouTube);
        Assert.Equal(song, title);
        Assert.Equal(singer, artist);
        var parsed = AnimeThemeService.Parse(video);
        // «Mika - Happy Ending» могла бы быть эндингом аниме «Happy» — без совпадения песни в AnimeThemes не верим
        Assert.Null(parsed == null ? null : AnimeThemeService.Pick(parsed, Array.Empty<AnimeThemeService.Anime>()));
    }

    /// <summary>«Roxy Music» — сама группа, а не приписка: название без её имени не трогаем.</summary>
    [Fact]
    public void KeepsBandNamedMusic()
    {
        var (title, artist, _) = MediaSessionService.CleanMetadata("Avalon (Official Video)", "Roxy Music", null, MusicSource.YouTube);
        Assert.Equal("Avalon", title);
        Assert.Equal("Roxy Music", artist);
    }

    [Fact]
    public void CleansYouTubeMetadata()
    {
        var (title, artist, _) = MediaSessionService.CleanMetadata("Кино - Группа крови (Official Video)",
            "Кино - Topic", null, MusicSource.YouTube);
        Assert.Equal("Группа крови", title);
        Assert.Equal("Кино", artist);
    }

    [Fact]
    public void CleansYouTubeMetadataWithoutArtist()
    {
        var (title, artist, _) = MediaSessionService.CleanMetadata("Кино - Группа крови (Official Video)", null, null,
            MusicSource.YouTube);
        Assert.Equal("Группа крови", title);
        Assert.Equal("Кино", artist);
    }

    [Theory]
    [InlineData("Группа крови", "Кино", "Группа крови", "Кино", 6)]
    [InlineData("Группа крови", "Кино", "Группа крови (Remastered)", "Кино", 5)]
    [InlineData("Группа крови", "Кино", "Группа крови", "Другая группа", 3)]
    [InlineData("Группа крови", "Кино", "Песня без слов", "Кино", 0)]
    public void ScoresCoverCandidates(string title, string? artist, string candidateTitle, string candidateArtist, int expected) =>
        Assert.Equal(expected, CoverArtService.Score(title, artist, candidateTitle, candidateArtist));

    [Theory]
    [InlineData("Ёлка", "елка")]
    [InlineData("  Кино — Группа крови! ", "кино группа крови")]
    [InlineData(null, "")]
    public void NormalizesForComparison(string? text, string expected) =>
        Assert.Equal(expected, CoverArtService.Norm(text));

    [Fact]
    public void ProposesSongAndArtistFromVideoTitle()
    {
        var candidates = MediaSessionService.Candidates(
            "I Follow Rivers - Lykke Li (La Vie d'Adèle/La Vida de Adele)", "Aldo Zuga");
        Assert.Contains(("I Follow Rivers", (string?)"Lykke Li"), candidates);
        Assert.Equal(("I Follow Rivers", (string?)"Lykke Li"), candidates[1]);
    }

    [Fact]
    public void ProposesReversedOrderWhenArtistComesFirst()
    {
        var candidates = MediaSessionService.Candidates("Кино - Группа крови", null);
        Assert.Contains(("Группа крови", (string?)"Кино"), candidates);
        Assert.Contains(("Кино", (string?)"Группа крови"), candidates);
    }

    [Fact]
    public void ProposesArtistFromColonTitle()
    {
        var candidates = MediaSessionService.Candidates("twenty one pilots: Heathens", "Fueled By Ramen");
        Assert.Contains(("Heathens", (string?)"twenty one pilots"), candidates);
        Assert.Contains(("twenty one pilots", (string?)"Heathens"), candidates);
    }

    [Fact]
    public void DoesNotRepeatCandidates()
    {
        var candidates = MediaSessionService.Candidates("Группа крови", "Кино");
        Assert.Equal(2, candidates.Count);
        Assert.Equal(("Группа крови", (string?)"Кино"), candidates[0]);
        Assert.Equal(("Группа крови", (string?)null), candidates[1]);
    }

    [Theory]
    [InlineData("Группа крови (Official Video)", "Группа крови")]
    [InlineData("I Follow Rivers - Lykke Li (La Vie d'Adèle/La Vida de Adele)", "I Follow Rivers - Lykke Li")]
    [InlineData("Группа крови", "Группа крови")]
    public void RemovesBracketsForSearch(string title, string expected) =>
        Assert.Equal(expected, MediaSessionService.WithoutBrackets(title));

    /// <summary>Chrome без обложки от сайта присылает свой значок — круг на прозрачном фоне.</summary>
    [Fact]
    public void RecognizesBrowserIconInsteadOfCover()
    {
        Assert.True(MediaSessionService.IsAppIcon(Png(System.Drawing.Color.Transparent)));
        Assert.False(MediaSessionService.IsAppIcon(Png(System.Drawing.Color.DarkRed)));
        Assert.False(MediaSessionService.IsAppIcon(Png(System.Drawing.Color.FromArgb(133, 40, 40, 40))));  // как у Spotify
        Assert.False(MediaSessionService.IsAppIcon(new byte[] { 1, 2, 3 }));
    }

    /// <summary>Spotify отдаёт обложку 300×300 — в каталоге должна найтись та же, но крупнее, а не караоке-версия.</summary>
    [SkippableFact]
    public async Task FindsSharpCoverForSpotifyTrack()
    {
        var (title, artist, _) = MediaSessionService.CleanMetadata(
            "Go Off (with Lil Uzi Vert, Quavo & Travis Scott)", "Lil Uzi Vert", "The Fate of the Furious: The Album", MusicSource.Spotify);
        var art = await CoverArtService.FindAsync(MediaSessionService.Candidates(title, artist).Take(4).ToList());
        Skip.If(art == null && !await Reachable("https://api.deezer.com/"), "Deezer недоступен");

        Assert.NotNull(art);
        Assert.True(MediaSessionService.PixelSize(art) >= 600, $"обложка {MediaSessionService.PixelSize(art)} пикселей");
    }

    private static async Task<bool> Reachable(string url)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await http.GetAsync(url);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [Fact]
    public void MeasuresCoverSize()
    {
        Assert.Equal(64, MediaSessionService.PixelSize(Png(System.Drawing.Color.DarkRed)));
        Assert.Equal(0, MediaSessionService.PixelSize(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void RejectsCoverLeftFromPreviousVideo()
    {
        var frame = Png(System.Drawing.Color.DarkRed);
        var copy = (byte[])frame.Clone();
        // Ролик YouTube без альбома сменился треком Яндекс Музыки, а браузер ещё отдаёт кадр ролика
        Assert.True(MediaSessionService.IsLeftover(copy, frame, null, "8 способов как бросить…"));
        // Следующий трек того же альбома — обложка общая, это не ошибка
        Assert.False(MediaSessionService.IsLeftover(copy, frame, "Альбом", "Альбом"));
        // Картинка уже другая — браузер её сменил
        Assert.False(MediaSessionService.IsLeftover(Png(System.Drawing.Color.Navy), frame, null, "Альбом"));
        Assert.False(MediaSessionService.IsLeftover(copy, null, null, "Альбом"));
    }

    [Fact]
    public void PicksScreensForDesktopLyrics()
    {
        var all = System.Windows.Forms.Screen.AllScreens;
        Assert.Equal(all.Select(s => s.DeviceName), DesktopLyricsService.Devices(AppSettings.AllScreens));
        Assert.Equal(new string?[] { null }, DesktopLyricsService.Devices(AppSettings.PrimaryScreen));
        Assert.Equal(new string?[] { all[0].DeviceName }, DesktopLyricsService.Devices(all[0].DeviceName));
        // Монитор отключили — показываем на основном
        Assert.Equal(new string?[] { null }, DesktopLyricsService.Devices(@"\\.\DISPLAY99"));
        var primary = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        Assert.Equal(primary, DesktopLayer.ScreenBounds(@"\\.\DISPLAY99"));
        Assert.Equal(all[^1].Bounds, DesktopLayer.ScreenBounds(all[^1].DeviceName));
    }

    private static byte[] Png(System.Drawing.Color background)
    {
        using var bmp = new System.Drawing.Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(background);
            g.FillEllipse(System.Drawing.Brushes.Gold, 4, 4, 56, 56);
        }
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }
}
