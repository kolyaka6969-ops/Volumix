using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Volumix.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

public enum LyricsStatus { Loading, Synced, Plain, Instrumental, NotFound, Error }

public sealed record Lyrics(LyricsStatus Status, IReadOnlyList<LyricLine> Lines)
{
    public static readonly Lyrics Loading = new(LyricsStatus.Loading, Array.Empty<LyricLine>());
    public static readonly Lyrics NotFound = new(LyricsStatus.NotFound, Array.Empty<LyricLine>());
    public static readonly Lyrics Error = new(LyricsStatus.Error, Array.Empty<LyricLine>());
    public static readonly Lyrics Instrumental = new(LyricsStatus.Instrumental, Array.Empty<LyricLine>());

    private static readonly Regex SentenceStart = new(@"(?<=\p{Ll}[,!?…]?)\s+(?=\p{Lu})");

    /// <summary>Синхронный текст из LRC; без меток времени — просто текст.</summary>
    public static Lyrics FromLrc(string lrc)
    {
        var lines = LyricsService.ParseLrc(lrc);
        if (LyricsService.IsSynced(lines))
            return new(LyricsStatus.Synced, lines);
        return FromPlain(lines.Count > 0 ? string.Join("\n", lines.Select(l => l.Text)) : lrc);
    }

    public static Lyrics FromPlain(string text)
    {
        var raw = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => !LyricsService.IsTag(l)).ToList();
        // Весь текст одной строкой: делим там, где после строчной буквы начинается слово с заглавной — обычно это начало строки
        if (raw.Count(l => l.Length > 0) == 1 && raw.First(l => l.Length > 0) is { Length: > 120 } single)
            raw = SentenceStart.Split(single).ToList();
        var lines = raw.Select(l => new LyricLine(TimeSpan.Zero, l)).ToList();
        // Пустые строки по краям только сдвигают текст
        while (lines.Count > 0 && lines[0].Text.Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Text.Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Count > 0 ? new(LyricsStatus.Plain, lines) : NotFound;
    }
}

/// <summary>
/// Тексты песен: сначала свои файлы «Исполнитель - Название.lrc» из папки lyrics, потом
/// <see href="https://lrclib.net">lrclib.net</see> — открытая база синхронных текстов, без ключа и регистрации.
/// </summary>
public static class LyricsService
{
    public static readonly string Folder = Path.Combine(SettingsService.Folder, "lyrics");

    /// <summary>Допуск по длительности: у альбомной версии и сингла она часто расходится на секунду-две.</summary>
    private const double DurationTolerance = 3;
    /// <summary>У клипа на YouTube вступление или титры: длительность ролика расходится с треком на секунды.</summary>
    internal const double VideoDurationTolerance = 12;
    private const int CacheLimit = 50;

