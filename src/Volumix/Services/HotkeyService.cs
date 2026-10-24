using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace Volumix.Services;

/// <summary>Глобальные горячие клавиши через RegisterHotKey на скрытом message-only окне.</summary>
public sealed class HotkeyService : IDisposable
{
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;

    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyBinding> _registered = new();
    private IReadOnlyList<HotkeyBinding> _bindings = Array.Empty<HotkeyBinding>();
    private bool _suspended;

    /// <summary>Привязки, которые не удалось зарегистрировать (сочетание занято).</summary>
    public HashSet<HotkeyBinding> Failed { get; } = new();

    public event Action<HotkeyBinding>? Pressed;
    public event Action? RegistrationChanged;

    public HotkeyService()
    {
        _source = new HwndSource(new HwndSourceParameters("VolumixHotkeys")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);
    }

    public void Apply(IReadOnlyList<HotkeyBinding> bindings)
    {
        _bindings = bindings;
        if (!_suspended)
            RegisterAll();
    }

    /// <summary>Временно снимает хоткеи, чтобы их можно было переназначить в окне настроек.</summary>
    public void Suspend()
    {
        _suspended = true;
        UnregisterAll();
    }

    public void Resume()
    {
        _suspended = false;
        RegisterAll();
    }

    private void RegisterAll()
    {
        UnregisterAll();
        Failed.Clear();
        int id = 1;
        foreach (var b in _bindings)
        {
            if (b.Key == Key.None || (b.Modifiers == ModifierKeys.None && !IsStandaloneKey(b.Key)))
                continue; // одиночная буква сломала бы набор текста во всех программах
            uint mods = 0;
            if (b.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
            if (b.Modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
            if (b.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
            if (b.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(b.Key);

            if (NativeMethods.RegisterHotKey(_source.Handle, id, mods, vk))
                _registered[id++] = b;
            else
                Failed.Add(b);
        }
        RegistrationChanged?.Invoke();
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
            NativeMethods.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var binding))
        {
            handled = true;
            Pressed?.Invoke(binding);
        }
        return IntPtr.Zero;
    }

    /// <summary>Клавиши, которые можно назначить без модификаторов: F1–F24, мультимедийные и т. п.</summary>
    public static bool IsStandaloneKey(Key key) =>
        key is >= Key.F1 and <= Key.F24
            or Key.Pause or Key.Scroll or Key.Insert
            or Key.VolumeUp or Key.VolumeDown or Key.VolumeMute
            or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaPlayPause or Key.MediaStop
            or Key.LaunchApplication1 or Key.LaunchApplication2;

    public static string Format(ModifierKeys mods, Key key)
    {
        if (key == Key.None)
            return "";
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(KeyName(key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Enter => "Enter",
        Key.CapsLock => "CapsLock",
        Key.Escape => "Esc",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Left => "←",
        Key.Right => "→",
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Multiply => "Num *",
        Key.Divide => "Num /",
        Key.VolumeUp => "Громкость +",
        Key.VolumeDown => "Громкость −",
        Key.VolumeMute => "Без звука",
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(key - Key.NumPad0),
        _ => key.ToString(),
    };

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
