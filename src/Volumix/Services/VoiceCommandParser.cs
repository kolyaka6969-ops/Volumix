using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Volumix.Services;

public enum VoiceIntent
{
    Unknown,
    SetVolume,
    ChangeVolume,
    Mute,
    Unmute,
    NextDevice,
    SwitchDevice,
    SetTheme,
    SetAutostart,
    SetOsd,
    SetCloseToTray,
    SetStep,
    OpenMixer,
    OpenSettings,
    OpenHotkeys,
    HideWindows,
}

public sealed record VoiceCommand
{
    public VoiceIntent Intent { get; init; }
    public HotkeyTarget Target { get; init; } = HotkeyTarget.Master;
    /// <summary>Ключ приложения (имя exe без расширения), если Target = App.</summary>
    public string? App { get; init; }
    /// <summary>Слова, в которых не удалось узнать приложение или устройство.</summary>
    public string? Unmatched { get; init; }
    /// <summary>Громкость, шаг или величина изменения (для ChangeVolume — со знаком).</summary>
    public int? Value { get; init; }
    /// <summary>Вкл/выкл для настроек; null — переключить.</summary>
    public bool? Flag { get; init; }
    public ThemeMode? Theme { get; init; }
    public string? DeviceId { get; init; }
}

public sealed record VoiceApp(string Key, string Name);
public sealed record VoiceDevice(string Id, string Name);

/// <summary>Разбирает русскую фразу в команду. Работает по ключевым словам, без жёсткой грамматики.</summary>
public static class VoiceCommandParser
{
    public const string SystemSoundsKey = "#system";
    public const int DefaultVoiceStep = 10;

    private static readonly Dictionary<string, int> Numbers = new()
    {
        ["ноль"] = 0, ["нуль"] = 0, ["нуля"] = 0,
        ["один"] = 1, ["одна"] = 1, ["одну"] = 1, ["одного"] = 1,
        ["два"] = 2, ["две"] = 2, ["двух"] = 2,
        ["три"] = 3, ["трех"] = 3,
        ["четыре"] = 4, ["четырех"] = 4,
        ["пять"] = 5, ["пяти"] = 5,
        ["шесть"] = 6, ["шести"] = 6,
        ["семь"] = 7, ["семи"] = 7,
        ["восемь"] = 8, ["восьми"] = 8,
        ["девять"] = 9, ["девяти"] = 9,
        ["десять"] = 10, ["десяти"] = 10,
        ["одиннадцать"] = 11, ["одиннадцати"] = 11,
        ["двенадцать"] = 12, ["двенадцати"] = 12,
        ["тринадцать"] = 13, ["тринадцати"] = 13,
        ["четырнадцать"] = 14, ["четырнадцати"] = 14,
        ["пятнадцать"] = 15, ["пятнадцати"] = 15,
        ["шестнадцать"] = 16, ["шестнадцати"] = 16,
        ["семнадцать"] = 17, ["семнадцати"] = 17,
        ["восемнадцать"] = 18, ["восемнадцати"] = 18,
        ["девятнадцать"] = 19, ["девятнадцати"] = 19,
        ["двадцать"] = 20, ["двадцати"] = 20,
        ["тридцать"] = 30, ["тридцати"] = 30,
        ["сорок"] = 40, ["сорока"] = 40,
        ["пятьдесят"] = 50, ["пятидесяти"] = 50,
        ["шестьдесят"] = 60, ["шестидесяти"] = 60,
        ["семьдесят"] = 70, ["семидесяти"] = 70,
        ["восемьдесят"] = 80, ["восьмидесяти"] = 80,
        ["девяносто"] = 90, ["девяноста"] = 90,
        ["сто"] = 100, ["ста"] = 100,
        ["половина"] = 50, ["половину"] = 50, ["половины"] = 50, ["пол"] = 50,
        ["максимум"] = 100, ["максимума"] = 100, ["максимальную"] = 100, ["полную"] = 100, ["полная"] = 100,
        ["минимум"] = 0, ["минимума"] = 0,
    };

    // Слова-команды и связки: убираются перед поиском названия приложения
    private static readonly string[] FillerStems =
    {
        "громк", "звук", "сдела", "постав", "установ", "выстав", "измен", "процент", "пожалуйст", "прилож",
        "программ", "немн", "слегк", "сильн", "намног", "гораздо", "очен", "чуточ", "включ", "вкл", "выкл", "откл",
        "заглуш", "громч", "погромч", "тише", "потиш", "прибав", "убав", "увелич", "уменьш", "повыс", "пониз",
        "больш", "меньш", "добав", "подним", "опуст", "приглуш", "верн", "мьют", "мут", "замут", "замьют",
        "размут", "размьют", "сним", "давай", "можеш", "убер", "убра", "отмен", "уровень", "уровня", "поменя",
    };