    private static readonly HttpClient Http = CreateClient();
    private static readonly HttpClient Fallback = CreateFallback();
    // NetEase ставит cookie на первом ответе и с ними отдаёт пустой поиск — ему отдельный клиент без cookie
    private static readonly HttpClient NetEase = CreateFallback(useCookies: false);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, Lyrics> Cache = new();
    private static readonly Dictionary<string, (Lyrics Lyrics, int Index)> VideoCache = new();

    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]");
    private static readonly Regex LeadingTimeTags = new(@"^(?:\s*\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\])+");
    private static readonly Regex OffsetTag = new(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase);
    private static readonly Regex AnyTag = new(@"^\[[a-z#]+:.*\]$", RegexOptions.IgnoreCase);
    private static readonly Regex WordTimeTag = new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>");
    private static readonly Regex TitleSuffix = new(@"\s+-\s+.*$|\s*[(\[](?:feat|ft|with|prod)\.?\s[^)\]]*[)\]]", RegexOptions.IgnoreCase);
    private static readonly Regex ArtistSeparator = new(@",\s*|\s+&\s+|\s+(?:feat|ft)\.?\s+", RegexOptions.IgnoreCase);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://lrclib.net/api/"), Timeout = TimeSpan.FromSeconds(5) };
        // lrclib просит подписывать запросы названием программы
        var version = typeof(LyricsService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Volumix/{version} (+https://github.com/kolyaka6969-ops/Volumix)");
        return http;
    }

    private static HttpClient CreateFallback(bool useCookies = true)
    {
        var http = new HttpClient(new HttpClientHandler { UseCookies = useCookies }) { Timeout = TimeSpan.FromSeconds(8) };
        var version = typeof(LyricsService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Volumix/{version}");
        return http;
    }

    /// <summary>
    /// Резервная база без ключа и регистрации: отдаёт обычный текст, когда lrclib недоступен.
    /// Синхронных меток там нет, поэтому строки просто подсвечиваются по порядку.
    /// </summary>
    private static async Task<Lyrics> FetchFallbackAsync(string title, string? artist, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Lyrics.Error;
        try
        {
            var url = $"https://api.lyrics.ovh/v1/{Uri.EscapeDataString(FirstArtist(artist))}/{Uri.EscapeDataString(title)}";
            using var response = await Fallback.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Lyrics.NotFound;
            if (!response.IsSuccessStatusCode)
                return Lyrics.Error;
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("lyrics", out var text) || text.ValueKind != JsonValueKind.String)
                return Lyrics.NotFound;
            var raw = text.GetString() ?? "";
            // Французская подпись сервиса: «Paroles de la chanson … par …»
            var lines = raw.Replace("\r", "").Split('\n')
                .Where(line => !line.TrimStart().StartsWith("Paroles de la chanson", StringComparison.OrdinalIgnoreCase));
            var lyrics = Lyrics.FromPlain(string.Join("\n", lines));
            return lyrics.Status == LyricsStatus.NotFound ? Lyrics.NotFound : lyrics;
        }
        catch (Exception)
        {
            return Lyrics.Error;
        }
    }

    /// <summary>
    /// Последняя попытка — Genius: текстов там больше всего, но тоже без меток времени. Ключ не нужен:
    /// поиск идёт через тот же API, что и у их сайта, а текст берётся со страницы песни.
    /// Берём только песню с тем же названием и исполнителем, иначе на обои попадёт чужой текст.
    /// </summary>
    internal static async Task<Lyrics> FetchGeniusAsync(string title, string? artist, CancellationToken ct)
    {
        try
        {
            var query = Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{FirstArtist(artist)} {title}");
            var json = await Fallback.GetStringAsync($"https://genius.com/api/search/song?q={query}&per_page=5", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("sections", out var sections) || sections.GetArrayLength() == 0 ||
                !sections[0].TryGetProperty("hits", out var hits))
                return Lyrics.NotFound;

            int best = 0;
            JsonElement? song = null;
            foreach (var hit in hits.EnumerateArray())
            {
                if (!hit.TryGetProperty("result", out var result))
                    continue;
                var foundArtist = Str(result, "artist_names") ??
                                  (result.TryGetProperty("primary_artist", out var pa) ? Str(pa, "name") : null);
                int score = CoverArtService.Score(title, artist, Str(result, "title"), foundArtist);
                if (score > best)
                    (best, song) = (score, result);
            }
            // Совпало только название — у известных названий («Numb», «Home») это почти всегда другая песня
            if (song is not { } s || best < (string.IsNullOrWhiteSpace(artist) ? 3 : 4))
                return Lyrics.NotFound;
            if (s.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True)
                return Lyrics.Instrumental;
            if (Str(s, "url") is not { } url || !url.StartsWith("https://genius.com/", StringComparison.Ordinal))
                return Lyrics.NotFound;

            var text = ParseGeniusPage(await Fallback.GetStringAsync(url, ct));
            return text == null ? Lyrics.NotFound : Lyrics.FromPlain(text);
        }
        catch (Exception)
        {
            return Lyrics.Error; // нет сети, таймаут, отмена, страница поменялась
        }
    }

    /// <summary>
    /// Текст со страницы Genius: блоки data-lyrics-container без служебных вставок (data-exclude-from-selection),
    /// &lt;br&gt; — перевод строки, заголовки частей «[Chorus: …]» — пустая строка между куплетами.
    /// </summary>
    internal static string? ParseGeniusPage(string html)
    {
        var parts = new List<string>();
        int at = 0;
        while ((at = html.IndexOf("data-lyrics-container=\"true\"", at, StringComparison.Ordinal)) >= 0)
        {
            int start = html.IndexOf('>', at) + 1;
            if (start == 0)
                break;
            int end = ElementEnd(html, start, "div");
            parts.Add(RemoveExcluded(html[start..end]));
            at = end;
        }
        return parts.Count == 0 ? null : HtmlToLyrics(string.Join("<br>", parts));
    }

    /// <summary>
    /// HTML с текстом в строки: &lt;br&gt; — перевод строки, теги и сущности убраны, заголовки частей
    /// «[Chorus: …]», «[Текст песни «…»]» — пустая строка между куплетами.
    /// </summary>
    private static string? HtmlToLyrics(string html)
    {
        // Переводы строк в самом HTML — просто пробелы, строки задают только <br>
        var flat = html.Replace("\r", "").Replace('\n', ' ');
        var text = System.Net.WebUtility.HtmlDecode(HtmlTag.Replace(HtmlBreak.Replace(flat, "\n"), ""));
        // &nbsp; — неразрывный пробел: с ним длинная строка не переносится на обоях
        text = ZeroWidth.Replace(UnicodeSpace.Replace(text, " "), "");
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = Regex.Replace(raw, @"\s{2,}", " ").Trim();
            if (SectionHeader.IsMatch(line))
                line = "";
            // Между куплетами одна пустая строка
            if (line.Length > 0 || (lines.Count > 0 && lines[^1].Length > 0))
                lines.Add(line);
        }
        var result = string.Join("\n", lines).Trim();
        return result.Length > 0 ? result : null;
    }

    /// <summary>
    /// text-lyrics.ru — русская база текстов: там есть песни, которых нет ни на lrclib, ни на Genius, ни на NetEase
    /// (например, «Грель — Прощаться»). Без меток времени. Ищем через поиск сайта, берём только песню с тем же
    /// названием и исполнителем; текст — блок entry_content на странице песни.
    /// </summary>
    internal static async Task<Lyrics> FetchTextLyricsAsync(string title, string? artist, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Lyrics.NotFound; // у сайта в выдаче «Исполнитель - Название», без исполнителя не сверить
        try
        {
            var query = Uri.EscapeDataString($"{FirstArtist(artist)} {title}");
            var page = await Fallback.GetStringAsync($"https://text-lyrics.ru/index.php?do=search&subaction=search&story={query}", ct);

            int best = 0;
            string? url = null;
            foreach (Match m in TextLyricsHit.Matches(page))
            {
                var name = System.Net.WebUtility.HtmlDecode(HtmlTag.Replace(m.Groups["name"].Value, "")).Trim();
                int dash = name.IndexOf(" - ", StringComparison.Ordinal);
                if (dash <= 0)
                    continue;
                int score = CoverArtService.Score(title, artist, name[(dash + 3)..], name[..dash]);
                if (score > best)
                    (best, url) = (score, m.Groups["url"].Value);
            }
            // Совпало только название — у известных названий это почти всегда другая песня
            if (url == null || best < 4)
                return Lyrics.NotFound;

            var text = ParseTextLyricsPage(await Fallback.GetStringAsync(url, ct));
            return text == null ? Lyrics.NotFound : Lyrics.FromPlain(text);
        }
        catch (Exception)
        {
            return Lyrics.Error; // нет сети, таймаут, отмена, сайт поменялся
        }
    }

    private static readonly Regex TextLyricsHit = new(
        @"<h2 class=""title""><a href=""(?<url>https://text-lyrics\.ru/[^""]+\.html)"">(?<name>.*?)</a>", RegexOptions.Singleline);

    internal static string? ParseTextLyricsPage(string html)
    {
        int at = html.IndexOf("id=\"entry_content\"", StringComparison.Ordinal);
        if (at < 0)
            return null;
        int start = html.IndexOf('>', at) + 1;
        return start == 0 ? null : HtmlToLyrics(html[start..ElementEnd(html, start, "div")]);
    }

    private static readonly Regex HtmlBreak = new(@"<br\s*/?>", RegexOptions.IgnoreCase);
    private static readonly Regex HtmlTag = new(@"<[^>]*>");
    private static readonly Regex SectionHeader = new(@"^\[[^\]]*\]$");

    /// <summary>Индекс закрывающего тега элемента, содержимое которого начинается с <paramref name="start"/>.</summary>
    private static int ElementEnd(string html, int start, string tag)
    {
        int depth = 1, i = start;
        while (true)
        {
            int open = html.IndexOf("<" + tag, i, StringComparison.OrdinalIgnoreCase);
            int close = html.IndexOf("</" + tag, i, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
                return html.Length;
            if (open >= 0 && open < close)
            {
                depth++;
                i = open + tag.Length + 1;
            }
            else
            {
                if (--depth == 0)
                    return close;
                i = close + tag.Length + 2;
            }
        }
    }

    /// <summary>Вырезает элементы с data-exclude-from-selection: «107 Contributors», переводы, «Read More».</summary>
    private static string RemoveExcluded(string html)
    {
        const string marker = "data-exclude-from-selection=\"true\"";
        int at;
        while ((at = html.IndexOf(marker, StringComparison.Ordinal)) >= 0)
        {
            int tagStart = html.LastIndexOf('<', at);
            int nameEnd = html.IndexOfAny(new[] { ' ', '>' }, tagStart + 1);
            int contentStart = html.IndexOf('>', at) + 1;
            if (tagStart < 0 || nameEnd < 0 || contentStart == 0)
                break;
            var tag = html[(tagStart + 1)..nameEnd];
            int close = ElementEnd(html, contentStart, tag);
            int after = html.IndexOf('>', close);
            html = html[..tagStart] + (after < 0 ? "" : html[(after + 1)..]);
        }
        return html;
    }

    /// <summary>
    /// Вторая очередь вместе с Genius — NetEase Cloud Music: огромный каталог, в том числе западный,
    /// и часто синхронный текст. Ключ не нужен. Берём только песню с тем же названием и исполнителем;
    /// метки времени — только если длительность сходится, иначе они бы врали.
    /// </summary>
    internal static async Task<Lyrics> FetchNetEaseAsync(string title, string? artist, double seconds, double tolerance, CancellationToken ct)
    {
        try
        {
            var query = Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{FirstArtist(artist)} {title}");
            var json = await NetEase.GetStringAsync($"https://music.163.com/api/search/get?s={query}&type=1&limit=8", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array)
                return Lyrics.NotFound;

            // Совпадения по названию и исполнителю, лучшие первыми, среди равных — ближайшие по длительности.
            // Совпало только название — у известных названий («Numb», «Home») это почти всегда другая песня.
            int threshold = string.IsNullOrWhiteSpace(artist) ? 3 : 4;
            var matches = songs.EnumerateArray().Select(song =>
                {
                    var artists = song.TryGetProperty("artists", out var list) && list.ValueKind == JsonValueKind.Array
                        ? string.Join(", ", list.EnumerateArray().Select(a => Str(a, "name")).Where(n => n != null))
                        : null;
                    double length = song.TryGetProperty("duration", out var d) && d.TryGetDouble(out var ms) ? ms / 1000 : 0;
                    long id = song.TryGetProperty("id", out var i) && i.TryGetInt64(out var v) ? v : 0;
                    return (Id: id, Length: length, Score: CoverArtService.Score(title, artist, Str(song, "name"), artists));
                })
                .Where(m => m.Id != 0 && m.Score >= threshold)
                .OrderByDescending(m => m.Score)
                .ThenBy(m => seconds > 0 && m.Length > 0 ? Math.Abs(m.Length - seconds) : 0)
                .Take(3)
                .ToList();

            // У части версий (live, ремастер) текста на NetEase нет — берём первую, где он есть
            string? lrc = null;
            double duration = 0;
            foreach (var match in matches)
            {
                using var lyricDoc = JsonDocument.Parse(await NetEase.GetStringAsync($"https://music.163.com/api/song/lyric?id={match.Id}&lv=1", ct));
                var root = lyricDoc.RootElement;
                if (root.TryGetProperty("pureMusic", out var pure) && pure.ValueKind == JsonValueKind.True)
                    return Lyrics.Instrumental;
                lrc = root.TryGetProperty("lrc", out var l) ? Str(l, "lyric") : null;
                duration = match.Length;
                if (!string.IsNullOrWhiteSpace(lrc))
                    break;
            }
            if (string.IsNullOrWhiteSpace(lrc))
                return Lyrics.NotFound;
            // NetEase ставит между словами неразрывные пробелы — с ними длинная строка не переносится на обоях
            lrc = ZeroWidth.Replace(UnicodeSpace.Replace(lrc, " "), "");

            // В начале идут авторы: «作词 : …», «作曲 : …», «Producer: …» — это не текст песни
            var lines = ParseLrc(lrc).Where(line => !CreditLine.IsMatch(line.Text)).ToList();
            while (lines.Count > 0 && lines[0].Text.Length == 0)
                lines.RemoveAt(0);
            bool timed = seconds <= 0 || duration <= 0 || Math.Abs(duration - seconds) <= tolerance;
            if (timed && IsSynced(lines))
                return new(LyricsStatus.Synced, lines);
            return lines.Count > 0 ? Lyrics.FromPlain(string.Join("\n", lines.Select(x => x.Text))) : Lyrics.NotFound;
        }
        catch (Exception)
        {
            return Lyrics.Error; // нет сети, таймаут, отмена, кривой ответ
        }
    }

    private static readonly Regex UnicodeSpace = new(@"\p{Zs}");
    private static readonly Regex ZeroWidth = new(@"[​-‍⁠﻿]");

    private static readonly Regex CreditLine = new(
        @"^\s*(?:作词|作曲|编曲|制作人|制作|混音|母带|和声|吉他|贝斯|鼓|录音|监制|出品|发行|词|曲|" +
        @"lyrics?(?:\s+by)?|lyricist|written\s+by|composer|composed\s+by|producers?|produced\s+by|arranger|mix(?:ing|ed)?(?:\s+by)?|master(?:ing|ed)?(?:\s+by)?)\s*[:：]",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Вторая очередь: Genius, NetEase и text-lyrics.ru одновременно. Синхронный текст NetEase лучше всего,
    /// потом полный текст Genius, потом text-lyrics.ru, потом текст NetEase без меток.
    /// </summary>
    internal static async Task<Lyrics> FetchSecondTierAsync(string title, string? artist, double seconds, double tolerance, CancellationToken ct)
    {
        var genius = FetchGeniusAsync(title, artist, ct);
        var netease = FetchNetEaseAsync(title, artist, seconds, tolerance, ct);
        var textLyrics = FetchTextLyricsAsync(title, artist, ct);
        await Task.WhenAll(genius, netease, textLyrics);
        return PickSecondTier(genius.Result, netease.Result, textLyrics.Result);
    }

    internal static Lyrics PickSecondTier(Lyrics genius, Lyrics netease, Lyrics? textLyrics = null)
    {
        textLyrics ??= Lyrics.NotFound;
        if (netease.Status == LyricsStatus.Synced)
            return netease;
        foreach (var plain in new[] { genius, textLyrics, netease })
            if (plain.Status == LyricsStatus.Plain)
                return plain;
        if (genius.Status == LyricsStatus.Instrumental || netease.Status == LyricsStatus.Instrumental)
            return Lyrics.Instrumental;
        // Ошибка — только если не ответил никто: «не нашли» хотя бы у одного запоминается
        return new[] { genius, netease, textLyrics }.All(l => l.Status == LyricsStatus.Error) ? Lyrics.Error : Lyrics.NotFound;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Никогда не бросает исключений: при сбое сети вернёт <see cref="Lyrics.Error"/>.</summary>
    public static async Task<Lyrics> FindAsync(string title, string? artist, string? album, TimeSpan duration, CancellationToken ct)
    {
        // Свои файлы не кэшируем: их могут положить, пока песня играет
        if (FindLocal(title, artist) is { } local)
            return local;

        var key = $"{artist}\n{title}\n{(int)duration.TotalSeconds}";
        lock (Cache)
            if (Cache.TryGetValue(key, out var cached))
                return cached;

        // Первая очередь — lrclib и lyrics.ovh одновременно: резервная база не ждёт, пока lrclib ответит.
        // Её текст нужен, только если lrclib не знает песню или недоступен (5xx).
        using var tier1 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ovh = FetchFallbackAsync(title, artist, tier1.Token);
        Lyrics lyrics;
        try { lyrics = await FetchAsync(title, artist, album, duration, ct); }
        catch (Exception) { lyrics = Lyrics.Error; } // нет сети, таймаут, отмена, кривой ответ

        if (lyrics.Status is LyricsStatus.Error or LyricsStatus.NotFound)
            lyrics = await ovh;
        else
            tier1.Cancel();

        // Вторая очередь — Genius и NetEase, только когда первая ничего не нашла
        if (lyrics.Status is LyricsStatus.Error or LyricsStatus.NotFound)
        {
            var second = await FetchSecondTierAsync(title, artist, Math.Round(duration.TotalSeconds), DurationTolerance, ct);
            if (second.Status != LyricsStatus.Error || lyrics.Status == LyricsStatus.Error)
                lyrics = second;
        }

        if (lyrics.Status != LyricsStatus.Error)
            lock (Cache)
            {
                if (Cache.Count >= CacheLimit)
                    Cache.Clear();
                Cache[key] = lyrics;
            }
        return lyrics;
    }

    /// <summary>
    /// Текст для ролика YouTube. Название ролика редко совпадает с названием песни, поэтому прочтений несколько
    /// (<paramref name="candidates"/>, самое вероятное первым). Все они ищутся на lrclib одновременно, синхронный
    /// текст любого прочтения важнее обычного текста, а резервная база без меток времени — только если lrclib
    /// не нашёл ничего. Возвращает текст и номер прочтения, по которому он нашёлся. Никогда не бросает исключений.
    /// </summary>
    public static async Task<(Lyrics Lyrics, int Index)> FindVideoAsync(
        IReadOnlyList<(string Title, string? Artist)> candidates, TimeSpan duration, CancellationToken ct)
    {
        for (int i = 0; i < candidates.Count; i++)
            if (FindLocal(candidates[i].Title, candidates[i].Artist) is { } local)
                return (local, i);
        if (candidates.Count == 0)
            return (Lyrics.NotFound, 0);

        double seconds = Math.Round(duration.TotalSeconds);
        var key = $"{(int)seconds}\n" + string.Join("\n", candidates.Select(c => $"{c.Artist}\t{c.Title}"));
        lock (VideoCache)
            if (VideoCache.TryGetValue(key, out var cached))
                return cached;

        // Сначала пробуем основное (наиболее точное) прочтение: если синхронный текст уже есть,
        // не шлём лишние запросы к lrclib и исключаем ошибку 503 ServerOverloaded
        if (candidates.Count > 1)
        {
            var firstTracks = await SearchAsync(candidates[0].Title, candidates[0].Artist, ct);
            if (firstTracks != null && Pick(firstTracks, seconds, VideoDurationTolerance) is { Status: LyricsStatus.Synced } firstSynced)
            {
                var r = (firstSynced, 0);
                lock (VideoCache)
                {
                    if (VideoCache.Count >= CacheLimit)
                        VideoCache.Clear();
                    VideoCache[key] = r;
                }
                return r;
            }
        }

        // Первая очередь: lrclib и резервная база сразу для всех прочтений.
        // Точный запрос get на lrclib тут не помогает (длительность ролика не та) и медленный — только поиск.
        using var tier1 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fallbacks = candidates.Select(c => FetchFallbackAsync(c.Title, c.Artist, tier1.Token)).ToArray();
        var searches = candidates.Select(c => SearchAsync(c.Title, c.Artist, ct)).ToArray();
        List<LrcLibTrack>?[] results;
        try { results = await Task.WhenAll(searches); }
        catch (Exception) { results = searches.Select(t => t.IsCompletedSuccessfully ? t.Result : null).ToArray(); }

        var result = PickVideo(results, seconds);
        if (result != null)
            tier1.Cancel();
        else
        {
            // lrclib не знает ни одного прочтения — берём текст из резервной базы
            var texts = await Task.WhenAll(fallbacks);
            int found = Array.FindIndex(texts, l => l.Status is LyricsStatus.Plain or LyricsStatus.Instrumental);
            if (found < 0)
            {
                // И там нет — вторая очередь (Genius и NetEase), тоже сразу для всех прочтений.
                // Синхронный текст любого прочтения важнее обычного.
                var second = await Task.WhenAll(candidates.Select(c =>
                    FetchSecondTierAsync(c.Title, c.Artist, seconds, VideoDurationTolerance, ct)));
                found = Array.FindIndex(second, l => l.Status == LyricsStatus.Synced);
                if (found < 0)
                    found = Array.FindIndex(second, l => l.Status is LyricsStatus.Plain or LyricsStatus.Instrumental);
                texts = texts.Zip(second, (ovh, s) => s.Status == LyricsStatus.Error ? ovh : s).ToArray();
            }
            bool anyFailed = results.Any(r => r == null) && texts.All(l => l.Status != LyricsStatus.NotFound);
            result = found >= 0 ? (texts[found], found) : (anyFailed ? Lyrics.Error : Lyrics.NotFound, 0);
        }

        // «Не нашлось», когда часть запросов к lrclib сорвалась, тоже не запоминаем: за долгий джем
        // lrclib порой отвечает отказами, и песня иначе так и осталась бы без текста
        var status = result.Value.Lyrics.Status;
        if (status != LyricsStatus.Error && !(status == LyricsStatus.NotFound && results.Any(r => r == null)))
            lock (VideoCache)
            {
                if (VideoCache.Count >= CacheLimit)
                    VideoCache.Clear();
                VideoCache[key] = result.Value;
            }
        return result.Value;
    }

    /// <summary>
    /// Выбор между прочтениями ролика: сначала синхронный текст с подходящей длительностью (в порядке прочтений),
    /// потом обычный с подходящей длительностью, потом любой найденный текст без меток времени.
    /// </summary>
    internal static (Lyrics Lyrics, int Index)? PickVideo(IReadOnlyList<List<LrcLibTrack>?> results, double seconds)
    {
        var picked = results.Select(r => r == null ? null : Pick(r, seconds, VideoDurationTolerance)).ToList();
        int synced = picked.FindIndex(l => l?.Status == LyricsStatus.Synced);
        if (synced >= 0)
            return (picked[synced]!, synced);
        int any = picked.FindIndex(l => l != null);
        if (any >= 0)
            return (picked[any]!, any);
        // Длительность не совпала ни у кого (длинный клип, live) — синхронизация не сойдётся, но сам текст верный
        for (int i = 0; i < results.Count; i++)
        {
            var plain = results[i]?.Select(ToLyrics).FirstOrDefault(l => l != null);
            if (plain != null)
                return (plain.Status == LyricsStatus.Synced ? Lyrics.FromPlain(string.Join("\n", plain.Lines.Select(l => l.Text))) : plain, i);
        }
        return null;
    }

    private static async Task<List<LrcLibTrack>?> SearchAsync(string title, string? artist, CancellationToken ct)
    {
        try
        {
            var query = artist == null ? Query(("track_name", title)) : Query(("track_name", title), ("artist_name", artist));
            using var response = await Http.GetAsync("search?" + query, ct);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(350, ct);
                    using var retry = await Http.GetAsync("search?" + query, ct);
                    if (retry.IsSuccessStatusCode)
                        return JsonSerializer.Deserialize<List<LrcLibTrack>>(await retry.Content.ReadAsStringAsync(ct), Json) ?? new();
                }
                return null;
            }
            return JsonSerializer.Deserialize<List<LrcLibTrack>>(await response.Content.ReadAsStringAsync(ct), Json) ?? new();
        }
        catch (Exception)
        {
            return null; // нет сети, таймаут, отмена
        }
    }

    /// <summary>Свой файл можно положить для песен, которых нет на lrclib, или чтобы поправить чужой.</summary>
    internal static Lyrics? FindLocal(string title, string? artist)
    {
        try
        {
            if (!Directory.Exists(Folder))
                return null;
            var names = artist == null ? new[] { title } : new[] { $"{artist} - {title}", $"{FirstArtist(artist)} - {title}", title };
            foreach (var name in names.Distinct())
            {
                var path = Path.Combine(Folder, SafeFileName(name) + ".lrc");
                if (File.Exists(path))
                    return Lyrics.FromLrc(File.ReadAllText(path));
                path = Path.ChangeExtension(path, ".txt");
                if (File.Exists(path))
                    return Lyrics.FromPlain(File.ReadAllText(path));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    internal static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim().TrimEnd('.');
    }

    private static async Task<Lyrics> FetchAsync(string title, string? artist, string? album, TimeSpan duration, CancellationToken ct)
    {
        double seconds = Math.Round(duration.TotalSeconds);
        // Текст без синхронизации берём, только если синхронного так и не найдётся
        Lyrics? best = null;

        // Точное совпадение. Если трека нет в базе, lrclib ищет его у других сервисов и под нагрузкой отвечает 503 —
        // это не ошибка, просто идём в обычный поиск.
        if (artist != null && seconds > 0)
        {
            var query = Query(("track_name", title), ("artist_name", artist), ("album_name", album ?? ""), ("duration", seconds.ToString(CultureInfo.InvariantCulture)));
            using var response = await Http.GetAsync("get?" + query, ct);
            if (response.IsSuccessStatusCode)
            {
                var track = JsonSerializer.Deserialize<LrcLibTrack>(await response.Content.ReadAsStringAsync(ct), Json);
                best = track == null ? null : ToLyrics(track);
                if (best?.Status == LyricsStatus.Synced)
                    return best;
            }
        }

        // Поиск: другой альбом, «feat.» в названии, «- Remastered 2011» и несколько исполнителей через запятую
        var variants = new List<(string Title, string? Artist)> { (title, artist) };
        var shortTitle = TitleSuffix.Replace(title, "").Trim();
        var firstArtist = artist == null ? null : FirstArtist(artist);
        if (shortTitle.Length > 0 && (shortTitle != title || firstArtist != artist))
            variants.Add((shortTitle, firstArtist));

        bool failed = false;
        foreach (var (t, a) in variants)
        {
            var query = a == null ? Query(("track_name", t)) : Query(("track_name", t), ("artist_name", a));
            using var response = await Http.GetAsync("search?" + query, ct);
            if (!response.IsSuccessStatusCode)
            {
                failed = true;
                continue;
            }
            var found = JsonSerializer.Deserialize<List<LrcLibTrack>>(await response.Content.ReadAsStringAsync(ct), Json);
            var lyrics = found == null ? null : Pick(found, seconds, DurationTolerance);
            if (lyrics?.Status == LyricsStatus.Synced)
                return lyrics;
            best ??= lyrics;
        }
        return best ?? (failed ? Lyrics.Error : Lyrics.NotFound);
    }

    /// <summary>Лучший из найденных: подходящий по длительности, синхронный, потом просто текст.</summary>
    private static Lyrics? Pick(List<LrcLibTrack> found, double seconds, double tolerance) =>
        found.Where(t => seconds <= 0 || Math.Abs(t.Duration - seconds) <= tolerance)
            .Select(t => (Track: t, Lyrics: ToLyrics(t)))
            .Where(x => x.Lyrics != null)
            .OrderBy(x => x.Lyrics!.Status switch { LyricsStatus.Synced => 0, LyricsStatus.Plain => 1, _ => 2 })
            .ThenBy(x => Math.Abs(x.Track.Duration - seconds))
            .Select(x => x.Lyrics)
            .FirstOrDefault();

    private static Lyrics? ToLyrics(LrcLibTrack track)
    {
        var synced = string.IsNullOrWhiteSpace(track.SyncedLyrics) ? new List<LyricLine>() : ParseLrc(track.SyncedLyrics);
        if (IsSynced(synced))
            return new(LyricsStatus.Synced, synced);
        if (!string.IsNullOrWhiteSpace(track.PlainLyrics))
            return Lyrics.FromPlain(track.PlainLyrics);
        if (synced.Count > 0)
            return Lyrics.FromPlain(string.Join("\n", synced.Select(l => l.Text)));
        return track.Instrumental ? Lyrics.Instrumental : null;
    }

    /// <summary>На lrclib попадается «синхронный» текст, где всё одной строкой с меткой 00:00 — это не синхронизация.</summary>
    internal static bool IsSynced(IReadOnlyList<LyricLine> lines) =>
        lines.Select(l => l.Time).Distinct().Count() >= 3;

    private static string Query(params (string Name, string Value)[] items) =>
        string.Join("&", items.Select(i => i.Name + "=" + Uri.EscapeDataString(i.Value)));

    private static string FirstArtist(string artist) => ArtistSeparator.Split(artist)[0].Trim();

    internal static bool IsTag(string line) => AnyTag.IsMatch(line);

    /// <summary>
    /// Разбирает LRC: «[01:23.45]строка», несколько меток на одной строке, [offset:±мс],
    /// пословные метки &lt;01:23.45&gt; (убираются). Пустые строки остаются — это паузы в песне.
    /// </summary>
    public static List<LyricLine> ParseLrc(string lrc)
    {
        var result = new List<LyricLine>();
        var offset = TimeSpan.Zero;
        foreach (var raw in lrc.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (OffsetTag.Match(line) is { Success: true } o)
            {
                // Положительный offset — текст должен появляться раньше
                offset = TimeSpan.FromMilliseconds(int.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture));
                continue;
            }
            var tags = LeadingTimeTags.Match(line);
            if (!tags.Success)
                continue;

            var text = WordTimeTag.Replace(line[tags.Length..], "").Trim();
            text = Regex.Replace(text, @"\s{2,}", " ");
            foreach (Match m in TimeTag.Matches(tags.Value))
            {
                var fraction = m.Groups[3].Success
                    ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / Math.Pow(10, m.Groups[3].Value.Length)
                    : 0;
                var time = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                           + TimeSpan.FromSeconds(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) + fraction);
                result.Add(new LyricLine(time, text));
            }
        }

        if (offset != TimeSpan.Zero)
            for (int i = 0; i < result.Count; i++)
                result[i] = result[i] with { Time = result[i].Time - offset < TimeSpan.Zero ? TimeSpan.Zero : result[i].Time - offset };

        // Устойчивая сортировка: у повторяющихся припевов метки часто идут не по порядку
        var sorted = result.Select((l, i) => (l, i)).OrderBy(x => x.l.Time).ThenBy(x => x.i).Select(x => x.l).ToList();
        // Пустые строки в начале и подряд ничего не дают
        var clean = new List<LyricLine>(sorted.Count);
        foreach (var l in sorted)
            if (l.Text.Length > 0 || (clean.Count > 0 && clean[^1].Text.Length > 0))
                clean.Add(l);
        return clean;
    }

    internal sealed class LrcLibTrack
    {
        public double Duration { get; set; }
        public bool Instrumental { get; set; }
        public string? PlainLyrics { get; set; }
        public string? SyncedLyrics { get; set; }
    }
}
