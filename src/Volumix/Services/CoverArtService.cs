using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Volumix.Services;

/// <summary>
/// Ищет обложку альбома по исполнителю и названию. Нужна для YouTube: браузер отдаёт в медиа-сеансе
/// кадр из ролика, а на рабочем столе должна висеть обложка трека, а не стоп-кадр.
/// </summary>
internal static class CoverArtService
{
    private const int CacheLimit = 200;

    // Три каталога по очереди на каждое прочтение — Deezer, Apple Music и Genius
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static readonly HttpClient Http = CreateHttp();
    private static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Обложка альбома или null, если подходящей не нашлось. Результат (в том числе пустой) кешируется.</summary>
    public static Task<byte[]?> FindAsync(string title, string? artist, CancellationToken ct = default) =>
        FindAsync(new[] { (Title: title, Artist: artist) }, ct);

    /// <summary>
    /// Перебирает варианты «название — исполнитель» в рамках одного бюджета времени: у YouTube они
    /// бывают перемешаны, и ждать полный таймаут на каждом варианте слишком долго.
    /// </summary>
    public static async Task<byte[]?> FindAsync(
        IReadOnlyList<(string Title, string? Artist)> candidates, CancellationToken ct = default) =>
        (await LookUpAsync(candidates, ct)).Art;

