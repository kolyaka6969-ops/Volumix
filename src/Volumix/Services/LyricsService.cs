using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

    private static readonly Regex SentenceStart = new(@"(?<=\p{Ll}[,!?…]?)\s+(?=\p{Lu})", RegexOptions.Compiled);

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
    private const int CacheLimit = 200;

    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, Lyrics> Cache = new();

    private static readonly Regex TimeTag = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex LeadingTimeTags = new(@"^(?:\s*\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\])+", RegexOptions.Compiled);
    private static readonly Regex OffsetTag = new(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AnyTag = new(@"^\[[a-z#]+:.*\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WordTimeTag = new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);
    private static readonly Regex TitleSuffix = new(@"\s+-\s+.*$|\s*[(\[](?:feat|ft|with|prod)\.?\s[^)\]]*[)\]]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ArtistSeparator = new(@",\s*|\s+&\s+|\s+(?:feat|ft)\.?\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://lrclib.net/api/"), Timeout = TimeSpan.FromSeconds(15) };
        // lrclib просит подписывать запросы названием программы
        var version = typeof(LyricsService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Volumix/{version} (+https://github.com/kolyaka6969-ops/Volumix)");
        return http;
    }

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

        Lyrics lyrics;
        try { lyrics = await FetchAsync(title, artist, album, duration, ct); }
        catch (Exception) { lyrics = Lyrics.Error; } // нет сети, таймаут, отмена, кривой ответ

        if (lyrics.Status != LyricsStatus.Error)
            lock (Cache)
            {
                if (Cache.Count >= CacheLimit)
                    Cache.Clear();
                Cache[key] = lyrics;
            }
        return lyrics;
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
            var lyrics = found == null ? null : Pick(found, seconds);
            if (lyrics?.Status == LyricsStatus.Synced)
                return lyrics;
            best ??= lyrics;
        }
        return best ?? (failed ? Lyrics.Error : Lyrics.NotFound);
    }

    /// <summary>Лучший из найденных: подходящий по длительности, синхронный, потом просто текст.</summary>
    private static Lyrics? Pick(List<LrcLibTrack> found, double seconds) =>
        found.Where(t => seconds <= 0 || Math.Abs(t.Duration - seconds) <= DurationTolerance)
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

    private sealed class LrcLibTrack
    {
        public double Duration { get; set; }
        public bool Instrumental { get; set; }
        public string? PlainLyrics { get; set; }
        public string? SyncedLyrics { get; set; }
    }
}
