using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Volumix.Services;

/// <summary>
/// Строит грамматику для второго, «узкого» распознавателя: слова команд + названия запущенных программ и устройств.
/// Названий вроде «спотифай» нет в словаре модели, поэтому они раскладываются на известные кусочки («спот и фай»),
/// а парсер потом склеивает соседние слова обратно.
/// </summary>
public static class VoiceGrammar
{
    private static readonly string[] CommandWords =
    {
        "громкость", "громкости", "громче", "погромче", "тише", "потише", "прибавь", "прибавить", "убавь", "убавить",
        "увеличь", "увеличить", "уменьши", "уменьшить", "повысь", "понизь", "подними", "опусти", "сделай", "сделать",
        "поставь", "поставить", "установи", "звук", "звука", "звуки", "без", "выключи", "выключить", "отключи",
        "отключить", "включи", "включить", "верни", "заглуши", "процентов", "процента", "процент", "на", "до", "в",
        "во", "и", "у", "для", "с", "чуть", "немного", "сильно", "намного", "гораздо", "очень", "общая", "общую",
        "общей", "это", "этого", "окно", "окна", "текущего", "активного", "приложение", "приложения", "программы",
        "системные", "системных", "максимум", "минимум", "половину", "пожалуйста", "больше", "меньше", "выше", "ниже",
        "тема", "тему", "темная", "темную", "тёмная", "тёмную", "светлая", "светлую", "как", "системе", "режим",
        "ночной", "дневной", "автозапуск", "автозагрузку", "запуск", "вместе", "виндовс", "индикатор", "индикатора",
        "шаг", "шага", "трей", "сворачивать", "не", "показывать", "покажи", "открой", "закрой", "скрой", "микшер",
        "настройки", "горячие", "клавиши", "следующее", "устройство", "вывода", "переключи", "наушники", "динамики",
        "колонки", "монитор", "смени", "другое", "браузер", "музыка", "игра",
    };

    // Как программы называют вслух: фрагмент имени exe/описания → слова
    private static readonly (string Fragment, string[] Spoken)[] KnownNames =
    {
        ("spotify", new[] { "спотифай" }),
        ("discord", new[] { "дискорд" }),
        ("chrome", new[] { "хром", "хроме", "гугл хром" }),
        ("telegram", new[] { "телеграм", "телеграмм", "телеграма" }),
        ("steam", new[] { "стим" }),
        ("yandex", new[] { "яндекс" }),
        ("browser", new[] { "яндекс", "браузер" }),
        ("msedge", new[] { "эдж" }),
        ("firefox", new[] { "фаерфокс", "мозилла" }),
        ("opera", new[] { "опера" }),
        ("javaw", new[] { "майнкрафт", "джава" }),
        ("minecraft", new[] { "майнкрафт" }),
        ("zoom", new[] { "зум" }),
        ("skype", new[] { "скайп" }),
        ("whatsapp", new[] { "ватсап" }),
        ("teams", new[] { "тимс" }),
        ("obs", new[] { "обс" }),
        ("vlc", new[] { "плеер" }),
        ("aimp", new[] { "аимп", "плеер" }),
        ("music", new[] { "музыка" }),
    };

