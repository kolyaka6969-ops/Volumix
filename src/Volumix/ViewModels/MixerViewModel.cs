using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Volumix.Services;

namespace Volumix.ViewModels;

public sealed record DeviceItem(string Id, string Name)
{
    public string Glyph =>
        Name.Contains("науш", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("head", StringComparison.OrdinalIgnoreCase) ? "" : "";
}

public sealed class MixerViewModel : ObservableObject, IMMNotificationClient, IDisposable
{
    private const string SystemKey = "#system";

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<uint> _knownPids = new();
    private MMDevice? _device;
    private DeviceItem? _selectedDevice;
    private bool _devicesDirty = true;
    private bool _refreshQueued;
    private bool _suppressSelection;
    private int _visibleViews;
    private int _tick;

    public MixerViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Master = new MasterChannelViewModel(() => _device);
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;
        _enumerator.RegisterEndpointNotificationCallback(this);
        Refresh();
    }

    public MasterChannelViewModel Master { get; }
    public ObservableCollection<DeviceItem> Devices { get; } = new();
    public ObservableCollection<AppSessionViewModel> Apps { get; } = new();
    public bool HasDevice => _device != null;

    /// <summary>Выбор в списке делает устройство устройством вывода по умолчанию в Windows.</summary>
    public DeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value) || _suppressSelection || value == null || value.Id == _device?.ID)
                return;
            try { DefaultDevice.Set(value.Id); }
            catch (Exception) { }
            _devicesDirty = true;
            Refresh();
        }
    }

    public void NextDevice()
    {
        if (Devices.Count < 2)
            return;
        int i = SelectedDevice == null ? -1 : Devices.IndexOf(SelectedDevice);
        SelectedDevice = Devices[(i + 1) % Devices.Count];
    }

    public AppSessionViewModel? FindApp(string? key) =>
        string.IsNullOrEmpty(key) ? null : Apps.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Видимые окна микшера включают частый опрос (индикаторы уровня).</summary>
    public void SetViewVisible(bool visible)
    {
        _visibleViews = Math.Max(0, _visibleViews + (visible ? 1 : -1));
        if (_visibleViews > 0)
        {
            Refresh();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public void Refresh()
    {
        try
        {
            RefreshCore();
        }
        catch (COMException)
        {
            // Устройство пропало между вызовами — пересоберём при следующем обновлении.
            _devicesDirty = true;
        }
    }

    private void RefreshCore()
    {
        if (_devicesDirty)
        {
            _devicesDirty = false;
            UpdateDevice();
            UpdateDeviceList();
        }

        if (_device == null)
        {
            Apps.Clear();
            return;
        }

        var endpoint = _device.AudioEndpointVolume;
        Master.Sync(endpoint.MasterVolumeLevelScalar, endpoint.Mute);

        var manager = _device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;

        var groups = new Dictionary<string, (ProcessInfo? Info, List<AudioSessionControl> List)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var seen = new HashSet<uint>();

        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (s.State == AudioSessionState.AudioSessionStateExpired)
                continue;

            string key;
            ProcessInfo? info = null;
            if (s.IsSystemSoundsSession)
            {
                key = SystemKey;
            }
            else
            {
                uint pid = s.GetProcessID;
                if (pid == 0)
                    continue;
                seen.Add(pid);
                info = ProcessInfo.Get(pid);
                if (info == null)
                    continue;
                key = info.Key;
            }

            if (!groups.TryGetValue(key, out var group))
            {
                group = (info, new List<AudioSessionControl>());
                groups[key] = group;
                order.Add(key);
            }
            group.List.Add(s);
        }

        foreach (var pid in _knownPids.Except(seen).ToList())
            ProcessInfo.Forget(pid);
        _knownPids.Clear();
        _knownPids.UnionWith(seen);

        for (int i = Apps.Count - 1; i >= 0; i--)
            if (!groups.ContainsKey(Apps[i].Key))
                Apps.RemoveAt(i);

        foreach (var key in order)
        {
            var (info, list) = groups[key];
            var app = FindApp(key);
            if (app == null)
            {
                app = key == SystemKey
                    ? new AppSessionViewModel(SystemKey, "Системные звуки", null, true)
                    : new AppSessionViewModel(key, info!.DisplayName, info.Icon, false);
                if (app.IsSystem) Apps.Insert(0, app);
                else Apps.Add(app);
            }
            app.Update(list);
        }
    }

    private void UpdateDevice()
    {
        MMDevice? current = null;
        try
        {
            if (_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (COMException) { }

        if (_device != null && current?.ID == _device.ID)
        {
            current?.Dispose();
            return;
        }

        if (_device != null)
        {
            try { _device.AudioEndpointVolume.OnVolumeNotification -= OnMasterNotification; } catch (COMException) { }
            _device.Dispose();
        }

        Apps.Clear();
        _device = current;
        if (_device != null)
        {
            _device.AudioEndpointVolume.OnVolumeNotification += OnMasterNotification;
            _device.AudioSessionManager.OnSessionCreated += (_, _) => QueueRefresh();
            Master.SetDeviceName(_device.FriendlyName);
        }
        else
        {
            Master.SetDeviceName("Нет устройства вывода");
        }
        OnPropertyChanged(nameof(HasDevice));
    }

    private void UpdateDeviceList()
    {
        var items = new List<DeviceItem>();
        foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try { items.Add(new DeviceItem(d.ID, d.FriendlyName)); }
            catch (COMException) { }
            finally { d.Dispose(); }
        }

        _suppressSelection = true;
        try
        {
            if (!items.SequenceEqual(Devices))
            {
                Devices.Clear();
                foreach (var item in items)
                    Devices.Add(item);
            }
            SelectedDevice = Devices.FirstOrDefault(d => d.Id == _device?.ID);
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (++_tick % 20 == 0)
            Refresh();

        if (_device == null)
            return;
        try
        {
            Master.UpdatePeak(_device.AudioMeterInformation.MasterPeakValue);
            foreach (var app in Apps)
                app.UpdatePeak(app.ReadPeak());
        }
        catch (COMException)
        {
            _devicesDirty = true;
        }
    }

    private void OnMasterNotification(AudioVolumeNotificationData data) =>
        _dispatcher.BeginInvoke(() => Master.Sync(data.MasterVolume, data.Muted));

    private void QueueRefresh(bool devices = false)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _devicesDirty |= devices;
            if (_refreshQueued)
                return;
            _refreshQueued = true;
            // Небольшая задержка: Windows шлёт пачки уведомлений подряд
            var t = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                _refreshQueued = false;
                Refresh();
            };
            t.Start();
        });
    }

    // IMMNotificationClient — вызывается из потоков Windows, поэтому только ставим обновление в очередь.
    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => QueueRefresh(true);
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => QueueRefresh(true);
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => QueueRefresh(true);
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia)
            QueueRefresh(true);
    }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _timer.Stop();
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch (COMException) { }
        _device?.Dispose();
        _enumerator.Dispose();
    }
}
