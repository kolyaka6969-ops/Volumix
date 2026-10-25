using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Volumix.Services;

/// <summary>
/// Опенинги и эндинги аниме с YouTube. В названии ролика обычно аниме и номер опенинга, а песня и исполнитель —
/// если повезёт: «Chainsaw Man - Opening | KICK BACK», «Jujutsu Kaisen - Opening 2 | 4K». Недостающее берём
/// из открытой базы AnimeThemes: по аниме и номеру она знает песню и исполнителя.
/// </summary>
internal static class AnimeThemeService
{
    /// <summary>Что удалось прочитать из названия ролика. Kind — «OP» или «ED».</summary>
    internal sealed record Parsed(string? Anime, string Kind, int? Number, string? Song, string? Artist, bool Strong);

    private const int CacheLimit = 200;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly HttpClient Http = CreateHttp();
    // Ответ по названию ролика; null — не опенинг. Срок — только у ответов, собранных без AnimeThemes
    private static readonly Dictionary<string, ((string Title, string? Artist)? Song, DateTime Until)> Cache = new();
    // Запросы в пути: обновления ролика приходят часто, а AnimeThemes отвечает секунд по пять
    private static readonly Dictionary<string, Task<(string Title, string? Artist)?>> Pending = new();

    // Латинские OP и ED — только заглавными: «Ed Sheeran» не эндинг
    private static readonly Regex Marker = new(
        @"(?<![A-Za-z\d\p{IsCyrillic}])(?:(?<op>(?i:opening|опенинг)|NCOP|OP)|(?<ed>(?i:ending|эндинг)|NCED|ED))(?![\p{L}])" +
        @"|(?<op>オープニング|ＯＰ)|(?<ed>エンディング|ＥＤ)");
    private static readonly Regex Number = new(@"^\s*(\d{1,2})(?!\d)");
    /// <summary>Приметы ролика с аниме: без них «Happy Ending» осталось бы просто песней.</summary>
    private static readonly Regex AnimeHint = new(
        @"(?i)(?<![\p{L}])(?:full|tv\s*size|creditless|non-?credit|anime|аниме|season|сезон)(?![\p{L}])|アニメ|ノンクレジット|主題歌");
    private static readonly Regex Quote = new(
        @"「(?<q>[^」]+)」|『(?<q>[^』]+)』|“(?<q>[^”]+)”|""(?<q>[^""]+)""|«(?<q>[^»]+)»|(?<![\p{L}\d])'(?<q>[^']+)'(?![\p{L}\d])");
    // Перед кавычками с названием аниме: «TVアニメ「チェンソーマン」»
    private static readonly Regex AnimeBeforeQuote = new(@"(?:アニメ|劇場版)\s*$");
    private static readonly Regex Separator = new(@"\s*[｜│|]\s*|\s+[-–—/]\s+");
    private static readonly Regex ByArtist = new(@"^(?<song>.+?)\s+(?i:by)\s+(?<artist>.+)$");
    private static readonly Regex AfterQuoteArtist = new(@"^\s*(?i:by)\s+(?<artist>[^|｜│\[(【]+?)\s*(?:[|｜│\[(【]|\s[-–—/]\s|$)");
    /// <summary>Сегмент без песни: «4K», «60FPS», «Creditless», «Full», «TV Size».</summary>
    private static readonly Regex NoiseSegment = new(
        @"^(?i:\s|[,&+.]|\d+k|\d+\s*fps|\d{3,4}p|u?hd|creditless|full|tv\s*size|lyrics?|romaji|(?:eng(?:lish)?\s*)?sub(?:s|bed)?|amv|mad|ncop\d*|nced\d*|official|version|ver)*$");
    private static readonly Regex SeasonWord = new(@"(?i)^(?:season|part|cour|\d+(?:st|nd|rd|th)?)$");

