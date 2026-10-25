using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Volumix.Services;
using Volumix.ViewModels;
using Volumix.Views;

namespace Volumix;

public partial class App : Application
{
    private const string MutexName = "Volumix_SingleInstance";
    private const string ShowEventName = "Volumix_ShowWindow";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private HotkeyService? _hotkeys;
    private TrayService? _tray;
    private MainWindow? _main;
    private FlyoutWindow? _flyout;
    private OsdWindow? _osd;
    private MediaSessionService? _media;
    private DesktopLyricsService? _desktopLyrics;

    public static MixerViewModel Mixer { get; private set; } = null!;
    public static SettingsViewModel SettingsVm { get; private set; } = null!;
    public static bool IsExiting { get; private set; }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Уже запущено — просим первую копию показать окно.
            AllowSetForegroundWindow(-1);
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject);

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne() && !IsExiting)
                Dispatcher.BeginInvoke(() => ShowMain(null));
        }) { IsBackground = true, Name = "Volumix show listener" }.Start();

        SettingsService.Load();
        ThemeService.Apply(SettingsService.Current.Theme);
        try { AutostartService.RepairPath(); } catch (Exception) { }

        Mixer = new MixerViewModel(Dispatcher);
        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnHotkey;
        SettingsVm = new SettingsViewModel(_hotkeys, Mixer);
        _media = new MediaSessionService(Dispatcher) { Mode = SettingsVm.Source };
        _tray = new TrayService(this, Mixer, SettingsVm, _media);
        _desktopLyrics = new DesktopLyricsService(_media, Dispatcher)
        {
            MarqueeSpeed = SettingsVm.MarqueeSpeed,
            MarqueeFps = SettingsVm.MarqueeFps,
            Screen = SettingsVm.DesktopScreen,
            Controls = SettingsVm.DesktopControls,
            Enabled = SettingsVm.DesktopLyrics,
        };
        SettingsVm.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(SettingsViewModel.DesktopLyrics):
                    _desktopLyrics.Enabled = SettingsVm.DesktopLyrics;
                    break;
                case nameof(SettingsViewModel.MarqueeSpeed):
                    _desktopLyrics.MarqueeSpeed = SettingsVm.MarqueeSpeed;
                    break;
                case nameof(SettingsViewModel.MarqueeFps):
                    _desktopLyrics.MarqueeFps = SettingsVm.MarqueeFps;
                    break;
                case nameof(SettingsViewModel.DesktopScreen):
                    _desktopLyrics.Screen = SettingsVm.DesktopScreen;
                    break;
                case nameof(SettingsViewModel.DesktopControls):
                    _desktopLyrics.Controls = SettingsVm.DesktopControls;
                    break;
                case nameof(SettingsViewModel.Source):
                    _media.Mode = SettingsVm.Source;
                    break;
            }
        };
        _media.Start();

        if (!e.Args.Contains(AutostartService.TrayArgument, StringComparer.OrdinalIgnoreCase))
            ShowMain(AppPage.Mixer);
    }

    public void ShowMain(AppPage? page)
    {
        _flyout?.HideFlyout();
        _main ??= new MainWindow();
        if (page is { } p)
            _main.ShowPage(p);
        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
            _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;
        _main.Topmost = false;
        _main.Focus();
    }

    public void ToggleFlyout()
    {
        _flyout ??= new FlyoutWindow();
        if (_flyout.IsVisible)
            _flyout.HideFlyout();
        else if (!_flyout.JustHidden)
            _flyout.ShowFlyout();
    }

    public void ShowFlyout()
    {
        _flyout ??= new FlyoutWindow();
        _flyout.ShowFlyout();
    }

    public void HideWindows()
    {
        _flyout?.HideFlyout();
        _main?.Hide();
    }

    private void OnHotkey(HotkeyBinding b)
    {
        var settings = SettingsService.Current;
        _osd ??= new OsdWindow();

        switch (b.Action)
        {
            case HotkeyAction.OpenMixer:
                ToggleFlyout();
                return;
            case HotkeyAction.NextDevice:
                Mixer.NextDevice();
                Mixer.Refresh();
                if (settings.ShowOsd && Mixer.SelectedDevice is { } d)
                    _osd.ShowMessage(d.Glyph, d.Name);
                return;
        }

        Mixer.Refresh();
        ChannelViewModel? channel;
        string missing;
        switch (b.Target)
        {
            case HotkeyTarget.Master:
                channel = Mixer.HasDevice ? Mixer.Master : null;
                missing = "Нет устройства вывода";
                break;
            case HotkeyTarget.App:
                channel = Mixer.FindApp(b.App);
                missing = $"{b.App}: нет звука";
                break;
            default:
                NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
                var info = ProcessInfo.Get(pid);
                channel = Mixer.FindApp(info?.Key);
                missing = $"{info?.DisplayName ?? "Активное окно"}: нет звука";
                break;
        }

        if (channel == null)
        {
            if (settings.ShowOsd)
                _osd.ShowMessage("", missing);
            return;
        }

        switch (b.Action)
        {
            case HotkeyAction.VolumeUp:
                channel.IsMuted = false;
                channel.Volume += settings.VolumeStep;
                break;
            case HotkeyAction.VolumeDown:
                channel.Volume -= settings.VolumeStep;
                break;
            case HotkeyAction.ToggleMute:
                channel.IsMuted = !channel.IsMuted;
                break;
        }

        if (settings.ShowOsd)
            _osd.ShowChannel(channel);
    }

    public void ExitApp()
    {
        if (IsExiting)
            return;
        IsExiting = true;
        _desktopLyrics?.Dispose();
        _tray?.Dispose();
        _media?.Dispose();
        _hotkeys?.Dispose();
        Mixer?.Dispose();
        _showEvent?.Set();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Дорожка ползунка WPF-UI идёт на всю ширину, а бегунок — круг: на 0 и 100 % её скруглённый край
    /// выглядывает из-под него. Дорожка от центра бегунка до центра бегунка — как индикатор уровня под ней.
    /// </summary>
    private void VolumeSlider_Loaded(object sender, RoutedEventArgs e)
    {
        var slider = (System.Windows.Controls.Slider)sender;
        if (slider.Template?.FindName("TrackBackground", slider) is FrameworkElement track &&
            slider.Template.FindName("Thumb", slider) is FrameworkElement thumb)
            track.Margin = new Thickness(thumb.Width / 2, 0, thumb.Width / 2, 0);
    }

    internal static void LogError(object error)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
            File.AppendAllText(Path.Combine(SettingsService.Folder, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}\n\n");
        }
        catch (IOException) { }
    }
}