    /// <summary>
    /// То же, но ещё и сообщает, был ли поиск неполным (каталог не ответил, не хватило времени):
    /// тогда пустой ответ не значит «обложки нет», и искать стоит ещё раз.
    /// </summary>
    public static async Task<(byte[]? Art, bool Failed)> LookUpAsync(
        IReadOnlyList<(string Title, string? Artist)> candidates, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Budget);
        bool failed = false;
        foreach (var (title, artist) in candidates)
        {
            if (cts.IsCancellationRequested)
                return (null, true);
            var (art, oneFailed) = await FindOneAsync(title, artist, cts.Token);
            if (art != null)
                return (art, false);
            failed |= oneFailed;
        }
        return (null, failed || cts.IsCancellationRequested);
    }

    private static async Task<(byte[]? Art, bool Failed)> FindOneAsync(string title, string? artist, CancellationToken ct)
    {
        var key = $"{Norm(artist)}\n{Norm(title)}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
                return (cached, false);
        }

        byte[]? art = null;
        bool failed = false;
        try
        {
            art = await FromDeezerAsync(title, artist, ct);
        }
        catch (Exception)
        {
            // Deezer не ответил — пробуем Apple Music.
            failed = true;
        }
        if (art == null && !ct.IsCancellationRequested)
        {
            try
            {
                art = await FromITunesAsync(title, artist, ct);
            }
            catch (Exception)
            {
                // Apple Music не ответил — остаётся Genius
                failed = true;
            }
        }
        if (art == null && !ct.IsCancellationRequested)
        {
            try
            {
                art = await FromGeniusAsync(title, artist, ct);
            }
            catch (Exception)
            {
                // Обложки не будет: покажем то, что дал браузер.
                failed = true;
            }
        }

        // Прерванный поиск и отказ сервиса не запоминаем: за часы прослушивания каталоги порой упираются
        // в лимит запросов, и «не нашлось» тогда навсегда закрыло бы обои для этих песен.
        if (art != null || (!ct.IsCancellationRequested && !failed))
        {
            lock (Cache)
            {
                if (Cache.Count >= CacheLimit)
                    Cache.Clear();
                Cache[key] = art;
            }
        }
        return (art, art == null && (failed || ct.IsCancellationRequested));
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Volumix/{typeof(CoverArtService).Assembly.GetName().Version?.ToString(3)}");
        return http;
    }

    private static async Task<byte[]?> FromDeezerAsync(string title, string? artist, CancellationToken ct)
    {
        var query = Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}");
        var json = await Http.GetStringAsync($"https://api.deezer.com/search?q={query}&limit=6", ct);
        using var doc = JsonDocument.Parse(json);
        // Превышение лимита Deezer отдаёт с кодом 200 и полем error
        if (doc.RootElement.TryGetProperty("error", out _))
            throw new HttpRequestException("Deezer: " + doc.RootElement.GetProperty("error"));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;

        var best = 0;
        string? bestUrl = null;
        foreach (var item in data.EnumerateArray())
        {
            var candidateTitle = Str(item, "title");
            var candidateArtist = item.TryGetProperty("artist", out var a) ? Str(a, "name") : null;
            var score = Score(title, artist, candidateTitle, candidateArtist);
            if (score <= best || score < 3 || !item.TryGetProperty("album", out var album))
                continue;
            best = score;
            bestUrl = Str(album, "cover_xl") ?? Str(album, "cover_big") ?? Str(album, "cover_medium");
        }
        return bestUrl == null ? null : await DownloadAsync(bestUrl, ct);
    }

    private static async Task<byte[]?> FromITunesAsync(string title, string? artist, CancellationToken ct)
    {
        var query = Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}");
        var json = await Http.GetStringAsync(
            $"https://itunes.apple.com/search?term={query}&media=music&entity=song&limit=8", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return null;

        var best = 0;
        string? bestUrl = null;
        foreach (var item in results.EnumerateArray())
        {
            var score = Score(title, artist, Str(item, "trackName"), Str(item, "artistName"));
            var art = Str(item, "artworkUrl100");
            if (score <= best || score < 3 || art == null)
                continue;
            best = score;
            // 100x100bb.jpg — превью; тот же файл в 600x600bb.jpg заметно приятнее на обоях.
            bestUrl = art.Replace("100x100bb", "600x600bb", StringComparison.OrdinalIgnoreCase);
        }
        return bestUrl == null ? null : await DownloadAsync(bestUrl, ct);
    }

    /// <summary>
    /// Genius: обложки есть почти у всех песен, у которых там есть текст, — в том числе у тех, что не продаются
    /// в Deezer и Apple Music. Как и с текстом, берём только песню с тем же названием и исполнителем.
    /// </summary>
    private static async Task<byte[]?> FromGeniusAsync(string title, string? artist, CancellationToken ct)
    {
        var query = Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}");
        var json = await Http.GetStringAsync($"https://genius.com/api/search/song?q={query}&per_page=5", ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("sections", out var sections) || sections.GetArrayLength() == 0 ||
            !sections[0].TryGetProperty("hits", out var hits))
            return null;

        var best = 0;
        string? bestUrl = null;
        foreach (var hit in hits.EnumerateArray())
        {
            if (!hit.TryGetProperty("result", out var result))
                continue;
            var foundArtist = Str(result, "artist_names") ??
                              (result.TryGetProperty("primary_artist", out var pa) ? Str(pa, "name") : null);
            var score = Score(title, artist, Str(result, "title"), foundArtist);
            var art = Str(result, "song_art_image_url");
            // У песен без своей обложки Genius подставляет общую заглушку
            if (score <= best || art == null || art.Contains("default_cover", StringComparison.OrdinalIgnoreCase))
                continue;
            best = score;
            bestUrl = art;
        }
        // Совпало только название — у известных названий («Numb», «Home») это почти всегда другая песня
        if (best < (string.IsNullOrWhiteSpace(artist) ? 3 : 4))
            return null;
        return bestUrl == null ? null : await DownloadAsync(bestUrl, ct);
    }

    private static async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length is > 512 and < 8 * 1024 * 1024 && IsImage(bytes) ? bytes : null;
    }

    private static bool IsImage(byte[] bytes) =>
        (bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8) ||
        (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47);

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Насколько найденный вариант похож на искомую песню. Ноль — это другая песня.</summary>
    internal static int Score(string title, string? artist, string? candidateTitle, string? candidateArtist)
    {
        var wanted = Norm(title);
        var found = Norm(candidateTitle);
        if (wanted.Length < 2 || found.Length < 2)
            return 0;
        int score;
        if (wanted == found)
            score = 3;
        else if (wanted.Length >= 5 && (wanted.Contains(found) || found.Contains(wanted)))
            score = 2;
        else
            return 0;

        var wantedArtist = Norm(artist);
        var foundArtist = Norm(candidateArtist);
        if (wantedArtist.Length > 0 && foundArtist.Length > 0)
        {
            if (wantedArtist == foundArtist)
                score += 3;
            else if (wantedArtist.Contains(foundArtist) || foundArtist.Contains(wantedArtist))
                score += 2;
        }
        return score;
    }

    /// <summary>Сравниваем без регистра, знаков и «ё»: «Ёлка» и «елка» — одно и то же.</summary>
    internal static string Norm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (ch == 'ё')
            {
                builder.Append('е');
            }
            else if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }
        return builder.ToString().Trim();
    }
}
