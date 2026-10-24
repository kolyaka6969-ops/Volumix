using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Volumix.ViewModels;
using Volumix.Views;
using Forms = System.Windows.Forms;

namespace Volumix.Services;

/// <summary>Значок в области уведомлений. Само меню — WPF, чтобы оно было в стиле Windows 11.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MixerViewModel _mixer;
    private readonly SettingsViewModel _settings;
    private readonly App _app;
    private readonly SpotifyService _spotify;
    private readonly MediaTrayIcons _media;

    public TrayService(App app, MixerViewModel mixer, SettingsViewModel settings)
    {
        _app = app;
        _mixer = mixer;
        _settings = settings;

        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Volumix",
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _app.ToggleFlyout();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };

        _mixer.Master.PropertyChanged += OnMasterChanged;
        UpdateTooltip();

        // Значки плеера создаются сразу после основного, чтобы их номера в трее не менялись от запуска к запуску
        _spotify = new SpotifyService(app.Dispatcher);
        _media = new MediaTrayIcons(_spotify, app.Dispatcher, ShowMenu) { Enabled = _settings.MediaButtons };
        _settings.PropertyChanged += OnSettingsChanged;
        _spotify.Start();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.MediaButtons))
            _media.Enabled = _settings.MediaButtons;
    }

    private void OnMasterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChannelViewModel.Volume) or nameof(ChannelViewModel.IsMuted) or nameof(MasterChannelViewModel.DeviceName))
            UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        var m = _mixer.Master;
        var text = _mixer.HasDevice
            ? $"Volumix — {(m.IsMuted ? "без звука" : m.Volume + "%")}\n{m.Name}"
            : "Volumix — нет устройства вывода";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowMenu()
    {
        _mixer.Refresh();
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        menu.Items.Add(Item("Открыть микшер", () => _app.ShowMain(AppPage.Mixer)));

        var devices = new MenuItem { Header = "Устройство вывода" };
        foreach (var device in _mixer.Devices)
        {
            var d = device;
            devices.Items.Add(new MenuItem
            {
                Header = d.Name,
                IsCheckable = true,
                IsChecked = d == _mixer.SelectedDevice,
            }.Also(mi => mi.Click += (_, _) => _mixer.SelectedDevice = d));
        }
        devices.IsEnabled = _mixer.Devices.Count > 0;
        menu.Items.Add(devices);

        if (SettingsService.Current.VoiceEnabled)
            menu.Items.Add(Item("Голосовая команда", _app.ToggleVoice));
        menu.Items.Add(new MenuItem
        {
            Header = "Кнопки Spotify в трее",
            IsCheckable = true,
            IsChecked = _settings.MediaButtons,
        }.Also(mi => mi.Click += (_, _) => _settings.MediaButtons = mi.IsChecked));
        menu.Items.Add(Item("Горячие клавиши", () => _app.ShowMain(AppPage.Hotkeys)));
        menu.Items.Add(Item("Настройки", () => _app.ShowMain(AppPage.Settings)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Выход", _app.ExitApp));

        // Без активации меню не закрывается по щелчку мимо
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource src)
                NativeMethods.SetForegroundWindow(src.Handle);
        };
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action) =>
        new MenuItem { Header = header }.Also(mi => mi.Click += (_, _) => action());

    public void Dispose()
    {
        _mixer.Master.PropertyChanged -= OnMasterChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
        _media.Dispose();
        _spotify.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
    }
}

internal static class ObjectExtensions
{
    public static T Also<T>(this T obj, Action<T> action)
    {
        action(obj);
        return obj;
    }
}
