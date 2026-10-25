using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Volumix.Services;

/// <summary>
/// Видео из браузера: браузер отдаёт название ролика, канал и обложку размером со спичечный коробок.
/// Здесь — название песни для поиска текста и обложка: официальная обложка песни из iTunes, а если песни там нет
/// (неофициальный клип, кавер, просто видео) — обложка ролика в полном размере с YouTube.
/// </summary>
public static class YouTubeService
{
    /// <summary>Сколько первых результатов поиска сверять с обложкой из браузера.</summary>
    private const int Candidates = 4;
    /// <summary>Средняя разница яркости (0–255), при которой картинки ещё считаются одной обложкой.</summary>
    private const double MaxDifference = 28;
    private const int CacheLimit = 100;

    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, byte[]?> Cache = new();

    private static readonly Regex VideoId = new(@"""videoId"":""([\w-]{11})""", RegexOptions.Compiled);
    private static readonly Regex Dash = new(@"\s+[-–—]\s+", RegexOptions.Compiled);
    // (Official Video), [MV], (Lyrics), 「…」, | Премьера клипа …
    private static readonly Regex Noise = new(
        @"\s*[(\[【「][^)\]】」]*(?:official|video|audio|lyric|клип|видео|премьера|mv|m/v|hd|4k|visualizer|live|концерт)[^)\]】」]*[)\]】」]|\s+[|｜/].*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ChannelNoise = new(@"\s*(?:VEVO|-\s*Topic|Official)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Brackets = new(@"\s*[(\[][^)\]]*[)\]]", RegexOptions.Compiled);
    private static readonly Regex NotLetters = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru,en;q=0.8");
        // Без него в Европе вместо выдачи YouTube приходит страница согласия на куки
        http.DefaultRequestHeaders.Add("Cookie", "SOCS=CAI");
        return http;
    }

    /// <summary>«Eminem - Not Afraid (Official Video)», канал «EminemVEVO» → песня «Not Afraid», исполнитель «Eminem».</summary>
    public static (string Title, string? Artist) ParseTitle(string title, string? channel)
    {
        var clean = Noise.Replace(title, "").Trim();
        if (clean.Length == 0)
            clean = title.Trim();
        var parts = Dash.Split(clean, 2);
        if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            return (parts[1].Trim().Trim('"', '«', '»'), parts[0].Trim());
        var artist = channel == null ? null : ChannelNoise.Replace(channel, "").Trim();
        return (clean.Trim('"', '«', '»'), string.IsNullOrEmpty(artist) ? null : artist);
    }

    /// <summary>Официальная обложка песни, если её нет — обложка ролика с YouTube. Никогда не бросает исключений.</summary>
    public static async Task<byte[]?> FindCoverAsync(string title, string? channel, byte[] thumbnail, CancellationToken ct)
    {
        var key = $"{channel}\n{title}";
        lock (Cache)
            if (Cache.TryGetValue(key, out var cached))
                return cached;

        byte[]? cover;
        try
        {
            var (song, artist) = ParseTitle(title, channel);
            cover = await FindOfficialAsync(song, artist, ct) ?? await FindVideoCoverAsync(title, channel, thumbnail, ct);
        }
        catch (Exception) { return null; } // нет сети, отмена, сервис поменял ответ — в кэш не кладём

        lock (Cache)
        {
            if (Cache.Count >= CacheLimit)
                Cache.Clear();
            Cache[key] = cover;
        }
        return cover;
    }