    /// <summary>
    /// Разбор названия без сети. null — не опенинг и не эндинг. Strong — уверены, что это аниме
    /// (номер, OP заглавными, «Full», «Creditless», песня отдельной частью после «Opening»).
    /// </summary>
    internal static Parsed? Parse(string title)
    {
        var marker = Marker.Match(title);
        if (!marker.Success)
            return null;
        string kind = marker.Groups["ed"].Success ? "ED" : "OP";
        int markerEnd = marker.Index + marker.Length;
        int? number = null;
        var digits = Number.Match(title[markerEnd..]);
        if (digits.Success)
        {
            number = int.Parse(digits.Groups[1].Value);
            markerEnd += digits.Length;
        }
        // Английские «Opening» и «Ending» бывают и в названиях песен, остальные метки (OP, опенинг, オープニング) — нет
        bool plainWord = Regex.IsMatch(marker.Value, "^(?:opening|ending)$", RegexOptions.IgnoreCase) && marker.Value.Any(char.IsLower);
        bool strong = number != null || !plainWord || AnimeHint.IsMatch(title);

        var segments = Segments(title);
        int k = segments.FindIndex(s => s.Start <= marker.Index && marker.Index < s.Start + s.Text.Length);
        string? song = null, artist = null, anime = null;

        var quotes = Quote.Matches(title).ToList();
        var animeQuote = quotes.FirstOrDefault(q => q.Index < marker.Index && AnimeBeforeQuote.IsMatch(title[..q.Index]));
        var songQuote = quotes.FirstOrDefault(q => q.Index >= markerEnd)
                        ?? quotes.LastOrDefault(q => q.Index < marker.Index && q != animeQuote);
        if (songQuote != null)
        {
            song = songQuote.Groups["q"].Value.Trim();
            var by = AfterQuoteArtist.Match(title[(songQuote.Index + songQuote.Length)..]);
            if (by.Success)
                artist = by.Groups["artist"].Value.Trim();
            else
            {
                // «｜King Gnu「SPECIALZ」»: исполнитель прямо перед кавычками
                int start = segments.LastOrDefault(s => s.Start <= songQuote.Index).Start;
                var before = title[start..songQuote.Index].Trim();
                if (before.Any(char.IsLetter) && !Marker.IsMatch(before) && !Quote.IsMatch(before) && !AnimeHint.IsMatch(before))
                    artist = before;
            }
        }
        else if (k >= 0)
        {
            // Песня — отдельная часть после опенинга: «Chainsaw Man - Opening | KICK BACK»
            var next = segments.Skip(k + 1).FirstOrDefault(s => !NoiseSegment.IsMatch(s.Text));
            bool animeInSegment = title[segments[k].Start..marker.Index].Any(char.IsLetter);
            if (next.Text != null)
            {
                song = next.Text;
                strong = true;
            }
            else if (k > 0 && animeInSegment)
                song = segments[k - 1].Text; // «Unravel - Tokyo Ghoul Opening»
            if (song != null && ByArtist.Match(song) is { Success: true } by)
            {
                song = by.Groups["song"].Value.Trim();
                artist = by.Groups["artist"].Value.Trim();
            }
        }

        if (animeQuote != null)
            anime = animeQuote.Groups["q"].Value;
        else if (k >= 0)
        {
            var prefix = Quote.Replace(title[segments[k].Start..marker.Index], " ");
            if (prefix.Any(char.IsLetter))
                anime = prefix;
            else if (k > 0 && segments[k - 1].Text != song)
                anime = segments[k - 1].Text;
        }
        anime = anime == null ? null : Regex.Replace(anime, @"^\s*TV\s*アニメ|\s{2,}", " ").Trim(' ', '-', '–', '—', ':');
        return new Parsed(string.IsNullOrEmpty(anime) ? null : anime, kind, number,
            string.IsNullOrWhiteSpace(song) ? null : song, string.IsNullOrWhiteSpace(artist) ? null : artist, strong);
    }

    /// <summary>Песня из названия, если в нём есть всё нужное и это точно аниме. Без сети.</summary>
    internal static (string Title, string? Artist)? FromTitle(string title) =>
        Parse(title) is { Strong: true, Song: { } song } parsed ? (song, parsed.Artist) : null;

    /// <summary>Готовый ответ <see cref="ResolveAsync"/> без ожидания; false — его ещё нет.</summary>
    public static bool TryGetResolved(string title, out (string Title, string? Artist)? song)
    {
        lock (Cache)
            if (Cache.TryGetValue(title, out var cached) && DateTime.UtcNow < cached.Until)
            {
                song = cached.Song;
                return true;
            }
        song = null;
        return false;
    }

    /// <summary>
    /// Песня и исполнитель для ролика с опенингом или эндингом; null — это не он или узнать не удалось.
    /// Песню из названия сверяет с AnimeThemes, недостающее (исполнителя, саму песню) берёт оттуда.
    /// Одновременные запросы одного ролика сливаются в один. Никогда не бросает исключений.
    /// </summary>
    public static Task<(string Title, string? Artist)?> ResolveAsync(string title)
    {
        if (TryGetResolved(title, out var song))
            return Task.FromResult(song);
        lock (Pending)
        {
            if (!Pending.TryGetValue(title, out var task))
            {
                task = LookUpAsync(title);
                Pending[title] = task;
                task.ContinueWith(_ => { lock (Pending) Pending.Remove(title); }, TaskScheduler.Default);
            }
            return task;
        }
    }

