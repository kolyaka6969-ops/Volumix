using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Volumix.Services;
using Volumix.ViewModels;

namespace Volumix.Views;

public partial class HotkeysView : UserControl
{
    public HotkeysView()
    {
        InitializeComponent();
        DataContext = App.SettingsVm;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not HotkeyBindingViewModel vm)
            return;

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };
        var mods = CurrentModifiers();

        if (key == Key.Tab && mods == ModifierKeys.None)
            return; // обычная навигация по форме

        e.Handled = true;

        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin:
                return; // ждём основную клавишу
            case Key.Escape when mods == ModifierKeys.None:
                Keyboard.ClearFocus();
                return;
            case Key.Back or Key.Delete when mods == ModifierKeys.None:
                vm.SetGesture(ModifierKeys.None, Key.None);
                return;
        }

        // Одиночная буква или цифра перехватывалась бы во всех программах и мешала печатать
        if (mods == ModifierKeys.None && !HotkeyService.IsStandaloneKey(key))
            return;

        vm.SetGesture(mods, key);
    }

    /// <summary>
    /// Keyboard.Modifiers не видит Win, а Alt в сочетании с Win иногда теряется, поэтому опрашиваем клавиши напрямую.
    /// </summary>
    private static ModifierKeys CurrentModifiers()
    {
        var mods = ModifierKeys.None;
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) mods |= ModifierKeys.Control;
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) mods |= ModifierKeys.Alt;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) mods |= ModifierKeys.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= ModifierKeys.Windows;
        return mods | Keyboard.Modifiers;
    }

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is HotkeyBindingViewModel vm)
            vm.BeginCapture();
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is HotkeyBindingViewModel vm)
            vm.EndCapture();
    }

    private void AppCombo_DropDownOpened(object? sender, System.EventArgs e) =>
        App.SettingsVm.NotifyKnownAppsChanged();
}
