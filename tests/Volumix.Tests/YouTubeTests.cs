using System.IO;
using Volumix.Services;

namespace Volumix.Tests;

public class YouTubeTests
{
    /// <summary>Кадр из браузера мельче, чем на YouTube, но сверка его узнаёт, а другую картинку — нет.</summary>
    [Fact]
    public void ComparesFrames()
    {
        var frame = LyricsTests.MakeCover(1280, 720);
        using var big = YouTubeService.Fingerprint(frame);
        using var small = YouTubeService.Fingerprint(Shrink(frame, 150, 83));
        using var other = YouTubeService.Fingerprint(Solid(System.Drawing.Color.SeaGreen));
        Assert.NotNull(big);
        Assert.InRange(YouTubeService.Difference(big!, small!), 0, 10);
        Assert.True(YouTubeService.Difference(big!, other!) > 28);
    }

    [Theory]
    [InlineData("Linkin Park - Numb (Fan Made Clip)", true)]
    [InlineData("Мот — Капкан [Official Music Video]", true)]
    [InlineData("Numb | Piano Cover", true)]
    [InlineData("Кино - Группа крови (кавер)", true)]
    [InlineData("ПОШЛАЯ МОЛЛИ –СУПЕРМАРКЕТ  [320 kbps]", true)]
    [InlineData("Как я провёл лето — влог", false)]
    [InlineData("Лекция 5. Матанализ", false)]
    [InlineData("Remixing my kitchen", false)] // «remix» только целым словом
    public void TellsMusicVideos(string title, bool music) => Assert.Equal(music, DesktopLyricsService.IsMusicVideo(title));

    /// <summary>Кусок страницы ролика: блок «Музыка» в описании, как его отдаёт YouTube.</summary>
    private const string MusicSection = """
        {"horizontalCardListRenderer":{"cards":[{"videoAttributeViewModel":{"image":{"sources":[]},"title":"Супермаркет",
        "subtitle":"Пошлая Молли","secondarySubtitle":{"content":"8 способов как бросить..."}}}],
        "header":{"richListHeaderRenderer":{"title":{"simpleText":"Музыка"}}},
        "footerButton":{"buttonViewModel":{"iconName":"MUSIC","titleFormatted":{"content":"Музыка"}}}}}
        """;

    [Fact]
    public void ReadsMusicSection() =>
        Assert.Equal(new YouTubeSong("Супермаркет", "Пошлая Молли", "8 способов как бросить..."),
            YouTubeService.ParseMusicSection(MusicSection));

    /// <summary>Такие же карточки бывают у мест и игр — без значка MUSIC это не песня.</summary>
    [Fact]
    public void IgnoresOtherCards() =>
        Assert.Null(YouTubeService.ParseMusicSection(MusicSection.Replace("\"MUSIC\"", "\"PLACE\"")));

    [Fact]
    public void ReadsSearchResults()
    {
        const string html = """
            {"videoRenderer":{"videoId":"i7o5erke_iU","thumbnail":{},"title":{"runs":[{"text":"ПОШЛАЯ МОЛЛИ –СУПЕРМАРКЕТ"}]}}},
            {"videoRenderer":{"videoId":"x6_mbnsh6VU","thumbnail":{},"title":{"runs":[{"text":"Другое"}]}}}
            """;
        Assert.Equal(new[] { ("i7o5erke_iU", "ПОШЛАЯ МОЛЛИ –СУПЕРМАРКЕТ"), ("x6_mbnsh6VU", "Другое") },
            YouTubeService.ParseSearch(html).ToArray());
    }

    /// <summary>
    /// Ролик, у которого в названии тире без пробела, а категория — «Gaming»: песню из описания всё равно видно.
    /// Кадра нет — ролик ищется по названию.
    /// </summary>
    [SkippableFact]
    public async Task FindsSongFromDescription()
    {
        var song = await YouTubeService.FindSongAsync("ПОШЛАЯ МОЛЛИ –СУПЕРМАРКЕТ  [320 kbps]", "ХАРТ_hack", null);
        Skip.If(song == null, "YouTube недоступен или ролик не нашёлся");
        Assert.Equal("Супермаркет", song!.Title);
        Assert.Equal("Пошлая Молли", song.Artist);
    }

    [Fact]
    public void IgnoresBrokenImage() => Assert.Null(YouTubeService.Fingerprint(new byte[] { 1, 2, 3 }));

    /// <summary>Как браузер: тот же кадр, ужатый до 150×83.</summary>
    private static byte[] Shrink(byte[] image, int width, int height)
    {
        using var source = new System.Drawing.Bitmap(new MemoryStream(image));
        using var bmp = new System.Drawing.Bitmap(source, width, height);
        using var png = new MemoryStream();
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        return png.ToArray();
    }

    private static byte[] Solid(System.Drawing.Color color)
    {
        using var bmp = new System.Drawing.Bitmap(160, 90);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.Clear(color);
        using var png = new MemoryStream();
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        return png.ToArray();
    }
}
