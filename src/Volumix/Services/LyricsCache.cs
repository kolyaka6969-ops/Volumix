using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Volumix.Services;

/// <summary>
/// Найденные тексты на диске: песня, которую уже слушали, получает текст сразу, без запросов к базам — и после
/// перезапуска Volumix. «Не нашлось» хранится сутки (базы пополняются), найденный текст — месяц.
/// Файл: строка «volumix-lyrics 1 &lt;статус&gt; &lt;номер прочтения&gt;», дальше строки «&lt;метка в тиках&gt;\t&lt;текст&gt;».
/// </summary>
internal static class LyricsCache
{
    private const string Header = "volumix-lyrics 1";
    private static readonly TimeSpan FoundLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissingLifetime = TimeSpan.FromDays(1);

    internal static string Folder = Path.Combine(SettingsService.Folder, "cache", "lyrics");
    private static int _writes;

    private static string PathFor(string key) =>
        Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24] + ".txt");

    public static async Task<(Lyrics Lyrics, int Index)?> ReadAsync(string key)
    {
        try
        {
            var path = PathFor(key);
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            var parsed = Parse(await File.ReadAllTextAsync(path));
            var lifetime = parsed?.Lyrics.Status == LyricsStatus.NotFound ? MissingLifetime : FoundLifetime;
            if (parsed == null || DateTime.UtcNow - info.LastWriteTimeUtc > lifetime)
            {
                info.Delete();
                return null;
            }
            return parsed;
        }
        catch (Exception)
        {
            return null; // кэш — только ускорение: при любой ошибке ищем заново
        }
    }

    public static void Write(string key, Lyrics lyrics, int index)
    {
        if (lyrics.Status is LyricsStatus.Error or LyricsStatus.Loading)
            return;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor(key), Format(lyrics, index));
            // Изредка убираем устаревшее, чтобы папка не росла бесконечно
            if (++_writes % 50 == 1)
                Prune();
        }
        catch (Exception) { }
    }

    internal static string Format(Lyrics lyrics, int index)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append(' ').Append(lyrics.Status).Append(' ').Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var line in lyrics.Lines)
            sb.Append(line.Time.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(line.Text.Replace('\n', ' ')).Append('\n');
        return sb.ToString();
    }

    internal static (Lyrics Lyrics, int Index)? Parse(string text)
    {
        var rows = text.Split('\n');
        var head = rows[0].Split(' ');
        if (head.Length != 4 || $"{head[0]} {head[1]}" != Header ||
            !Enum.TryParse<LyricsStatus>(head[2], out var status) ||
            !int.TryParse(head[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            return null;
        var lines = new List<LyricLine>();
        foreach (var row in rows.Skip(1))
        {
            int tab = row.IndexOf('\t');
            if (tab <= 0 || !long.TryParse(row[..tab], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
                continue;
            lines.Add(new LyricLine(TimeSpan.FromTicks(ticks), row[(tab + 1)..]));
        }
        if (lines.Count > 0)
            lines = LyricsService.CleanLeadingMetadata(lines);
        var lyrics = status switch
        {
            LyricsStatus.NotFound => Lyrics.NotFound,
            LyricsStatus.Instrumental => Lyrics.Instrumental,
            LyricsStatus.Synced or LyricsStatus.Plain when lines.Count > 0 => new Lyrics(status, lines),
            _ => null,
        };
        return lyrics == null ? null : (lyrics, index);
    }

    private static void Prune()
    {
        foreach (var file in new DirectoryInfo(Folder).EnumerateFiles("*.txt"))
            if (DateTime.UtcNow - file.LastWriteTimeUtc > FoundLifetime)
                file.Delete();
    }
}
