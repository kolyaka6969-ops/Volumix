using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Windows.Input;
using Volumix.Services;

namespace Volumix.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly HotkeyService _hotkeys;
    private readonly MixerViewModel _mixer;

    public SettingsViewModel(HotkeyService hotkeys, MixerViewModel mixer)
    {
        _hotkeys = hotkeys;
        _mixer = mixer;
        foreach (var b in S.Hotkeys)
            Hotkeys.Add(new HotkeyBindingViewModel(b, this));
        AddHotkeyCommand = new RelayCommand(AddHotkey);
        _hotkeys.RegistrationChanged += () =>
        {
            foreach (var h in Hotkeys)
                h.RefreshStatus(_hotkeys.Failed.Contains(h.Model));
        };
        ApplyHotkeys();
        NotifyKnownAppsChanged();
    }

    private static AppSettings S => SettingsService.Current;

    public static IReadOnlyList<Option<ThemeMode>> Themes { get; } = new[]
    {
        new Option<ThemeMode>(ThemeMode.System, "Как в системе"),
        new Option<ThemeMode>(ThemeMode.Light, "Светлая"),
        new Option<ThemeMode>(ThemeMode.Dark, "Тёмная"),
    };

    public static IReadOnlyList<Option<MusicSourceMode>> Sources { get; } = new[]
    {
        new Option<MusicSourceMode>(MusicSourceMode.Auto, "Spotify, Яндекс Музыка и YouTube"),
        new Option<MusicSourceMode>(MusicSourceMode.Spotify, "Только Spotify"),
        new Option<MusicSourceMode>(MusicSourceMode.YandexMusic, "Яндекс Музыка (и браузер)"),
        new Option<MusicSourceMode>(MusicSourceMode.YouTube, "YouTube (и браузер)"),
    };

    public string Version => "Версия " + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");

    public bool Autostart
    {
        get => AutostartService.IsEnabled;
        set
        {
            try { AutostartService.SetEnabled(value); }
            catch (Exception) { }
            OnPropertyChanged();
        }
    }

    public ThemeMode Theme
    {
        get => S.Theme;
        set
        {
            if (S.Theme == value) return;
            S.Theme = value;
            ThemeService.Apply(value);
            Save();
            OnPropertyChanged();
        }
    }

    public int VolumeStep
    {
        get => S.VolumeStep;
        set
        {
            value = Math.Clamp(value, 1, 25);
            if (S.VolumeStep == value) return;
            S.VolumeStep = value;
            Save();
            OnPropertyChanged();
        }
    }

    public bool ShowOsd
    {
        get => S.ShowOsd;
        set { if (S.ShowOsd == value) return; S.ShowOsd = value; Save(); OnPropertyChanged(); }
    }

    public bool CloseToTray
    {
        get => S.CloseToTray;
        set { if (S.CloseToTray == value) return; S.CloseToTray = value; Save(); OnPropertyChanged(); }
    }

    public bool MediaButtons
    {
        get => S.MediaButtons;
        set { if (S.MediaButtons == value) return; S.MediaButtons = value; Save(); OnPropertyChanged(); }
    }

    public bool DesktopLyrics
    {
        get => S.DesktopLyrics;
        set { if (S.DesktopLyrics == value) return; S.DesktopLyrics = value; Save(); OnPropertyChanged(); }
    }

    public MusicSourceMode Source
    {
        get => S.Source;
        set { if (S.Source == value) return; S.Source = value; Save(); OnPropertyChanged(); }
    }

    // ---------- Горячие клавиши ----------

    public ObservableCollection<HotkeyBindingViewModel> Hotkeys { get; } = new();
    public ICommand AddHotkeyCommand { get; }

    /// <summary>Подсказки для выбора приложения: всё, что сейчас играет звук.</summary>
    public ObservableCollection<string> KnownApps { get; } = new();

    /// <summary>Дополняет список (а не пересоздаёт его, чтобы не сбить текст в редактируемых ComboBox).</summary>
    public void NotifyKnownAppsChanged()
    {
        _mixer.Refresh();
        var names = _mixer.Apps.Where(a => !a.IsSystem).Select(a => a.Key)
            .Concat(S.Hotkeys.Where(h => !string.IsNullOrEmpty(h.App)).Select(h => h.App!));
        foreach (var name in names)
            if (!KnownApps.Contains(name, StringComparer.OrdinalIgnoreCase))
                KnownApps.Add(name);
    }

    private void AddHotkey()
    {
        var model = new HotkeyBinding { Action = HotkeyAction.VolumeUp, Target = HotkeyTarget.Foreground };
        S.Hotkeys.Add(model);
        Hotkeys.Add(new HotkeyBindingViewModel(model, this));
        Save();
    }

    internal void Remove(HotkeyBindingViewModel vm)
    {
        S.Hotkeys.Remove(vm.Model);
        Hotkeys.Remove(vm);
        ApplyHotkeys();
        Save();
    }

    internal void ApplyHotkeys() => _hotkeys.Apply(S.Hotkeys);

    internal void SuspendHotkeys() => _hotkeys.Suspend();
    internal void ResumeHotkeys() => _hotkeys.Resume();

    internal static void Save() => SettingsService.Save();
}