    private static readonly HashSet<string> FillerWords = new()
    {
        "на", "в", "во", "до", "у", "для", "с", "со", "и", "по", "а", "ну", "мне", "еще", "пусть", "будет", "без",
        "же", "бы", "ли", "вот", "там", "чуть", "так", "как", "его", "ее", "их", "мой", "мою", "моего",
        "да", "нет", "не", "ага", "угу",
    };

    private static readonly string[] MasterStems = { "общ", "главн", "мастер", "всех", "целиком", "компьютер", "компа", "систем" };
    private static readonly HashSet<string> MasterWords = new() { "все", "всего", "везде" };
    private static readonly string[] ForegroundStems = { "текущ", "активн", "окн" };
    private static readonly HashSet<string> ForegroundWords = new() { "это", "этого", "этой", "эту", "этом", "здесь", "тут" };

    // Русские названия программ → фрагменты имени exe/описания
    private static readonly (string Stem, string[] Fragments)[] AppAliases =
    {
        ("хром", new[] { "chrome" }),
        ("гугл", new[] { "chrome" }),
        ("браузер", new[] { "chrome", "msedge", "firefox", "opera", "browser", "yandex", "brave", "vivaldi" }),
        ("ютуб", new[] { "chrome", "msedge", "firefox", "opera", "browser", "yandex", "brave", "vivaldi" }),
        ("эдж", new[] { "msedge" }),
        ("едж", new[] { "msedge" }),
        ("эйдж", new[] { "msedge" }),
        ("фаерфокс", new[] { "firefox" }),
        ("файрфокс", new[] { "firefox" }),
        ("мозил", new[] { "firefox" }),
        ("опер", new[] { "opera" }),
        ("яндекс", new[] { "browser", "yandex" }),
        ("спотиф", new[] { "spotify" }),
        ("спотик", new[] { "spotify" }),
        ("дискорд", new[] { "discord" }),
        ("диском", new[] { "discord" }),
        ("телег", new[] { "telegram" }),
        ("стим", new[] { "steam" }),
        ("зум", new[] { "zoom" }),
        ("скайп", new[] { "skype" }),
        ("тимс", new[] { "teams" }),
        ("ватсап", new[] { "whatsapp" }),
        ("вотсап", new[] { "whatsapp" }),
        ("обс", new[] { "obs" }),
        ("плеер", new[] { "vlc", "wmplayer", "music", "aimp", "foobar", "player", "mpc" }),
        ("проигрыват", new[] { "vlc", "wmplayer", "music", "aimp", "foobar", "player", "mpc" }),
        ("аимп", new[] { "aimp" }),
        ("айм", new[] { "aimp" }),
        ("майнкрафт", new[] { "javaw", "minecraft", "openjdk" }),
        ("джава", new[] { "javaw", "java", "openjdk" }),
        ("ява", new[] { "javaw", "java", "openjdk" }),
        ("музык", new[] { "spotify", "music", "aimp", "foobar", "vlc", "yandexmusic" }),
    };

    // Русские слова → фрагменты в названии устройства вывода
    private static readonly (string Stem, string[] Fragments)[] DeviceAliases =
    {
        ("наушн", new[] { "наушн", "headphone", "headset", "гарнитур", "earbuds", "buds", "airpods" }),
        ("гарнитур", new[] { "гарнитур", "headset", "наушн" }),
        ("колонк", new[] { "динамик", "speaker", "колонк" }),
        ("динамик", new[] { "динамик", "speaker" }),
        ("спикер", new[] { "динамик", "speaker" }),
        ("монитор", new[] { "monitor", "монитор", "hdmi", "displayport", "nvidia", "amd", "intel" }),
        ("телевизор", new[] { "tv", "hdmi", "телевизор" }),
        ("блютуз", new[] { "bluetooth", "buds", "airpods", "hands-free" }),
    };

    public static IEnumerable<string> NumberWords => Numbers.Keys;

    /// <summary>Насколько хорошо разобрана команда: 0 — не понята, 1 — не найдено приложение, 2 — полностью.</summary>
    public static int Quality(VoiceCommand command) =>
        command.Intent == VoiceIntent.Unknown ? 0 : command.Unmatched != null ? 1 : 2;

