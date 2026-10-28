using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
    private const int CacheLimit = 200;

    private static readonly HttpClient Http = CreateClient();
    private static readonly HttpClient Fallback = CreateFallback();
    // NetEase ставит cookie на первом ответе и с ними отдаёт пустой поиск — ему отдельный клиент без cookie
    private static readonly HttpClient NetEase = CreateFallback(useCookies: false);
    private static readonly HttpClient Kugou = CreateFallback(useCookies: false);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, Lyrics> Cache = new();
    private static readonly Dictionary<string, (Lyrics Lyrics, int Index)> VideoCache = new();

    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]");
    private static readonly Regex LeadingTimeTags = new(@"^(?:\s*\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\])+");
    private static readonly Regex OffsetTag = new(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase);
    private static readonly Regex AnyTag = new(@"^\[[a-z#]+:.*\]$", RegexOptions.IgnoreCase);
    private static readonly Regex WordTimeTag = new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>");
    private static readonly Regex TitleSuffix = new(@"\s+-\s+.*$|\s*[(\[](?:feat|ft|with|prod)\.?\s[^)\]]*[)\]]", RegexOptions.IgnoreCase);
    private static readonly Regex LeadingArticle = new(@"^the\s+(?=\S)", RegexOptions.IgnoreCase);
    private static readonly Regex ArtistSeparator = new(@",\s*|\s+&\s+|\s+(?:feat|ft)\.?\s+", RegexOptions.IgnoreCase);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(CreateHandler(useCookies: true))
        {
            BaseAddress = new Uri("https://lrclib.net/api/"),
            Timeout = TimeSpan.FromSeconds(4),
            // Все запросы песни идут по одному соединению HTTP/2, без лишних рукопожатий TLS
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        // lrclib просит подписывать запросы названием программы
        var version = typeof(LyricsService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Volumix/{version} (+https://github.com/kolyaka6969-ops/Volumix)");
        return http;
    }

    /// <summary>
    /// Ответы сжатые: поиск lrclib — десятки текстов целиком (сотни КБ), страница Genius — ещё больше.
    /// Соединение держится открытым между песнями и раз в несколько минут обновляется (смена сети, DNS).
    /// </summary>
    private static SocketsHttpHandler CreateHandler(bool useCookies) => new()
    {
        UseCookies = useCookies,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    private static HttpClient CreateFallback(bool useCookies = true)
    {
        var http = new HttpClient(CreateHandler(useCookies)) { Timeout = TimeSpan.FromSeconds(4) };
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3.5));
            var query = Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{FirstArtist(artist)} {title}");
            var json = await Fallback.GetStringAsync($"https://genius.com/api/search/song?q={query}&per_page=5", cts.Token);
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

            var text = ParseGeniusPage(await Fallback.GetStringAsync(url, cts.Token));
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3.5));
            var query = Uri.EscapeDataString($"{FirstArtist(artist)} {title}");
            var page = await Fallback.GetStringAsync($"https://text-lyrics.ru/index.php?do=search&subaction=search&story={query}", cts.Token);

            int best = 0;
            string? url = null;
            foreach (Match m in TextLyricsHit.Matches(page))
            {
                var name = System.Net.WebUtility.HtmlDecode(HtmlTag.Replace(m.Groups["name"].Value, "")).Trim();
                int dash = name.IndexOf(" - ", StringComparison.Ordinal);
                if (dash < 0) dash = name.IndexOf(" – ", StringComparison.Ordinal);
                if (dash < 0) dash = name.IndexOf(" — ", StringComparison.Ordinal);
                if (dash <= 0)
                    continue;
                int score = CoverArtService.Score(title, artist, name[(dash + 3)..], name[..dash]);
                if (score > best)
                    (best, url) = (score, m.Groups["url"].Value);
            }
            // Совпало только название — у известных названий это почти всегда другая песня
            if (url == null || best < 4)
                return Lyrics.NotFound;

            var text = ParseTextLyricsPage(await Fallback.GetStringAsync(url, cts.Token));
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
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3.5));
            var query = Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{FirstArtist(artist)} {title}");
            var json = await NetEase.GetStringAsync($"https://music.163.com/api/search/get?s={query}&type=1&limit=8", cts.Token);
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
        @"слова|музыка|текст(?:\s+песни)?|автор(?:ы)?(?:\s+слов)?|композитор|исполнитель|аранжировка|сведение|мастеринг|" +
        @"lyrics?(?:\s+by)?|lyricist|written\s+by|composer|composed\s+by|producers?|produced\s+by|arranger|mix(?:ing|ed)?(?:\s+by)?|master(?:ing|ed)?(?:\s+by)?)\s*[:：]",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Kugou Music — огромная база синхронных текстов (LRC) без ключей и регистрации.
    /// Отлично находит как зарубежные треки (Linkin Park, Queen), так и русские («Король и Шут», «Кино», Баста).
    /// </summary>
    internal static async Task<Lyrics> FetchKugouAsync(string title, string? artist, double seconds, double tolerance, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3.5));
            var query = Uri.EscapeDataString(string.IsNullOrWhiteSpace(artist) ? title : $"{FirstArtist(artist)} {title}");
            var searchUrl = $"http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={query}&page=1&pagesize=6";
            var json = await Kugou.GetStringAsync(searchUrl, cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array)
                return Lyrics.NotFound;

            int threshold = string.IsNullOrWhiteSpace(artist) ? 3 : 4;
            var matches = info.EnumerateArray().Select(song =>
                {
                    var songTitle = Str(song, "songname");
                    var songArtist = Str(song, "singername");
                    var hash = Str(song, "hash");
                    double duration = song.TryGetProperty("duration", out var d) && d.TryGetDouble(out var s) ? s : 0;
                    int score = CoverArtService.Score(title, artist, songTitle, songArtist);
                    return (Hash: hash, Duration: duration, Score: score);
                })
                .Where(m => !string.IsNullOrEmpty(m.Hash) && m.Score >= threshold)
                .OrderByDescending(m => m.Score)
                .ThenBy(m => seconds > 0 && m.Duration > 0 ? Math.Abs(m.Duration - seconds) : 0)
                .Take(2)
                .ToList();

            if (matches.Count == 0)
                return Lyrics.NotFound;

            Lyrics? plainFallback = null;
            foreach (var match in matches)
            {
                var candUrl = $"http://lyrics.kugou.com/search?ver=1&man=yes&client=pc&keyword={query}&hash={match.Hash}";
                var candJson = await Kugou.GetStringAsync(candUrl, cts.Token);
                using var candDoc = JsonDocument.Parse(candJson);
                if (!candDoc.RootElement.TryGetProperty("candidates", out var candidates) ||
                    candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
                    continue;

                var cand = candidates[0];
                var id = Str(cand, "id");
                var accessKey = Str(cand, "accesskey");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(accessKey))
                    continue;

                var dlUrl = $"http://lyrics.kugou.com/download?ver=1&client=pc&id={id}&accesskey={accessKey}&fmt=lrc&charset=utf8";
                var dlJson = await Kugou.GetStringAsync(dlUrl, cts.Token);
                using var dlDoc = JsonDocument.Parse(dlJson);
                var content = Str(dlDoc.RootElement, "content");
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                var lrcBytes = Convert.FromBase64String(content);
                var lrc = Encoding.UTF8.GetString(lrcBytes).Trim('\uFEFF');
                if (string.IsNullOrWhiteSpace(lrc))
                    continue;

                lrc = ZeroWidth.Replace(UnicodeSpace.Replace(lrc, " "), "");
                var lines = ParseLrc(lrc).Where(line => !CreditLine.IsMatch(line.Text)).ToList();
                while (lines.Count > 0 && lines[0].Text.Length == 0)
                    lines.RemoveAt(0);

                bool timed = seconds <= 0 || match.Duration <= 0 || Math.Abs(match.Duration - seconds) <= tolerance;
                if (timed && IsSynced(lines) && !IsBrokenSync(lines))
                    return new(LyricsStatus.Synced, lines);

                if (lines.Count > 0 && plainFallback == null)
                    plainFallback = Lyrics.FromPlain(string.Join("\n", lines.Select(x => x.Text)));
            }

            return plainFallback ?? Lyrics.NotFound;
        }
        catch (Exception)
        {
            return Lyrics.Error;
        }
    }

    /// <summary>
    /// amalgama-lab.com — крупнейшая в СНГ база оригинальных текстов зарубежных песен с переводами.
    /// Быстрый прямой доступ по URL-slug, не блокируется в РФ.
    /// </summary>
    internal static async Task<Lyrics> FetchAmalgamaAsync(string title, string? artist, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Lyrics.NotFound;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3.5));

            var artistSlug = Slug(FirstArtist(artist));
            var titleSlug = Slug(title);
            if (string.IsNullOrEmpty(artistSlug) || string.IsNullOrEmpty(titleSlug))
                return Lyrics.NotFound;

            char letter = artistSlug[0];
            var url = $"https://www.amalgama-lab.com/songs/{letter}/{artistSlug}/{titleSlug}.html";
            using var response = await Fallback.GetAsync(url, cts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Lyrics.NotFound;
            if (!response.IsSuccessStatusCode)
                return Lyrics.Error;

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            string html = GetEncoding1251().GetString(bytes);

            var text = ParseAmalgamaPage(html);
            return text == null ? Lyrics.NotFound : Lyrics.FromPlain(text);
        }
        catch (Exception)
        {
            return Lyrics.Error;
        }
    }

    private static Encoding? _encoding1251;
    private static Encoding GetEncoding1251()
    {
        if (_encoding1251 != null)
            return _encoding1251;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _encoding1251 = Encoding.GetEncoding("windows-1251");
        }
        catch
        {
            _encoding1251 = Encoding.UTF8;
        }
        return _encoding1251;
    }

    internal static string Slug(string text)
    {
        var s = text.Trim().ToLowerInvariant();
        s = LeadingArticle.Replace(s, "");
        s = TitleSuffix.Replace(s, "");
        s = Regex.Replace(s, @"[^a-z0-9]+", "_").Trim('_');
        return s;
    }

    private static readonly Regex AmalgamaNextTranslation = new(
        @"<div class=""string_container"">\s*<div class=""original"">\s*<strong>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex AmalgamaOriginalDiv = new(
        @"<div class=""original"">(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    internal static string? ParseAmalgamaPage(string html)
    {
        int at = html.IndexOf("id=\"click_area\"", StringComparison.Ordinal);
        if (at < 0)
            at = html.IndexOf("class=\"texts col\"", StringComparison.Ordinal);
        if (at < 0)
            return null;

        var sub = html[at..];
        var strongMatch = AmalgamaNextTranslation.Match(sub);
        if (strongMatch.Success)
            sub = sub[..strongMatch.Index];

        var lines = new List<string>();
        foreach (Match m in AmalgamaOriginalDiv.Matches(sub))
        {
            lines.Add(m.Groups[1].Value);
        }

        return lines.Count == 0 ? null : HtmlToLyrics(string.Join("<br>", lines));
    }

    /// <summary>
    /// Первая очередь: параллельный поиск синхронных текстов (с метками времени / LRC)
    /// одновременно по всем сервисам (lrclib.net, NetEase, Kugou).
    /// Первый найденный качественный синхронный текст (не транслит) немедленно отменяет остальные запросы.
    /// </summary>
    internal static async Task<(Lyrics? Synced, Lyrics? Plain)> FetchTimedAsync(
        string title, string? artist, string? album, TimeSpan duration, CancellationToken ct)
    {
        double seconds = Math.Round(duration.TotalSeconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var lrclibTask = FetchAsync(title, artist, album, duration, linked.Token);
        var neteaseTask = FetchNetEaseAsync(title, artist, seconds, DurationTolerance, linked.Token);
        var kugouTask = FetchKugouAsync(title, artist, seconds, DurationTolerance, linked.Token);

        var pending = new List<Task<Lyrics>> { lrclibTask, neteaseTask, kugouTask };
        Lyrics? bestSynced = null;
        int bestSyncedRank = int.MaxValue;
        Lyrics? bestPlain = null;

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);

            Lyrics result;
            try { result = await completed; }
            catch { continue; }

            if (result.Status == LyricsStatus.Synced)
            {
                int rank = Rank(result);
                if (rank < TranslitPenalty)
                {
                    // Идеальный синхронный текст на родном языке — сразу отменяем всё остальное
                    linked.Cancel();
                    return (result, null);
                }

                if (rank < bestSyncedRank)
                {
                    bestSynced = result;
                    bestSyncedRank = rank;
                }
            }
            else if (result.Status is LyricsStatus.Plain or LyricsStatus.Instrumental)
            {
                bestPlain ??= result;
            }
        }

        return (bestSynced, bestPlain);
    }

    /// <summary>
    /// Вторая очередь: поиск обычного текста песни (без меток времени), если синхронный текст не найден.
    /// Ищет параллельно по сайтам с текстами:
    /// Для русских песен: text-lyrics.ru (быстро и на кириллице) и Genius.
    /// Для остальных: amalgama-lab.com, Genius, lyrics.ovh, плюс запасные тексты из первой очереди.
    /// </summary>
    internal static async Task<Lyrics> FetchPlainTierAsync(
        string title, string? artist, Lyrics? fallbackPlain, CancellationToken ct)
    {
        bool isRussian = HasCyrillic(title) || (artist != null && HasCyrillic(artist));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var tasks = new List<Task<Lyrics>>();
        Task<Lyrics>? textLyricsTask = null;
        Task<Lyrics>? amalgamaTask = null;
        Task<Lyrics>? geniusTask = null;

        if (isRussian)
        {
            textLyricsTask = FetchTextLyricsAsync(title, artist, linked.Token);
            tasks.Add(textLyricsTask);
        }
        else
        {
            amalgamaTask = FetchAmalgamaAsync(title, artist, linked.Token);
            tasks.Add(amalgamaTask);
            tasks.Add(FetchFallbackAsync(title, artist, linked.Token));
        }

        geniusTask = FetchGeniusAsync(title, artist, linked.Token);
        tasks.Add(geniusTask);

        Lyrics? best = fallbackPlain != null && fallbackPlain.Status != LyricsStatus.NotFound && fallbackPlain.Status != LyricsStatus.Error
            ? fallbackPlain
            : null;
        bool hasInstrumental = best?.Status == LyricsStatus.Instrumental;
        int errorCount = 0;

        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);

            Lyrics result;
            try { result = await completed; }
            catch { result = Lyrics.Error; }

            if (result.Status == LyricsStatus.Instrumental)
            {
                hasInstrumental = true;
            }
            else if (result.Status == LyricsStatus.Error)
            {
                errorCount++;
            }
            else if (result.Status == LyricsStatus.Plain)
            {
                // Для русских песен аутентичный текст на кириллице с text-lyrics.ru возвращаем сразу
                if (isRussian && ReferenceEquals(completed, textLyricsTask))
                {
                    linked.Cancel();
                    return result;
                }

                // Для зарубежных песен чистый текст с amalgama-lab.com возвращаем сразу
                if (!isRussian && ReferenceEquals(completed, amalgamaTask))
                {
                    linked.Cancel();
                    return result;
                }

                best ??= result;
            }

            if (best != null && best.Status == LyricsStatus.Plain)
            {
                if (isRussian && textLyricsTask != null && !tasks.Contains(textLyricsTask))
                {
                    linked.Cancel();
                    return best;
                }
                if (!isRussian && amalgamaTask != null && !tasks.Contains(amalgamaTask))
                {
                    linked.Cancel();
                    return best;
                }
            }
        }

        if (best != null)
            return best;
        if (hasInstrumental)
            return Lyrics.Instrumental;
        return errorCount == (isRussian ? 2 : 3) ? Lyrics.Error : Lyrics.NotFound;
    }

    /// <summary>
    /// Вторая очередь: Genius, NetEase и text-lyrics.ru одновременно. Синхронный текст NetEase лучше всего,
    /// потом полный текст Genius (или text-lyrics.ru для русских песен), потом остальное.
    /// Не ждёт медленные или заблокированные сервисы, если уже найден подходящий ответ.
    /// </summary>
    internal static async Task<Lyrics> FetchSecondTierAsync(string title, string? artist, double seconds, double tolerance, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var geniusTask = FetchGeniusAsync(title, artist, linked.Token);
        var neteaseTask = FetchNetEaseAsync(title, artist, seconds, tolerance, linked.Token);
        var textLyricsTask = FetchTextLyricsAsync(title, artist, linked.Token);

        var pending = new List<Task<Lyrics>> { geniusTask, neteaseTask, textLyricsTask };
        bool isRussian = HasCyrillic(title) || (artist != null && HasCyrillic(artist));

        Lyrics? bestPlain = null;
        int errorCount = 0;
        bool hasInstrumental = false;

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);

            Lyrics result;
            try { result = await completed; }
            catch { result = Lyrics.Error; }

            if (result.Status == LyricsStatus.Synced)
            {
                linked.Cancel();
                return result;
            }

            if (result.Status == LyricsStatus.Instrumental)
            {
                hasInstrumental = true;
            }
            else if (result.Status == LyricsStatus.Error)
            {
                errorCount++;
            }
            else if (result.Status == LyricsStatus.Plain)
            {
                if (isRussian && ReferenceEquals(completed, textLyricsTask))
                {
                    // Для русских песен текст с text-lyrics.ru аутентичный на кириллице.
                    // Не ждём заблокированный Genius или китайский NetEase
                    linked.Cancel();
                    return result;
                }

                if (bestPlain == null)
                    bestPlain = result;
                else if (isRussian && ReferenceEquals(completed, textLyricsTask))
                    bestPlain = result;
                else if (!isRussian && ReferenceEquals(completed, geniusTask))
                    bestPlain = result;
            }

            // Если уже есть обычный текст, и длительность не известна (нет смысла ждать NetEase для синхронизации)
            // или NetEase уже закончил работу:
            if (bestPlain != null && (seconds <= 0 || !pending.Contains(neteaseTask)))
            {
                linked.Cancel();
                return bestPlain;
            }
        }

        if (bestPlain != null)
            return bestPlain;
        if (hasInstrumental)
            return Lyrics.Instrumental;
        return errorCount == 3 ? Lyrics.Error : Lyrics.NotFound;
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
        if (await LyricsCache.ReadAsync(key) is { } stored)
        {
            Remember(Cache, key, stored.Lyrics);
            return stored.Lyrics;
        }

        // 1. Первая очередь: параллельный поиск синхронных текстов (дорожки с таймингами / LRC)
        // одновременно по всем сервисам (lrclib.net, NetEase, Kugou).
        // Первый найденный качественный синхронный текст (не транслит) возвращается сразу.
        var (synced, plainFallback) = await FetchTimedAsync(title, artist, album, duration, ct);
        Lyrics lyrics;

        if (synced != null && Rank(synced) < TranslitPenalty)
        {
            lyrics = synced;
        }
        else
        {
            // 2. Вторая очередь: если синхронного текста нет (или найден только транслит),
            // ищем обычный текст песни на сайтах без таймингов (text-lyrics.ru, amalgama-lab, Genius, lyrics.ovh).
            var plain = await FetchPlainTierAsync(title, artist, plainFallback, ct);
            if (synced != null && (plain.Status is LyricsStatus.NotFound or LyricsStatus.Error || Rank(synced) <= Rank(plain)))
                lyrics = synced;
            else if (plain.Status != LyricsStatus.NotFound && plain.Status != LyricsStatus.Error)
                lyrics = plain;
            else
                lyrics = synced ?? plain;
        }

        if (lyrics.Status != LyricsStatus.Error && !ct.IsCancellationRequested)
        {
            Remember(Cache, key, lyrics);
            LyricsCache.Write(key, lyrics, 0);
        }
        return lyrics;
    }

    private static void Remember<T>(Dictionary<string, T> cache, string key, T value)
    {
        lock (cache)
        {
            if (cache.Count >= CacheLimit)
                cache.Clear();
            cache[key] = value;
        }
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
        if (await LyricsCache.ReadAsync(key) is { } stored && stored.Index < candidates.Count)
        {
            Remember(VideoCache, key, stored);
            return stored;
        }

        // Первая очередь: lrclib и резервная база сразу для всех прочтений.
        // Точный запрос get на lrclib тут не помогает (длительность ролика не та) и медленный — только поиск.
        using var tier1 = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fallbacks = candidates.Select(c =>
            HasCyrillic(c.Title) || (c.Artist != null && HasCyrillic(c.Artist))
                ? FetchTextLyricsAsync(c.Title, c.Artist, tier1.Token)
                : FetchFallbackAsync(c.Title, c.Artist, tier1.Token)).ToArray();
        var results = await Task.WhenAll(candidates.Select(c => SearchCandidateAsync(c.Title, c.Artist, ct)));

        var result = PickVideo(results, seconds);
        if (result is { } picked)
        {
            tier1.Cancel();
            // На lrclib только транслит («Ya prishol daty…») — оригинал часто есть на NetEase или Genius
            if (Rank(picked.Lyrics) >= TranslitPenalty)
            {
                var (title, artist) = candidates[picked.Index];
                var second = await FetchSecondTierAsync(title, artist, seconds, VideoDurationTolerance, ct);
                if (second.Status is LyricsStatus.Synced or LyricsStatus.Plain && Rank(second) < Rank(picked.Lyrics))
                    result = (second, picked.Index);
            }
        }
        else
        {
            // lrclib не нашёл — проверяем Kugou для главного прочтения:
            var (topTitle, topArtist) = candidates[0];
            var kugou = await FetchKugouAsync(topTitle, topArtist, seconds, VideoDurationTolerance, ct);
            if (kugou.Status == LyricsStatus.Synced && Rank(kugou) < TranslitPenalty)
            {
                result = (kugou, 0);
                tier1.Cancel();
            }
            else
            {
                // Нет синхронизации — берём текст из резервной базы
                var texts = await Task.WhenAll(fallbacks);
                int found = Array.FindIndex(texts, l => l.Status is LyricsStatus.Plain or LyricsStatus.Instrumental);
                if (found < 0)
                {
                    // И там нет — вторая очередь (Genius, NetEase, amalgama-lab), берём до двух лучших прочтений
                    var topCandidates = candidates.Take(2).ToList();
                    var second = await Task.WhenAll(topCandidates.Select(c =>
                        FetchPlainTierAsync(c.Title, c.Artist, null, ct)));
                    found = Array.FindIndex(second, l => l.Status == LyricsStatus.Synced);
                    if (found < 0)
                        found = Array.FindIndex(second, l => l.Status is LyricsStatus.Plain or LyricsStatus.Instrumental);
                    texts = texts.Take(topCandidates.Count).Zip(second, (ovh, s) => s.Status == LyricsStatus.Error ? ovh : s).ToArray();
                }
                bool anyFailed = results.Any(r => r == null) && texts.All(l => l.Status != LyricsStatus.NotFound);
                result = found >= 0 ? (texts[found], found) : (anyFailed ? Lyrics.Error : Lyrics.NotFound, 0);
            }
        }

        // «Не нашлось», когда часть запросов к lrclib сорвалась, тоже не запоминаем: за долгий джем
        // lrclib порой отвечает отказами, и песня иначе так и осталась бы без текста
        var status = result.Value.Lyrics.Status;
        if (status != LyricsStatus.Error && !(status == LyricsStatus.NotFound && results.Any(r => r == null)) &&
            !ct.IsCancellationRequested)
        {
            Remember(VideoCache, key, result.Value);
            LyricsCache.Write(key, result.Value.Lyrics, result.Value.Index);
        }
        return result.Value;
    }

    /// <summary>
    /// Выбор между прочтениями ролика: лучший по <see cref="Rank"/> текст с подходящей длительностью, среди равных —
    /// раньше по порядку прочтений (так синхронный текст любого прочтения важнее обычного), потом любой найденный
    /// текст без меток времени.
    /// </summary>
    internal static (Lyrics Lyrics, int Index)? PickVideo(IReadOnlyList<List<LrcLibTrack>?> results, double seconds)
    {
        (Lyrics Lyrics, int Index, int Rank)? best = null;
        for (int i = 0; i < results.Count; i++)
            if (results[i] is { } found && PickTrack(found, seconds, VideoDurationTolerance) is { } p &&
                (best == null || p.Rank < best.Value.Rank))
                best = (p.Lyrics, i, p.Rank);
        if (best is { } b)
            return (b.Lyrics, b.Index);

        // Длительность не совпала ни у кого (длинный клип, live) — синхронизация не сойдётся, но сам текст верный
        for (int i = 0; i < results.Count; i++)
        {
            var plain = results[i]?.Select(ToLyrics).OfType<Lyrics>().MinBy(Rank);
            if (plain != null)
                return (plain.Status == LyricsStatus.Synced ? Lyrics.FromPlain(string.Join("\n", plain.Lines.Select(l => l.Text))) : plain, i);
        }
        return null;
    }

    /// <summary>
    /// Поиск одного прочтения. Название с артиклем ищется ещё и без него: на lrclib у «The 7th Element» только транслит
    /// с битыми метками, а нормальный текст — у «7th Element».
    /// </summary>
    private static async Task<List<LrcLibTrack>?> SearchCandidateAsync(string title, string? artist, CancellationToken ct)
    {
        var bare = LeadingArticle.Replace(title, "");
        if (bare == title || bare.Length < 2)
            return await SearchAsync(title, artist, ct);
        var both = await Task.WhenAll(SearchAsync(title, artist, ct), SearchAsync(bare, artist, ct));
        if (both[0] == null && both[1] == null)
            return null;
        return (both[0] ?? new()).Concat(both[1] ?? new()).ToList();
    }

    private static Task<List<LrcLibTrack>?> SearchAsync(string title, string? artist, CancellationToken ct) =>
        GetLrcLibAsync<List<LrcLibTrack>>(
            "search?" + (artist == null ? Query(("track_name", title)) : Query(("track_name", title), ("artist_name", artist))), ct);

    /// <summary>
    /// Запрос к lrclib; null — не ответил. Под нагрузкой он отвечает 503 или 429 — тогда одна быстрая повторная попытка.
    /// </summary>
    private static async Task<T?> GetLrcLibAsync<T>(string query, CancellationToken ct) where T : class
    {
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                using var response = await Http.GetAsync(query, ct);
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct);
                }
                if (attempt > 0 || response.StatusCode is not (HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests))
                    return null;
                await Task.Delay(200, ct);
            }
        }
        catch (Exception)
        {
            return null; // нет сети, таймаут, отмена, кривой ответ
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

    /// <summary>
    /// Точный запрос get и поиск по всем вариантам названия идут одновременно: get порой ждёт секунды, пока lrclib
    /// спросит другие сервисы, а поиск отвечает быстро. Первый хороший синхронный текст отменяет остальные запросы.
    /// </summary>
    private static async Task<Lyrics> FetchAsync(string title, string? artist, string? album, TimeSpan duration, CancellationToken ct)
    {
        double seconds = Math.Round(duration.TotalSeconds);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<Task<List<LrcLibTrack>?>>();
        if (artist != null && seconds > 0)
            pending.Add(GetExactAsync(Query(("track_name", title), ("artist_name", artist), ("album_name", album ?? ""),
                ("duration", seconds.ToString(CultureInfo.InvariantCulture))), cts.Token));
        // Поиск: другой альбом, «feat.» в названии, «- Remastered 2011», несколько исполнителей через запятую, артикль
        foreach (var (t, a) in Variants(title, artist))
            pending.Add(SearchAsync(t, a, cts.Token));

        Lyrics? best = null;
        int bestRank = int.MaxValue;
        bool failed = false;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            var found = await done;
            if (found == null)
            {
                failed = true;
                continue;
            }
            if (PickTrack(found, seconds, DurationTolerance) is { } p && p.Rank < bestRank)
                (best, bestRank) = (p.Lyrics, p.Rank);
            if (bestRank == 0)
            {
                cts.Cancel();
                break;
            }
        }
        ct.ThrowIfCancellationRequested();
        return best ?? (failed ? Lyrics.Error : Lyrics.NotFound);
    }

    /// <summary>
    /// Точное совпадение. Если трека нет в базе, lrclib ищет его у других сервисов и под нагрузкой отвечает 503 —
    /// это не ошибка: пустой ответ, поиск идёт параллельно.
    /// </summary>
    private static async Task<List<LrcLibTrack>?> GetExactAsync(string query, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1800));
            using var response = await Http.GetAsync("get?" + query, cts.Token);
            if (!response.IsSuccessStatusCode)
                return new();
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            return await JsonSerializer.DeserializeAsync<LrcLibTrack>(stream, Json, cts.Token) is { } track ? new() { track } : new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    /// <summary>Варианты названия и исполнителя для поиска, без повторов.</summary>
    internal static List<(string Title, string? Artist)> Variants(string title, string? artist)
    {
        var variants = new List<(string Title, string? Artist)>();
        void Add(string t, string? a)
        {
            t = t.Trim();
            if (t.Length >= 2 && !variants.Any(v => string.Equals(v.Title, t, StringComparison.OrdinalIgnoreCase) &&
                                                    string.Equals(v.Artist, a, StringComparison.OrdinalIgnoreCase)))
                variants.Add((t, a));
        }
        Add(title, artist);
        var shortTitle = TitleSuffix.Replace(title, "").Trim();
        if (shortTitle.Length == 0)
            shortTitle = title;
        var firstArtist = artist == null ? null : FirstArtist(artist);
        Add(shortTitle, firstArtist);
        Add(LeadingArticle.Replace(shortTitle, ""), firstArtist);
        return variants;
    }

    /// <summary>Лучший из найденных с подходящей длительностью: меньший <see cref="Rank"/>, потом ближе по длительности.</summary>
    private static (Lyrics Lyrics, int Rank)? PickTrack(List<LrcLibTrack> found, double seconds, double tolerance) =>
        found.Where(t => seconds <= 0 || Math.Abs(t.Duration - seconds) <= tolerance)
            .Select(t => (Track: t, Lyrics: ToLyrics(t)))
            .Where(x => x.Lyrics != null)
            .Select(x => (x.Track, Lyrics: x.Lyrics!, Rank: Rank(x.Lyrics!)))
            .OrderBy(x => x.Rank)
            .ThenBy(x => Math.Abs(x.Track.Duration - seconds))
            .Select(x => ((Lyrics Lyrics, int Rank)?)(x.Lyrics, x.Rank))
            .FirstOrDefault();

    /// <summary>Штраф за транслит: русская песня латиницей хуже любого текста на кириллице.</summary>
    internal const int TranslitPenalty = 4;

    /// <summary>
    /// Насколько текст плох, меньше — лучше: синхронный 0, обычный 2 (сюда же синхронный с битыми метками — он
    /// показывается без них), плюс <see cref="TranslitPenalty"/> за транслит.
    /// </summary>
    internal static int Rank(Lyrics lyrics)
    {
        int rank = lyrics.Status switch
        {
            LyricsStatus.Synced => 0,
            LyricsStatus.Plain or LyricsStatus.Instrumental => 2,
            _ => 10,
        };
        return lyrics.Lines.Count > 0 && IsTransliteration(lyrics.Lines.Select(l => l.Text)) ? rank + TranslitPenalty : rank;
    }

    /// <summary>
    /// Битые метки: на lrclib встречается текст, где куплет целиком уложен в пару секунд (строки через 0,4–0,7 с).
    /// Три быстрые смены строк подряд в настоящей песне не бывают.
    /// </summary>
    internal static bool IsBrokenSync(IReadOnlyList<LyricLine> lines)
    {
        int fast = 0;
        LyricLine? previous = null;
        foreach (var line in lines)
        {
            if (line.Text.Length == 0)
                continue; // пустые строки — паузы, они бывают короткими
            if (previous != null && (line.Time - previous.Time).TotalSeconds < 0.75)
            {
                if (++fast >= 3)
                    return true;
            }
            else
                fast = 0;
            previous = line;
        }
        return false;
    }

    /// <summary>Слово, какие бывают только в русском транслите: zh, kh, yu, ya, yo (кроме you), iy, yy.</summary>
    private static readonly Regex TranslitWord = new(@"^\p{L}*(?:zh|kh|yu|ya|yo(?!u)|iy|yy)\p{L}*$", RegexOptions.IgnoreCase);
    private static readonly char[] WordSeparators = { ' ', ',', '.', '!', '?', ';', ':', '"', '«', '»', '(', ')', '-', '—', '…', '\'' };

    /// <summary>Есть ли в тексте кириллические буквы (русские названия треков или исполнителей).</summary>
    internal static bool HasCyrillic(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        foreach (var c in text)
            if (c is >= 'Ѐ' and <= 'ӿ')
                return true;
        return false;
    }

    /// <summary>
    /// Русский текст латиницей («Ya prishol daty etu piesnyu»). Признак — доля слов с сочетаниями, которых в английском
    /// почти нет. В испанском «yo» и «ya» встречаются, но их доля намного ниже порога. Текст с кириллицей — не транслит.
    /// </summary>
    internal static bool IsTransliteration(IEnumerable<string> lines)
    {
        int words = 0, marked = 0;
        foreach (var line in lines)
            foreach (var word in line.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (char c in word)
                    if (c is >= 'Ѐ' and <= 'ӿ')
                        return false;
                words++;
                if (TranslitWord.IsMatch(word))
                    marked++;
            }
        return marked >= 3 && marked >= words * 0.15;
    }

    private static Lyrics? ToLyrics(LrcLibTrack track)
    {
        var synced = string.IsNullOrWhiteSpace(track.SyncedLyrics) ? new List<LyricLine>() : ParseLrc(track.SyncedLyrics);
        // Битые метки врали бы — такой текст показываем без них
        if (IsSynced(synced) && !IsBrokenSync(synced))
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