    private static async Task<(string Title, string? Artist)?> LookUpAsync(string title)
    {
        await Task.Yield(); // не держим блокировку Pending, пока разбираем название
        var parsed = Parse(title);
        (string Title, string? Artist)? result = null;
        bool reliable = true;
        if (parsed != null && (parsed.Strong || parsed.Song != null))
        {
            var offline = parsed is { Strong: true, Song: { } s } ? (s, parsed.Artist) : ((string, string?)?)null;
            result = offline;
            if (parsed.Anime != null)
            {
                try
                {
                    var found = Pick(parsed, await SearchAsync(parsed.Anime, CancellationToken.None));
                    if (found != null)
                        result = found;
                }
                catch (Exception)
                {
                    reliable = false; // AnimeThemes не ответил — обходимся названием, позже спросим снова
                }
            }
        }

        lock (Cache)
        {
            if (Cache.Count >= CacheLimit)
                Cache.Clear();
            Cache[title] = (result, reliable ? DateTime.MaxValue : DateTime.UtcNow + RetryAfter);
        }
        return result;
    }

    internal sealed record Theme(string Type, int? Sequence, string Song, string? Artist);
    internal sealed record Anime(string Name, IReadOnlyList<string> Synonyms, IReadOnlyList<Theme> Themes);

    /// <summary>Выбор песни среди найденных аниме. Слабое прочтение («Mika - Happy Ending») верим, только если песня совпала.</summary>
    internal static (string Title, string? Artist)? Pick(Parsed parsed, IReadOnlyList<Anime> found)
    {
        if (parsed.Song != null)
        {
            var wanted = Key(parsed.Song);
            foreach (var anime in found)
                foreach (var theme in anime.Themes)
                {
                    var have = Key(theme.Song);
                    if (have == wanted || (Math.Min(have.Length, wanted.Length) >= 4 && (have.Contains(wanted) || wanted.Contains(have))))
                        return (theme.Song, theme.Artist ?? parsed.Artist);
                }
            return null;
        }
        if (!parsed.Strong || parsed.Anime == null)
            return null;

        // Песни в названии нет: берём опенинг с нужным номером у аниме, чьё название совпало
        var words = Words(parsed.Anime);
        foreach (var anime in found)
        {
            var names = new HashSet<string>(new[] { anime.Name }.Concat(anime.Synonyms).SelectMany(Words));
            if (words.Count == 0 || !words.All(names.Contains))
                continue;
            var theme = anime.Themes.FirstOrDefault(t => t.Type == parsed.Kind && (t.Sequence ?? 1) == (parsed.Number ?? 1));
            return theme == null ? null : (theme.Song, theme.Artist);
        }
        return null;
    }

    private static async Task<IReadOnlyList<Anime>> SearchAsync(string anime, CancellationToken ct)
    {
        var url = "https://api.animethemes.moe/search?fields%5Bsearch%5D=anime&page%5Blimit%5D=5" +
                  "&include%5Banime%5D=animesynonyms,animethemes.song.artists&q=" + Uri.EscapeDataString(anime);
        var json = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var list = new List<Anime>();
        foreach (var item in doc.RootElement.GetProperty("search").GetProperty("anime").EnumerateArray())
        {
            var synonyms = item.TryGetProperty("animesynonyms", out var syn)
                ? syn.EnumerateArray().Select(s => s.GetProperty("text").GetString() ?? "").ToList()
                : new List<string>();
            var themes = new List<Theme>();
            if (item.TryGetProperty("animethemes", out var all))
                foreach (var t in all.EnumerateArray())
                {
                    if (!t.TryGetProperty("song", out var song) || song.ValueKind != JsonValueKind.Object ||
                        song.GetProperty("title").GetString() is not { Length: > 0 } songTitle)
                        continue;
                    // У песни бывает несколько исполнителей («MAN WITH A MISSION», «milet») — в базах текстов ищется первый
                    string? artist = song.TryGetProperty("artists", out var artists)
                        ? artists.EnumerateArray().Select(a => a.GetProperty("name").GetString()).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                        : null;
                    int? sequence = t.TryGetProperty("sequence", out var seq) && seq.ValueKind == JsonValueKind.Number ? seq.GetInt32() : null;
                    themes.Add(new Theme(t.GetProperty("type").GetString() ?? "", sequence, songTitle, artist));
                }
            list.Add(new Anime(item.GetProperty("name").GetString() ?? "", synonyms, themes));
        }
        return list;
    }

    private readonly record struct Segment(int Start, string Text);

    private static List<Segment> Segments(string title)
    {
        var list = new List<Segment>();
        int start = 0;
        foreach (Match sep in Separator.Matches(title))
        {
            list.Add(new Segment(start, title[start..sep.Index]));
            start = sep.Index + sep.Length;
        }
        list.Add(new Segment(start, title[start..]));
        return list.Select(s => new Segment(s.Start, s.Text.Trim())).ToList();
    }

    private static string Key(string text) => CoverArtService.Norm(text).Replace(" ", "");

    /// <summary>Слова названия аниме без «Season 2», «2nd», «Part»: у AnimeThemes сезоны записаны по-своему.</summary>
    private static List<string> Words(string name) =>
        CoverArtService.Norm(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !SeasonWord.IsMatch(w)).ToList();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Volumix/{typeof(AnimeThemeService).Assembly.GetName().Version?.ToString(3)}");
        return http;
    }
}
