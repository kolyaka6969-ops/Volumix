using System;
using System.ComponentModel;
using System.Windows;
using Volumix.ViewModels;
using Volumix.Views;
using Forms = System.Windows.Forms;

namespace Volumix.Services;

/// <summary>Значок в области уведомлений со всплывающим окном в стиле Orbit.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MixerViewModel _mixer;
    private readonly SettingsViewModel _settings;
    private readonly App _app;
    private TrayMenuWindow? _menuWindow;
    private long _lastMenuOpenedAt;

    public bool IsMenuVisible => _menuWindow?.IsVisible == true;

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

        // Обрабатываем как MouseUp, так и MouseClick, гарантируя открытие по ПКМ в любой версии Windows
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
                _app.Dispatcher.BeginInvoke(ToggleMenu);
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                _app.ToggleFlyout();
            else if (e.Button == Forms.MouseButtons.Right)
                _app.Dispatcher.BeginInvoke(ToggleMenu);
        };

        _mixer.Master.PropertyChanged += OnMasterChanged;
        UpdateTooltip();
    }

    public void ToggleMenu()
    {
        if (Environment.TickCount64 - _lastMenuOpenedAt < 250)
            return;
        _lastMenuOpenedAt = Environment.TickCount64;

        _menuWindow ??= new TrayMenuWindow(_app, _settings);
        if (_menuWindow.IsVisible)
            _menuWindow.HideMenu();
        else
            _menuWindow.ShowMenu();
    }

    public void HideMenu() => _menuWindow?.HideMenu();

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

    public void Dispose()
    {
        _mixer.Master.PropertyChanged -= OnMasterChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _menuWindow?.Close();
    }
}
