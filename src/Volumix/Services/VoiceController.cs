using System;
using System.Diagnostics;
using System.Linq;
using Volumix.ViewModels;
using Volumix.Views;

namespace Volumix.Services;

/// <summary>Связывает распознавание речи с микшером и настройками: слушает, разбирает фразу, выполняет, показывает итог.</summary>
public sealed class VoiceController : IDisposable
{
    private const string MicGlyph = "";
    private const string WarningGlyph = "";
    private const string SettingsGlyph = "";

    private readonly App _app;
    private readonly MixerViewModel _mixer;
    private readonly SettingsViewModel _settings;
    private readonly Func<OsdWindow> _osd;
    private readonly VoiceService _voice;
    private string? _foregroundKey;

    public VoiceController(App app, MixerViewModel mixer, SettingsViewModel settings, Func<OsdWindow> osd)
    {
        _app = app;
        _mixer = mixer;
        _settings = settings;
        _osd = osd;
        _voice = new VoiceService(app.Dispatcher);
        _voice.Loading += () => _osd().ShowListening("Загружаю распознавание речи…");
        _voice.ListeningStarted += () => _osd().ShowListening("Слушаю…");
        _voice.PartialText += text => _osd().ShowListening(text);
        _voice.Failed += message => _osd().ShowMessage(WarningGlyph, message);
        _voice.Recognized += OnRecognized;
    }

    public bool IsListening => _voice.IsListening;

    /// <summary>Начать слушать; повторный вызов во время прослушивания — отмена.</summary>
    public void Toggle()
    {
        if (_voice.IsListening)
        {
            _voice.Cancel();
            _osd().ShowMessage(MicGlyph, "Голосовая команда отменена");
            return;
        }
        if (!SettingsService.Current.VoiceEnabled)
        {
            _osd().ShowMessage(MicGlyph, "Голосовые команды выключены в настройках");
            return;
        }

        // Запоминаем активное окно сейчас: пока человек говорит, фокус может уйти
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
        _foregroundKey = pid == (uint)Environment.ProcessId ? null : ProcessInfo.Get(pid)?.Key;

        _mixer.Refresh();
        var apps = CurrentApps();
        var devices = CurrentDevices();
        _voice.Start(inVocabulary => VoiceGrammar.Build(apps, devices, inVocabulary));
    }

    private VoiceApp[] CurrentApps() =>
        _mixer.Apps.Select(a => new VoiceApp(a.Key, a.Name)).ToArray();

    private VoiceDevice[] CurrentDevices() =>
        _mixer.Devices.Select(d => new VoiceDevice(d.Id, d.Name)).ToArray();

    private void OnRecognized(VoiceResult result)
    {
        _mixer.Refresh();
        var (command, text) = VoiceCommandParser.ParseBest(result, CurrentApps(), CurrentDevices());
        Debug.WriteLine($"Voice: free «{result.FreeText}», grammar «{result.GrammarText}» → {command}");
        Execute(command, text);
    }

    private void Execute(VoiceCommand c, string text)
    {
        var osd = _osd();
        string heard = $"«{text}»";

        switch (c.Intent)
        {
            case VoiceIntent.Unknown:
                osd.ShowMessage(MicGlyph, text.Length == 0 ? "Не расслышал команду" : "Не понял команду",
                    text.Length == 0 ? null : heard);
                return;

            case VoiceIntent.SetVolume or VoiceIntent.ChangeVolume or VoiceIntent.Mute or VoiceIntent.Unmute:
                ExecuteVolume(c, heard);
                return;

            case VoiceIntent.NextDevice:
                _mixer.NextDevice();
                ShowDevice(heard);
                return;

            case VoiceIntent.SwitchDevice:
                if (_mixer.Devices.FirstOrDefault(d => d.Id == c.DeviceId) is { } device)
                    _mixer.SelectedDevice = device;
                ShowDevice(heard);
                return;

            case VoiceIntent.SetTheme:
                var theme = c.Theme ?? (ThemeService.Effective == Wpf.Ui.Appearance.ApplicationTheme.Dark ? ThemeMode.Light : ThemeMode.Dark);
                _settings.Theme = theme;
                osd.ShowMessage("", "Тема: " + (theme switch
                {
                    ThemeMode.Dark => "тёмная",
                    ThemeMode.Light => "светлая",
                    _ => "как в системе",
                }), heard);
                return;

            case VoiceIntent.SetAutostart:
                _settings.Autostart = c.Flag ?? !_settings.Autostart;
                osd.ShowMessage("", _settings.Autostart ? "Автозапуск включён" : "Автозапуск выключен", heard);
                return;

            case VoiceIntent.SetOsd:
                _settings.ShowOsd = c.Flag ?? !_settings.ShowOsd;
                osd.ShowMessage(SettingsGlyph, _settings.ShowOsd ? "Индикатор громкости включён" : "Индикатор громкости выключен", heard);
                return;

            case VoiceIntent.SetCloseToTray:
                _settings.CloseToTray = c.Flag ?? !_settings.CloseToTray;
                osd.ShowMessage(SettingsGlyph, _settings.CloseToTray ? "Окно будет сворачиваться в трей" : "Окно будет закрываться полностью", heard);
                return;

            case VoiceIntent.SetStep:
                _settings.VolumeStep = c.Value ?? _settings.VolumeStep;
                osd.ShowMessage(SettingsGlyph, $"Шаг громкости: {_settings.VolumeStep}%", heard);
                return;

            case VoiceIntent.OpenMixer:
                _app.ShowFlyout();
                return;

            case VoiceIntent.OpenSettings:
                _app.ShowMain(AppPage.Settings);
                return;

            case VoiceIntent.OpenHotkeys:
                _app.ShowMain(AppPage.Hotkeys);
                return;

            case VoiceIntent.HideWindows:
                _app.HideWindows();
                osd.ShowMessage(MicGlyph, "Окна скрыты", heard);
                return;
        }
    }

    private void ExecuteVolume(VoiceCommand c, string heard)
    {
        var osd = _osd();
        ChannelViewModel? channel = c.Target switch
        {
            HotkeyTarget.Master => _mixer.HasDevice ? _mixer.Master : null,
            HotkeyTarget.Foreground => _mixer.FindApp(_foregroundKey),
            _ => _mixer.FindApp(c.App),
        };

        if (channel == null)
        {
            string message = c.Unmatched != null ? $"Не нашёл приложение «{c.Unmatched}»"
                : c.Target == HotkeyTarget.Master ? "Нет устройства вывода"
                : c.Target == HotkeyTarget.Foreground ? "У активного окна нет звука"
                : "Это приложение сейчас не играет звук";
            osd.ShowMessage(WarningGlyph, message, heard);
            return;
        }

        switch (c.Intent)
        {
            case VoiceIntent.SetVolume:
                channel.IsMuted = false;
                channel.Volume = c.Value ?? channel.Volume;
                break;
            case VoiceIntent.ChangeVolume:
                if (c.Value > 0)
                    channel.IsMuted = false;
                channel.Volume += c.Value ?? 0;
                break;
            case VoiceIntent.Mute:
                channel.IsMuted = true;
                break;
            case VoiceIntent.Unmute:
                channel.IsMuted = false;
                break;
        }
        osd.ShowChannel(channel, heard);
    }

    private void ShowDevice(string heard)
    {
        _mixer.Refresh();
        if (_mixer.SelectedDevice is { } d)
            _osd().ShowMessage(d.Glyph, d.Name, heard);
    }

    public void Dispose() => _voice.Dispose();
}
