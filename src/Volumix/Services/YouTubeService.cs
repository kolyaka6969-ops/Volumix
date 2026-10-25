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

/// <summary>Песня, которую YouTube указал в описании ролика (блок «Музыка»).</summary>
public sealed record YouTubeSong(string Title, string? Artist, string? Album);

/// <summary>
/// Сведения о ролике с самого YouTube. Браузер сообщает только название, канал и кадр размером со спичечный
/// коробок (150×83), а id ролика — нет, поэтому ролик ищем по названию и каналу и сверяем с кадром из браузера.
/// Отсюда берутся песня из описания ролика и кадр в полном размере — для клипов, которых нет в каталогах обложек.
/// </summary>
internal static class YouTubeService
{
    /// <summary>Сколько первых результатов поиска рассматривать.</summary>
    private const int Candidates = 6;
    /// <summary>Средняя разница цвета (0–255), при которой картинки ещё считаются одним кадром.</summary>
    private const double MaxDifference = 28;
    private const int CacheLimit = 100;

    private static readonly HttpClient Http = CreateClient();
    // Запросы, а не ответы: обои и поиск обложки спрашивают про один ролик одновременно — на YouTube идём один раз
    private static readonly Dictionary<string, Task<string?>> Ids = new();
    private static readonly Dictionary<string, Task<YouTubeSong?>> Songs = new();
    private static readonly Dictionary<string, Task<byte[]?>> Covers = new();

