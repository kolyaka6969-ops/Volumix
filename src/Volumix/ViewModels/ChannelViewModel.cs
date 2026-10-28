using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.CoreAudioApi;

namespace Volumix.ViewModels;

/// <summary>Общая часть для общей громкости устройства и громкости отдельного приложения.</summary>
public abstract class ChannelViewModel : ObservableObject
{
    private double _volume;
    private bool _isMuted;
    private double _peak;

    protected ChannelViewModel()
    {
        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
    }

    public abstract string Name { get; }
    public virtual ImageSource? Icon => null;
    public bool HasIcon => Icon != null;
    public virtual string FallbackGlyph => "";
    public ICommand ToggleMuteCommand { get; }

    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, 100);
            if (!Set(ref _volume, value))
                return;
            Try(() => ApplyVolume((float)(value / 100)));
            OnPropertyChanged(nameof(Glyph));
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (!Set(ref _isMuted, value))
                return;
            Try(() => ApplyMute(value));
            OnPropertyChanged(nameof(Glyph));
        }
    }

    /// <summary>Уровень сигнала 0..1 для индикатора.</summary>
    public double Peak
    {
        get => _peak;
        set => Set(ref _peak, value);
    }

    public string Glyph =>
        _isMuted ? "" :
        _volume <= 0 ? "" :
        _volume < 34 ? "" :
        _volume < 67 ? "" : "";

    /// <summary>Обновляет значения из системы, не записывая их обратно.</summary>
    internal void Sync(double volume01, bool muted)
    {
        var volume = Math.Clamp(Math.Round(volume01 * 100), 0, 100);
        bool changed = false;
        if (_volume != volume) { _volume = volume; OnPropertyChanged(nameof(Volume)); changed = true; }
        if (_isMuted != muted) { _isMuted = muted; OnPropertyChanged(nameof(IsMuted)); changed = true; }
        if (changed) OnPropertyChanged(nameof(Glyph));
    }

    internal void UpdatePeak(double raw)
    {
        // Плавное затухание, как у системного индикатора
        var next = Math.Max(raw, _peak * 0.82);
        Peak = next < 0.005 ? 0 : next;
    }

    protected abstract void ApplyVolume(float value);
    protected abstract void ApplyMute(bool muted);

    private static void Try(Action action)
    {
        try { action(); }
        catch (COMException) { }
        catch (InvalidCastException) { }
    }
}

public sealed class MasterChannelViewModel : ChannelViewModel
{
    private readonly Func<MMDevice?> _device;
    private string _deviceName = "Нет устройства вывода";

    public MasterChannelViewModel(Func<MMDevice?> device) => _device = device;

    public override string Name => "Общая громкость";
    public override string FallbackGlyph => "";
    public string DeviceName => _deviceName;

    internal void SetDeviceName(string name)
    {
        _deviceName = name;
        OnPropertyChanged(nameof(DeviceName));
    }

    protected override void ApplyVolume(float value)
    {
        if (_device() is { } d) d.AudioEndpointVolume.MasterVolumeLevelScalar = value;
    }

    protected override void ApplyMute(bool muted)
    {
        if (_device() is { } d) d.AudioEndpointVolume.Mute = muted;
    }
}

public sealed class AppSessionViewModel : ChannelViewModel, IDisposable
{
    private List<AudioSessionControl> _sessions = new();

    public AppSessionViewModel(string key, string name, ImageSource? icon, bool isSystem)
    {
        Key = key;
        _name = name;
        _icon = icon;
        IsSystem = isSystem;
    }

    private readonly string _name;
    private readonly ImageSource? _icon;

    /// <summary>Имя exe без расширения, по нему же работают горячие клавиши.</summary>
    public string Key { get; }
    public bool IsSystem { get; }
    public override string Name => _name;
    public override ImageSource? Icon => _icon;
    public override string FallbackGlyph => IsSystem ? "" : "";

    internal void Update(List<AudioSessionControl> sessions)
    {
        foreach (var old in _sessions)
        {
            if (!sessions.Contains(old))
            {
                try { old.Dispose(); } catch { }
            }
        }
        _sessions = sessions;
        var first = sessions[0].SimpleAudioVolume;
        bool allMuted = true;
        foreach (var s in sessions)
            allMuted &= s.SimpleAudioVolume.Mute;
        Sync(first.Volume, allMuted);
    }

    public void Dispose()
    {
        foreach (var s in _sessions)
        {
            try { s.Dispose(); } catch { }
        }
        _sessions.Clear();
    }

    internal double ReadPeak()
    {
        double max = 0;
        foreach (var s in _sessions)
        {
            try { max = Math.Max(max, s.AudioMeterInformation.MasterPeakValue); }
            catch (COMException) { }
        }
        return max;
    }

    protected override void ApplyVolume(float value)
    {
        foreach (var s in _sessions)
            s.SimpleAudioVolume.Volume = value;
    }

    protected override void ApplyMute(bool muted)
    {
        foreach (var s in _sessions)
            s.SimpleAudioVolume.Mute = muted;
    }
}