    /// <summary>Разбирает оба варианта распознавания и берёт тот, что понят лучше (при равенстве — свободный).</summary>
    public static (VoiceCommand Command, string Text) ParseBest(VoiceResult result, IReadOnlyList<VoiceApp> apps, IReadOnlyList<VoiceDevice> devices)
    {
        var free = Parse(result.FreeText, apps, devices);
        var grammar = Parse(result.GrammarText, apps, devices);
        var grammarText = string.Join(' ', result.GrammarText.Replace("[unk]", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Quality(grammar) > Quality(free) ? (grammar, grammarText) : (free, result.FreeText);
    }

    public static VoiceCommand Parse(string text, IReadOnlyList<VoiceApp> apps, IReadOnlyList<VoiceDevice> devices)
    {
        var words = Tokenize(text.Replace("[unk]", " "));
        if (words.Count == 0)
            return new VoiceCommand { Intent = VoiceIntent.Unknown };

        int? number = ExtractNumber(words);
        bool Has(params string[] stems) => words.Any(w => stems.Any(s => w.StartsWith(s, StringComparison.Ordinal)));
        bool HasWord(params string[] list) => words.Any(list.Contains);

        // ----- Настройки -----
        // Только существительное «тема»: «с тем» или «стим» не должны менять оформление
        bool themeNoun = HasWord("тема", "тему", "темы", "теме", "темой");
        if (themeNoun || HasWord("режим") || Has("оформлен"))
        {
            ThemeMode? theme =
                Has("темн", "ночн", "черн") ? ThemeMode.Dark :
                Has("светл", "дневн", "бел") ? ThemeMode.Light :
                Has("систем", "автомат", "винд", "windows") ? ThemeMode.System : null;
            if (theme != null || themeNoun)
                return new VoiceCommand { Intent = VoiceIntent.SetTheme, Theme = theme };
        }

        if (Has("автозапуск", "автозагруз", "автостарт", "автозапуст") || (Has("запуск", "старт") && Has("виндовс", "windows", "систем", "компьютер")))
            return new VoiceCommand { Intent = VoiceIntent.SetAutostart, Flag = ReadFlag(words) };

        if (Has("индикатор", "всплыва", "оповещ", "уведомлен", "подсказ"))
            return new VoiceCommand { Intent = VoiceIntent.SetOsd, Flag = ReadFlag(words) };

        if (Has("трей", "трэй", "tray"))
            return new VoiceCommand { Intent = VoiceIntent.SetCloseToTray, Flag = ReadFlag(words) };

        if (Has("шаг"))
            return new VoiceCommand { Intent = number is null ? VoiceIntent.Unknown : VoiceIntent.SetStep, Value = number };

        // ----- Окна -----
        if (Has("горяч", "сочетан", "клавиш"))
            return new VoiceCommand { Intent = VoiceIntent.OpenHotkeys };
        if (Has("настройк", "параметр"))
            return new VoiceCommand { Intent = VoiceIntent.OpenSettings };
        if (Has("микшер", "миксер"))
            return new VoiceCommand { Intent = Has("закр", "скр", "спряч", "убер") ? VoiceIntent.HideWindows : VoiceIntent.OpenMixer };
        if (Has("закр", "скрой", "скрыть", "спряч") && number is null)
            return new VoiceCommand { Intent = VoiceIntent.HideWindows };

        // ----- Устройство вывода -----
        if (Has("устройств", "вывод", "выход", "переключ"))
        {
            if (Has("следующ", "друг", "смен", "поменя") && !HasWord("на"))
                return new VoiceCommand { Intent = VoiceIntent.NextDevice };
            var device = MatchDevice(words, devices);
            return device != null
                ? new VoiceCommand { Intent = VoiceIntent.SwitchDevice, DeviceId = device.Id }
                : new VoiceCommand { Intent = VoiceIntent.NextDevice };
        }

        // ----- Громкость -----
        bool up = Has("громч", "погромч", "прибав", "увелич", "повыс", "больш", "подним", "выше");
        bool down = Has("тише", "потиш", "убав", "уменьш", "пониз", "меньш", "опуст", "ниже", "приглуш");
        bool muteWord = Has("заглуш", "мьют", "мут", "замут", "замьют") || HasWord("без") && Has("звук");
        bool unmuteWord = Has("размут", "размьют", "разглуш");
        bool? flag = ReadFlag(words);

        // «включи наушники» — это про устройство, а не про звук приложения
        if (flag == true && !Has("звук") && MatchDevice(words, devices) is { } wanted)
            return new VoiceCommand { Intent = VoiceIntent.SwitchDevice, DeviceId = wanted.Id };

        VoiceIntent intent;
        int? value = null;
        if (unmuteWord)
            intent = VoiceIntent.Unmute;
        else if (muteWord && !up)
            intent = VoiceIntent.Mute;
        else if ((up || down) && !(number != null && HasWord("до"))) // «увеличь до 50» — это установка
        {
            intent = VoiceIntent.ChangeVolume;
            int amount = number ?? (Has("немн", "чуть", "слегк", "чуточ") || HasWord("чуть") ? 5
                : Has("сильн", "намног", "гораздо", "очен") ? 20 : DefaultVoiceStep);
            value = up ? amount : -amount;
        }
        else if (number != null)
        {
            intent = VoiceIntent.SetVolume;
            value = Math.Clamp(number.Value, 0, 100);
        }
        else if (flag != null)
        {
            // «выключи телеграм» — команда, а просто «да» или «включи» — нет
            var target = ResolveTarget(words, apps);
            bool explicitTarget = target.App != null || target.Target == HotkeyTarget.Foreground ||
                                  words.Any(w => MasterWords.Contains(w) || MasterStems.Any(s => w.StartsWith(s, StringComparison.Ordinal)));
            if (!Has("звук") && !explicitTarget)
                return new VoiceCommand { Intent = VoiceIntent.Unknown };
            return target with { Intent = flag == false ? VoiceIntent.Mute : VoiceIntent.Unmute };
        }
        else
        {
            // Просто название устройства: «наушники»
            var device = MatchDevice(words, devices);
            if (device != null)
                return new VoiceCommand { Intent = VoiceIntent.SwitchDevice, DeviceId = device.Id };
            return new VoiceCommand { Intent = VoiceIntent.Unknown };
        }

        return ResolveTarget(words, apps) with { Intent = intent, Value = value };
    }

    // ---------- Цель команды ----------

    private static VoiceCommand ResolveTarget(List<string> words, IReadOnlyList<VoiceApp> apps)
    {
        bool hasSystem = words.Any(w => w.StartsWith("системн", StringComparison.Ordinal));
        bool hasSound = words.Any(w => w.StartsWith("звук", StringComparison.Ordinal));
        if (hasSystem && hasSound && apps.Any(a => a.Key == SystemSoundsKey))
            return new VoiceCommand { Target = HotkeyTarget.App, App = SystemSoundsKey };

        var rest = words.Where(w => !IsFiller(w)).ToList();

        if (rest.Any(w => MasterWords.Contains(w) || MasterStems.Any(s => w.StartsWith(s, StringComparison.Ordinal))))
            return new VoiceCommand { Target = HotkeyTarget.Master };
        if (rest.Any(w => ForegroundWords.Contains(w) || ForegroundStems.Any(s => w.StartsWith(s, StringComparison.Ordinal))))
            return new VoiceCommand { Target = HotkeyTarget.Foreground };
        if (rest.Count == 0)
            return new VoiceCommand { Target = HotkeyTarget.Master };

        var app = MatchApp(words, apps);
        return app != null
            ? new VoiceCommand { Target = HotkeyTarget.App, App = app.Key }
            : new VoiceCommand { Target = HotkeyTarget.App, Unmatched = string.Join(' ', rest) };
    }

    private static bool IsFiller(string w) =>
        FillerWords.Contains(w) || FillerStems.Any(s => w.StartsWith(s, StringComparison.Ordinal));

    internal static VoiceApp? MatchApp(List<string> words, IReadOnlyList<VoiceApp> apps)
    {
        // Склеиваем до трёх соседних слов: распознаватель часто режет название на части («дис корд», «спот и фай»)
        var phrases = new List<string>();
        for (int i = 0; i < words.Count; i++)
            for (int len = 1; len <= 3 && i + len <= words.Count; len++)
                if (!IsFiller(words[i]) && !IsFiller(words[i + len - 1]))
                    phrases.Add(string.Concat(words.Skip(i).Take(len)));

        VoiceApp? best = null;
        double bestScore = 0;
        foreach (var app in apps)
        {
            if (app.Key == SystemSoundsKey)
                continue;
            var key = app.Key.ToLowerInvariant();
            var name = app.Name.ToLowerInvariant();
            var candidates = SplitWords(name).Append(key).Distinct().ToList();

            foreach (var phrase in phrases)
            {
                double score = 0;
                foreach (var (stem, fragments) in AppAliases)
                    if (phrase.StartsWith(stem, StringComparison.Ordinal) &&
                        fragments.Any(f => key.Contains(f) || name.Contains(f)))
                        score = 1;

                if (score < 1 && phrase.Length >= 4)
                {
                    var latin = Transliterate(phrase);
                    foreach (var c in candidates)
                        score = Math.Max(score, Math.Max(Similarity(phrase, c), Similarity(latin, c)));
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = app;
                }
            }
        }
        return bestScore >= 0.65 ? best : null;
    }

    internal static VoiceDevice? MatchDevice(List<string> words, IReadOnlyList<VoiceDevice> devices)
    {
        VoiceDevice? best = null;
        double bestScore = 0;
        foreach (var device in devices)
        {
            var name = device.Name.ToLowerInvariant().Replace('ё', 'е');
            var nameWords = SplitWords(name);
            foreach (var w in words)
            {
                double score = 0;
                foreach (var (stem, fragments) in DeviceAliases)
                    if (w.StartsWith(stem, StringComparison.Ordinal) && fragments.Any(name.Contains))
                        score = 1;
                if (score < 1 && w.Length >= 4 && !IsFiller(w))
                    foreach (var nw in nameWords)
                        score = Math.Max(score, Math.Max(Similarity(w, nw), Similarity(Transliterate(w), nw)));
                if (score > bestScore)
                {
                    bestScore = score;
                    best = device;
                }
            }
        }
        return bestScore >= 0.75 ? best : null;
    }

    // ---------- Вспомогательное ----------

    internal static List<string> Tokenize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
            sb.Append(ch == 'ё' ? 'е' : char.IsLetterOrDigit(ch) ? ch : ' ');
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static List<string> SplitWords(string text) =>
        Tokenize(text).Where(w => w.Length >= 2).ToList();

    /// <summary>Находит первое число (словами или цифрами), удаляет его из списка слов.</summary>
    internal static int? ExtractNumber(List<string> words)
    {
        for (int i = 0; i < words.Count; i++)
        {
            if (int.TryParse(words[i], out int digits))
            {
                words.RemoveAt(i);
                return digits;
            }
            if (!Numbers.TryGetValue(words[i], out int value))
                continue;

            int count = 1;
            if (value is >= 20 and <= 90 && value % 10 == 0 && i + 1 < words.Count &&
                Numbers.TryGetValue(words[i + 1], out int unit) && unit is >= 1 and <= 9 &&
                words[i + 1] is not ("пол" or "половина" or "половину"))
            {
                value += unit;
                count = 2;
            }
            words.RemoveRange(i, count);
            return value;
        }
        return null;
    }

    private static bool? ReadFlag(List<string> words)
    {
        if (words.Any(w => w is "не" or "нет" || w.StartsWith("выкл", StringComparison.Ordinal) ||
                           w.StartsWith("откл", StringComparison.Ordinal) || w.StartsWith("убер", StringComparison.Ordinal) ||
                           w.StartsWith("убра", StringComparison.Ordinal) || w.StartsWith("запрет", StringComparison.Ordinal) ||
                           w.StartsWith("отмен", StringComparison.Ordinal)))
            return false;
        if (words.Any(w => w is "да" || w.StartsWith("вкл", StringComparison.Ordinal) || w.StartsWith("включ", StringComparison.Ordinal) ||
                           w.StartsWith("верн", StringComparison.Ordinal) || w.StartsWith("разреш", StringComparison.Ordinal) ||
                           w.StartsWith("показыв", StringComparison.Ordinal) || w.StartsWith("покаж", StringComparison.Ordinal)))
            return true;
        return null;
    }

    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ж'] = "zh", ['з'] = "z",
        ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p",
        ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts", ['ч'] = "ch",
        ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    internal static string Transliterate(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        foreach (var ch in text)
            sb.Append(Translit.TryGetValue(ch, out var s) ? s : ch.ToString());
        return sb.ToString();
    }

    internal static double Similarity(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2)
            return 0;
        if (a == b)
            return 1;
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
            prefix++;
        if (prefix >= 5 || (prefix >= 4 && Math.Min(a.Length, b.Length) <= 5))
            return 0.9; // разные падежи одного слова: «спотифая» / «спотифай»
        return 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