public sealed class HotkeyBindingViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;
    private bool _isFailed;

    public HotkeyBindingViewModel(HotkeyBinding model, SettingsViewModel owner)
    {
        Model = model;
        _owner = owner;
        RemoveCommand = new RelayCommand(() => _owner.Remove(this));
    }

    public HotkeyBinding Model { get; }
    public ICommand RemoveCommand { get; }

    public static IReadOnlyList<Option<HotkeyAction>> Actions { get; } = new[]
    {
        new Option<HotkeyAction>(HotkeyAction.VolumeUp, "Громче"),
        new Option<HotkeyAction>(HotkeyAction.VolumeDown, "Тише"),
        new Option<HotkeyAction>(HotkeyAction.ToggleMute, "Вкл/выкл звук"),
        new Option<HotkeyAction>(HotkeyAction.OpenMixer, "Открыть микшер"),
        new Option<HotkeyAction>(HotkeyAction.NextDevice, "Следующее устройство вывода"),
    };

    public static IReadOnlyList<Option<HotkeyTarget>> Targets { get; } = new[]
    {
        new Option<HotkeyTarget>(HotkeyTarget.Foreground, "Активное окно"),
        new Option<HotkeyTarget>(HotkeyTarget.Master, "Общая громкость"),
        new Option<HotkeyTarget>(HotkeyTarget.App, "Приложение…"),
    };

    public HotkeyAction Action
    {
        get => Model.Action;
        set
        {
            if (Model.Action == value) return;
            Model.Action = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTarget));
            OnPropertyChanged(nameof(HasApp));
            SettingsViewModel.Save();
        }
    }

    public HotkeyTarget Target
    {
        get => Model.Target;
        set
        {
            if (Model.Target == value) return;
            Model.Target = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasApp));
            SettingsViewModel.Save();
        }
    }

    public string? App
    {
        get => Model.App;
        set
        {
            value = value?.Trim();
            if (value != null && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                value = value[..^4];
            if (Model.App == value) return;
            Model.App = value;
            OnPropertyChanged();
            SettingsViewModel.Save();
        }
    }

    public bool HasTarget => Action is HotkeyAction.VolumeUp or HotkeyAction.VolumeDown or HotkeyAction.ToggleMute;
    public bool HasApp => HasTarget && Target == HotkeyTarget.App;

    public string GestureText => Model.Key == Key.None ? "" : HotkeyService.Format(Model.Modifiers, Model.Key);

    public bool IsFailed
    {
        get => _isFailed;
        private set => Set(ref _isFailed, value);
    }

    internal void RefreshStatus(bool failed) => IsFailed = failed;

    public void SetGesture(ModifierKeys modifiers, Key key)
    {
        Model.Modifiers = modifiers;
        Model.Key = key;
        OnPropertyChanged(nameof(GestureText));
        SettingsViewModel.Save();
    }

    public void BeginCapture() => _owner.SuspendHotkeys();
    public void EndCapture() => _owner.ResumeHotkeys();
}