    private const string JsonText = @"""((?:[^""\\]|\\.)*)""";
    private static readonly Regex SearchResult = new(@"""videoRenderer"":\{""videoId"":""([\w-]{11})""", RegexOptions.Compiled);
    private static readonly Regex ResultTitle = new(@"""title"":\{""runs"":\[\{""text"":" + JsonText, RegexOptions.Compiled);
    private static readonly Regex CardTitle = new(@"""title"":" + JsonText, RegexOptions.Compiled);
    private static readonly Regex CardSubtitle = new(@"""subtitle"":" + JsonText, RegexOptions.Compiled);
    private static readonly Regex CardAlbum = new(@"""secondarySubtitle"":\{""content"":" + JsonText, RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru,en;q=0.8");
        // Без него в Европе вместо выдачи приходит страница согласия на куки
        http.DefaultRequestHeaders.Add("Cookie", "SOCS=CAI");
        return http;
    }

    /// <summary>
    /// Песня из описания ролика. YouTube сам опознаёт музыку в роликах и пишет её в описании блоком «Музыка» —
    /// раз он там есть, это песня, даже если её нет в базах текстов и обложек. Никогда не бросает исключений.
    /// </summary>
    public static Task<YouTubeSong?> FindSongAsync(string title, string? channel, byte[]? thumbnail) =>
        Cached(Songs, Key(title, channel), async () =>
        {
            var id = await FindVideoIdAsync(title, channel, thumbnail);
            if (id == null)
                return null;
            var html = await Http.GetStringAsync("https://www.youtube.com/watch?v=" + id);
            return ParseMusicSection(html);
        });

    /// <summary>
    /// Кадр ролика 1280×720 (или 480×360, если большого нет). null — ролик не нашёлся или кадр из браузера
    /// не похож ни на один найденный: так чужой клип не подставится. Никогда не бросает исключений.
    /// </summary>
    public static Task<byte[]?> FindCoverAsync(string title, string? channel, byte[] thumbnail) =>
        Cached(Covers, Key(title, channel), async () =>
        {
            // Для обложки ролик обязан совпасть с кадром — одного названия мало
            if (await FindVideoIdAsync(title, channel, thumbnail, needFrame: true) is not { } id)
                return null;
            // maxres есть не у всех роликов; hq — с чёрными полосами, но всё равно лучше крошечного
            return await GetAsync($"https://i.ytimg.com/vi/{id}/maxresdefault.jpg")
                   ?? await GetAsync($"https://i.ytimg.com/vi/{id}/hqdefault.jpg");
        });

    /// <summary>
    /// Ищет ролик в выдаче YouTube: если есть кадр из браузера — самый похожий на него, иначе первый с тем же
    /// названием. Вместо кадра браузер иногда присылает значок сайта — тогда выручает название.
    /// </summary>
    private static Task<string?> FindVideoIdAsync(string title, string? channel, byte[]? thumbnail, bool needFrame = false) =>
        Cached(Ids, Key(title, channel) + (needFrame ? "\nframe" : ""), async () =>
        {
            var query = Uri.EscapeDataString(channel == null ? title : $"{title} {channel}");
            var html = await Http.GetStringAsync("https://www.youtube.com/results?search_query=" + query);
            var results = ParseSearch(html).Take(Candidates).ToList();

            using var reference = thumbnail == null ? null : Fingerprint(thumbnail);
            if (reference != null)
            {
                string? best = null;
                double bestDifference = MaxDifference;
                foreach (var (id, _) in results)
                {
                    var preview = await GetAsync($"https://i.ytimg.com/vi/{id}/mqdefault.jpg");
                    using var candidate = preview == null ? null : Fingerprint(preview);
                    if (candidate == null)
                        continue;
                    double difference = Difference(reference, candidate);
                    if (difference < bestDifference)
                        (best, bestDifference) = (id, difference);
                }
                if (best != null || needFrame)
                    return best;
            }
            else if (needFrame)
                return null;

            var normalized = Normalize(title);
            return results.FirstOrDefault(r => Normalize(r.Title) == normalized).Id;
        });

    /// <summary>Ролики из выдачи поиска: id и название.</summary>
    internal static IEnumerable<(string Id, string Title)> ParseSearch(string html)
    {
        var seen = new HashSet<string>();
        foreach (Match m in SearchResult.Matches(html))
        {
            var id = m.Groups[1].Value;
            if (!seen.Add(id))
                continue;
            // Название — первое после id, в пределах этого же результата
            var title = ResultTitle.Match(html, m.Index, Math.Min(4000, html.Length - m.Index));
            yield return (id, title.Success ? Unescape(title.Groups[1].Value) : "");
        }
    }

    /// <summary>Первая песня из блока «Музыка» в описании ролика; null — блока нет.</summary>
    internal static YouTubeSong? ParseMusicSection(string html)
    {
        int start = 0;
        while ((start = html.IndexOf("\"horizontalCardListRenderer\"", start, StringComparison.Ordinal)) >= 0)
        {
            int next = html.IndexOf("\"horizontalCardListRenderer\"", start + 1, StringComparison.Ordinal);
            var section = html[start..(next < 0 ? Math.Min(html.Length, start + 20000) : next)];
            start++;
            // Такие же карточки бывают у мест, людей и игр — у музыки значок MUSIC
            if (!section.Contains("\"iconName\":\"MUSIC\"", StringComparison.Ordinal))
                continue;
            int card = section.IndexOf("\"videoAttributeViewModel\"", StringComparison.Ordinal);
            if (card < 0)
                continue;
            var rest = section[card..];
            var title = CardTitle.Match(rest);
            if (!title.Success)
                continue;
            var artist = CardSubtitle.Match(rest);
            var album = CardAlbum.Match(rest);
            var name = Unescape(title.Groups[1].Value).Trim();
            if (name.Length == 0)
                continue;
            return new YouTubeSong(name,
                artist.Success && Unescape(artist.Groups[1].Value).Trim() is { Length: > 0 } a ? a : null,
                album.Success && Unescape(album.Groups[1].Value).Trim() is { Length: > 0 } b ? b : null);
        }
        return null;
    }

    private static string Unescape(string json)
    {
        try { return JsonSerializer.Deserialize<string>("\"" + json + "\"") ?? json; }
        catch (JsonException) { return json; }
    }

    private static string Key(string title, string? channel) => $"{channel}\n{title}";

    private static string Normalize(string s) => Spaces.Replace(s.Trim().ToLowerInvariant(), " ");

    /// <summary>Один запрос на ключ. Ошибку (нет сети, YouTube поменял страницу) не запоминаем — спросим ещё раз.</summary>
    private static Task<T?> Cached<T>(Dictionary<string, Task<T?>> cache, string key, Func<Task<T?>> load) where T : class
    {
        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached))
                return cached;
            if (cache.Count >= CacheLimit)
                cache.Clear();
            var task = Run();
            cache[key] = task;
            return task;
        }

        async Task<T?> Run()
        {
            try
            {
                return await Task.Run(load);
            }
            catch (Exception)
            {
                lock (cache)
                    cache.Remove(key);
                return null;
            }
        }
    }

    private static async Task<byte[]?> GetAsync(string url)
    {
        using var response = await Http.GetAsync(url);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : null;
    }

    /// <summary>Картинка, ужатая до 16×9: для сравнения кадров хватает с запасом.</summary>
    internal static Bitmap? Fingerprint(byte[] image)
    {
        try
        {
            using var source = new Bitmap(new MemoryStream(image));
            var small = new Bitmap(16, 9);
            using var g = Graphics.FromImage(small);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            // Кадр из браузера уже 16:9, а у YouTube бывают 4:3 с полосами — берём середину
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

    /// <summary>Средняя разница по каналам: по одной яркости разные картинки бывают слишком похожи.</summary>
    internal static double Difference(Bitmap a, Bitmap b)
    {
        double sum = 0;
        for (int y = 0; y < 9; y++)
            for (int x = 0; x < 16; x++)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                sum += (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B)) / 3.0;
            }
        return sum / (16 * 9);
    }
}
