using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Volumix.Services;

public enum ThemeMode { System, Light, Dark }

public enum HotkeyAction
{
    VolumeUp, VolumeDown, ToggleMute, OpenMixer, NextDevice,
    /// <summary>Голосовые команды убраны. Значение оставлено, чтобы старый settings.json читался: такие привязки удаляются при загрузке.</summary>
    VoiceCommand,
}

public enum HotkeyTarget { Foreground, Master, App }

public sealed class HotkeyBinding
{
    public HotkeyAction Action { get; set; }
    public HotkeyTarget Target { get; set; }
    /// <summary>Имя exe без расширения (для Target = App).</summary>
    public string? App { get; set; }
    public ModifierKeys Modifiers { get; set; }
    public Key Key { get; set; }
}

public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public int VolumeStep { get; set; } = 2;
    public bool ShowOsd { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    /// <summary>Песня и её текст на рабочем столе вместо обоев, пока играет песня.</summary>
    public bool DesktopLyrics { get; set; }
    /// <summary>Островок вверху экрана: что играет в любом плеере, сколько прошло и кнопки. Во время игр прячется.</summary>
    public bool MediaIsland { get; set; }
    /// <summary>Через сколько секунд паузы прятать островок; 0 — не прятать.</summary>
    public int IslandPauseHide { get; set; } = DefaultIslandPauseHide;

    public const int DefaultIslandPauseHide = 60;
    /// <summary>Ширина островка в точках при масштабе 1.</summary>
    public double IslandWidth { get; set; } = 400;
    /// <summary>Масштаб островка: тянут за нижний край.</summary>
    public double IslandScale { get; set; } = 1;
    /// <summary>Ширина островка на панели задач.</summary>
    public double IslandTaskbarWidth { get; set; } = 320;
    /// <summary>Где островок на экране: доли свободного места слева и сверху. 0,5 и 0 — вверху посередине.</summary>
    public double IslandX { get; set; } = 0.5;
    public double IslandY { get; set; }
    /// <summary>Монитор островка, «» — основной.</summary>
    public string IslandScreen { get; set; } = "";
    /// <summary>Островок закреплён на панели задач, в <see cref="IslandTaskbarX"/> долях её длины.</summary>
    public bool IslandOnTaskbar { get; set; }
    public double IslandTaskbarX { get; set; } = 0.75;
    /// <summary>Островок спрятан в край экрана и выезжает, когда к краю подводят курсор.</summary>
    public bool IslandDocked { get; set; }
    /// <summary>Через сколько секунд островок сам прячется в край; 0 — не прячется.</summary>
    public int IslandAutoHide { get; set; }
    /// <summary>Скорость бегущей строки длинного названия на обоях, точек макета 1920×1080 в секунду.</summary>
    public int MarqueeSpeed { get; set; } = DefaultMarqueeSpeed;

    public const int DefaultMarqueeSpeed = 22;
    public const int MinMarqueeSpeed = 5;
    public const int MaxMarqueeSpeed = 100;

    /// <summary>Кадров в секунду у бегущей строки. Выше частоты монитора не бывает: лишнее WPF не рисует.</summary>
    public int MarqueeFps { get; set; } = DefaultMarqueeFps;

    public const int DefaultMarqueeFps = 60;
    public const int MinMarqueeFps = 15;
    public const int MaxMarqueeFps = 240;
    /// <summary>Кнопки «назад», «играть/пауза», «вперёд» на обоях под полосой прогресса.</summary>
    public bool DesktopControls { get; set; } = true;
    /// <summary>На каких мониторах обои с песней: <see cref="AllScreens"/>, <see cref="PrimaryScreen"/> или имя устройства.</summary>
    public string DesktopScreen { get; set; } = AllScreens;

    public const string AllScreens = "";
    public const string PrimaryScreen = "primary";
    /// <summary>Откуда брать песню: Spotify, Яндекс Музыка (в том числе в браузере) или и то, и другое.</summary>
    public MusicSourceMode Source { get; set; } = MusicSourceMode.Auto;
    /// <summary>Версия формата настроек, для добавления новых значений по умолчанию старым пользователям.</summary>
    public int Version { get; set; }

    public List<HotkeyBinding> Hotkeys { get; set; } = new()
    {
        new() { Action = HotkeyAction.VolumeUp, Target = HotkeyTarget.Foreground, Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.PageUp },
        new() { Action = HotkeyAction.VolumeDown, Target = HotkeyTarget.Foreground, Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.PageDown },
        new() { Action = HotkeyAction.ToggleMute, Target = HotkeyTarget.Foreground, Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.End },
        new() { Action = HotkeyAction.OpenMixer, Modifiers = ModifierKeys.Control | ModifierKeys.Alt, Key = Key.M },
    };
}

public static class SettingsService
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Volumix");

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch
        {
            Current = new();
        }
        Current.VolumeStep = Math.Clamp(Current.VolumeStep, 1, 25);
        // Голосовые команды убраны: их сочетание клавиш больше ничего не делает и должно освободиться
        if (Current.Hotkeys.RemoveAll(h => h.Action == HotkeyAction.VoiceCommand) > 0)
            Save();
        Migrate(Current);
    }

    private const int CurrentVersion = 4;

    private static void Migrate(AppSettings s)
    {
        if (s.Version >= CurrentVersion)
            return;

        // v4: старый захват клавиш терял Win/Alt и мог сохранить одиночную букву — чиним такие сочетания
        foreach (var h in s.Hotkeys)
            if (h.Key != Key.None && h.Modifiers == ModifierKeys.None && !HotkeyService.IsStandaloneKey(h.Key))
                (h.Modifiers, h.Key) = (ModifierKeys.None, Key.None);

        s.Version = CurrentVersion;
        Save();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch
        {
            // Не критично: настройки просто не сохранятся.
        }
    }
}