    public static string Build(IEnumerable<VoiceApp> apps, IEnumerable<VoiceDevice> devices, Func<string, bool> inVocabulary)
    {
        var phrases = new HashSet<string>();

        void AddPhrase(string phrase)
        {
            var parts = new List<string>();
            foreach (var word in VoiceCommandParser.Tokenize(phrase))
            {
                var split = SplitToVocabulary(word, inVocabulary);
                if (split == null)
                    return;
                parts.AddRange(split);
            }
            if (parts.Count > 0)
                phrases.Add(string.Join(' ', parts));
        }

        foreach (var w in CommandWords.Concat(VoiceCommandParser.NumberWords))
            if (inVocabulary(w))
                phrases.Add(w);

        foreach (var app in apps)
            foreach (var spoken in SpokenNames(app))
                AddPhrase(spoken);

        foreach (var device in devices)
            foreach (var word in VoiceCommandParser.Tokenize(device.Name))
                if (word.Length >= 3 && word.Any(IsCyrillic) && inVocabulary(word))
                    phrases.Add(word);

        // Без экранирования: Vosk не понимает \uXXXX и молча выкинет все русские слова
        return JsonSerializer.Serialize(phrases.Append("[unk]"),
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Варианты произношения названия программы по-русски.</summary>
    internal static IEnumerable<string> SpokenNames(VoiceApp app)
    {
        if (app.Key == VoiceCommandParser.SystemSoundsKey)
            yield break;
        var key = app.Key.ToLowerInvariant();
        var name = app.Name.ToLowerInvariant();

        bool known = false;
        foreach (var (fragment, spoken) in KnownNames)
            if (key.Contains(fragment) || name.Contains(fragment))
            {
                known = true;
                foreach (var s in spoken)
                    yield return s;
            }
        if (known)
            yield break;

        // Русское название (например, «Проводник») — как есть, латиницу транслитерируем
        foreach (var word in VoiceCommandParser.Tokenize(name).Append(key).Distinct())
        {
            if (word.Length < 3 || word.Any(char.IsDigit))
                continue;
            yield return word.Any(IsCyrillic) ? word : ToCyrillic(word);
        }
    }

    /// <summary>Раскладывает слово на минимальное число слов из словаря модели («дискорд» → «дис корд»).</summary>
    internal static List<string>? SplitToVocabulary(string word, Func<string, bool> inVocabulary)
    {
        if (inVocabulary(word))
            return new List<string> { word };

        int n = word.Length;
        var best = new int[n + 1];
        var prev = new int[n + 1];
        Array.Fill(best, int.MaxValue);
        best[0] = 0;
        for (int end = 1; end <= n; end++)
            for (int start = Math.Max(0, end - 12); start < end; start++)
            {
                if (best[start] == int.MaxValue)
                    continue;
                var piece = word[start..end];
                bool ok = piece.Length >= 2 ? inVocabulary(piece) : piece is "и" or "а" or "о" or "у" or "в" or "с" or "к" or "я";
                if (ok && best[start] + 1 < best[end])
                {
                    best[end] = best[start] + 1;
                    prev[end] = start;
                }
            }

        if (best[n] == int.MaxValue || best[n] > 4)
            return null;
        var parts = new List<string>();
        for (int end = n; end > 0; end = prev[end])
            parts.Insert(0, word[prev[end]..end]);
        return parts;
    }

    private static bool IsCyrillic(char c) => c is >= 'а' and <= 'я' or 'ё';

    /// <summary>Грубая транслитерация латиницы «на слух»: spotify → спотифай, steam → стим.</summary>
    internal static string ToCyrillic(string latin)
    {
        (string From, string To)[] rules =
        {
            ("sch", "щ"), ("tch", "ч"), ("sh", "ш"), ("ch", "ч"), ("zh", "ж"), ("ts", "ц"), ("ph", "ф"), ("th", "т"),
            ("ck", "к"), ("qu", "кв"), ("ee", "и"), ("ea", "и"), ("oo", "у"), ("ou", "ау"), ("ya", "я"), ("yu", "ю"),
            ("ai", "ай"), ("ay", "эй"), ("ey", "эй"), ("oy", "ой"), ("ie", "и"), ("x", "кс"), ("j", "дж"), ("w", "в"),
        };
        var map = new Dictionary<char, string>
        {
            ['a'] = "а", ['b'] = "б", ['c'] = "к", ['d'] = "д", ['e'] = "е", ['f'] = "ф", ['g'] = "г", ['h'] = "х",
            ['i'] = "и", ['k'] = "к", ['l'] = "л", ['m'] = "м", ['n'] = "н", ['o'] = "о", ['p'] = "п", ['q'] = "к",
            ['r'] = "р", ['s'] = "с", ['t'] = "т", ['u'] = "у", ['v'] = "в", ['y'] = "и", ['z'] = "з",
        };

        var sb = new StringBuilder();
        int i = 0;
        while (i < latin.Length)
        {
            var rule = rules.FirstOrDefault(r => string.CompareOrdinal(latin, i, r.From, 0, r.From.Length) == 0);
            if (rule.From != null)
            {
                sb.Append(rule.To);
                i += rule.From.Length;
                continue;
            }
            char c = latin[i];
            if (c == 'c' && i + 1 < latin.Length && latin[i + 1] is 'e' or 'i' or 'y')
                sb.Append('с');
            else if (c == 'y' && i == latin.Length - 1 && i > 0 && latin[i - 1] == 'f')
                sb.Append("ай"); // -fy: spotify
            else if (c == 'e' && i == latin.Length - 1 && i > 1)
                { } // немая e на конце: chrome, skype
            else if (map.TryGetValue(c, out var s))
                sb.Append(s);
            i++;
        }
        return sb.ToString();
    }
}