    /// <summary>Обложка альбома из поиска iTunes (открытый, без ключа) — только если совпали и песня, и исполнитель.</summary>
    private static async Task<byte[]?> FindOfficialAsync(string song, string? artist, CancellationToken ct)
    {
        if (artist == null)
            return null; // по одному названию легко найти чужую песню
        var term = Uri.EscapeDataString($"{artist} {song}");
        var json = await Http.GetStringAsync($"https://itunes.apple.com/search?term={term}&media=music&entity=song&limit=15", ct);
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var name = item.TryGetProperty("trackName", out var n) ? n.GetString() : null;
            var by = item.TryGetProperty("artistName", out var a) ? a.GetString() : null;
            var art = item.TryGetProperty("artworkUrl100", out var u) ? u.GetString() : null;
            if (name == null || by == null || art == null || !SameSong(song, name) || !SameArtist(artist, by))
                continue;
            // Ссылка ведёт на 100×100, но iTunes отдаёт любой размер по имени файла
            return await GetAsync(art.Replace("100x100bb", "1000x1000bb"), ct) ?? await GetAsync(art, ct);
        }
        return null;
    }

    internal static bool SameSong(string a, string b) => Normalize(Brackets.Replace(a, "")) == Normalize(Brackets.Replace(b, ""));

    /// <summary>«Eminem» и «Eminem & Rihanna», «Скриптонит» и «Скриптонит, 104».</summary>
    internal static bool SameArtist(string a, string b)
    {
        string x = Normalize(a), y = Normalize(b);
        return x.Length > 0 && y.Length > 0 && (x.Contains(y) || y.Contains(x));
    }

    private static string Normalize(string s) => NotLetters.Replace(s.ToLowerInvariant(), "");

    /// <summary>
    /// Ищет ролик на YouTube по названию и каналу и сверяет обложки с той, что прислал браузер, — чтобы не подсунуть
    /// чужой клип.
    /// </summary>
    private static async Task<byte[]?> FindVideoCoverAsync(string title, string? channel, byte[] thumbnail, CancellationToken ct)
    {
        using var reference = Fingerprint(thumbnail);
        if (reference == null)
            return null;

        var query = Uri.EscapeDataString(channel == null ? title : $"{title} {channel}");
        var html = await Http.GetStringAsync("https://www.youtube.com/results?search_query=" + query, ct);
        var ids = VideoId.Matches(html).Select(m => m.Groups[1].Value).Distinct().Take(Candidates).ToList();

        string? best = null;
        double bestDifference = MaxDifference;
        foreach (var id in ids)
        {
            var preview = await GetAsync($"https://i.ytimg.com/vi/{id}/mqdefault.jpg", ct);
            using var candidate = preview == null ? null : Fingerprint(preview);
            if (candidate == null)
                continue;
            double difference = Difference(reference, candidate);
            if (difference < bestDifference)
                (best, bestDifference) = (id, difference);
        }
        if (best == null)
            return null;

        // maxres есть не у всех роликов; hq — с чёрными полосами, но всё равно лучше крошечной
        return await GetAsync($"https://i.ytimg.com/vi/{best}/maxresdefault.jpg", ct)
               ?? await GetAsync($"https://i.ytimg.com/vi/{best}/hqdefault.jpg", ct);
    }

    private static async Task<byte[]?> GetAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
    }

    /// <summary>Картинка, ужатая до 16×9: для сравнения обложек хватает с запасом.</summary>
    internal static Bitmap? Fingerprint(byte[] image)
    {
        try
        {
            using var source = new Bitmap(new MemoryStream(image));
            var small = new Bitmap(16, 9);
            using var g = Graphics.FromImage(small);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            // Обложка из браузера уже 16:9, а у YouTube бывают 4:3 с полосами — берём середину
            double aspect = (double)source.Width / source.Height;
            var crop = aspect > 16 / 9.0
                ? new Rectangle((int)((source.Width - source.Height * 16 / 9.0) / 2), 0, (int)(source.Height * 16 / 9.0), source.Height)
                : new Rectangle(0, (int)((source.Height - source.Width * 9 / 16.0) / 2), source.Width, (int)(source.Width * 9 / 16.0));
            g.DrawImage(source, new Rectangle(0, 0, 16, 9), crop, GraphicsUnit.Pixel);
            return small;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static double Difference(Bitmap a, Bitmap b)
    {
        double sum = 0;
        for (int y = 0; y < 9; y++)
            for (int x = 0; x < 16; x++)
                sum += Math.Abs(Gray(a.GetPixel(x, y)) - Gray(b.GetPixel(x, y)));
        return sum / (16 * 9);

        static double Gray(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
    }
}
