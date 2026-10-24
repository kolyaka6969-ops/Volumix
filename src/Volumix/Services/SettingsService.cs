using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Volumix.Services;

public enum ThemeMode { System, Light, Dark }

public enum HotkeyAction { VolumeUp, VolumeDown, ToggleMute, OpenMixer, NextDevice, VoiceCommand }

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
    public bool VoiceEnabled { get; set; } = true;
    /// <summary>Кнопки Spotify (назад / пауза / вперёд) значками в трее.</summary>
    public bool MediaButtons { get; set; } = true;
    /// <summary>Песня и её текст на рабочем столе вместо обоев, пока открыт Spotify.</summary>
    public bool DesktopLyrics { get; set; }
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
        Migrate(Current);
    }

    private const int CurrentVersion = 4;
    private const ModifierKeys VoiceModifiers = ModifierKeys.Windows | ModifierKeys.Alt;
    private const Key VoiceKey = Key.V;

    private static void Migrate(AppSettings s)
    {
        if (s.Version >= CurrentVersion)
            return;

        // v2: голосовые команды
        var voice = s.Hotkeys.Find(h => h.Action == HotkeyAction.VoiceCommand);
        if (voice == null)
            s.Hotkeys.Add(new() { Action = HotkeyAction.VoiceCommand, Modifiers = VoiceModifiers, Key = VoiceKey });
        // v3: Ctrl+Alt+Space часто занят (переключение раскладки, IME) — меняем на Win+Alt+V
        else if (voice.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && voice.Key == Key.Space)
            (voice.Modifiers, voice.Key) = (VoiceModifiers, VoiceKey);

        // v4: старый захват клавиш терял Win/Alt и мог сохранить одиночную букву — чиним такие сочетания
        foreach (var h in s.Hotkeys)
            if (h.Key != Key.None && h.Modifiers == ModifierKeys.None && !HotkeyService.IsStandaloneKey(h.Key))
                (h.Modifiers, h.Key) = h.Action == HotkeyAction.VoiceCommand ? (VoiceModifiers, VoiceKey) : (ModifierKeys.None, Key.None);

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
