using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Volumix.Services;

/// <summary>
/// Обложка ролика YouTube в полном размере — для неофициальных клипов, каверов и просто видео, которых нет
/// в каталогах обложек. Браузер присылает кадр размером со спичечный коробок (150×83), а id ролика не сообщает,
/// поэтому ролик ищем по названию и каналу и сверяем найденное с кадром из браузера.
/// </summary>
internal static class YouTubeService
{
    /// <summary>Сколько первых результатов поиска сверять с кадром из браузера.</summary>
    private const int Candidates = 4;
    /// <summary>Средняя разница цвета (0–255), при которой картинки ещё считаются одним кадром.</summary>
    private const double MaxDifference = 28;
    private const int CacheLimit = 100;

    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, byte[]?> Cache = new();
    private static readonly Regex VideoId = new(@"""videoId"":""([\w-]{11})""", RegexOptions.Compiled);

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
    /// Кадр ролика 1280×720 (или 480×360, если большого нет). null — ролик не нашёлся или кадр из браузера
    /// не похож ни на один найденный: так чужой клип не подставится. Никогда не бросает исключений.
    /// </summary>
    public static async Task<byte[]?> FindCoverAsync(string title, string? channel, byte[] thumbnail, CancellationToken ct = default)
    {
        var key = $"{channel}\n{title}";
        lock (Cache)
            if (Cache.TryGetValue(key, out var cached))
                return cached;

        byte[]? cover;
        try { cover = await SearchAsync(title, channel, thumbnail, ct); }
        catch (Exception) { return null; } // нет сети, отмена, YouTube поменял страницу — в кэш не кладём

        lock (Cache)
        {
            if (Cache.Count >= CacheLimit)
                Cache.Clear();
            Cache[key] = cover;
        }
        return cover;
    }

    private static async Task<byte[]?> SearchAsync(string title, string? channel, byte[] thumbnail, CancellationToken ct)
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

        // maxres есть не у всех роликов; hq — с чёрными полосами, но всё равно лучше крошечного
        return await GetAsync($"https://i.ytimg.com/vi/{best}/maxresdefault.jpg", ct)
               ?? await GetAsync($"https://i.ytimg.com/vi/{best}/hqdefault.jpg", ct);
    }

    private static async Task<byte[]?> GetAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
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
