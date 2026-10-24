using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Volumix.Services;

public static class ThemeService
{
    private static bool _subscribed;

    public static ApplicationTheme Effective { get; private set; } = ApplicationTheme.Dark;

    public static void Apply(ThemeMode mode)
    {
        Effective = mode switch
        {
            ThemeMode.Light => ApplicationTheme.Light,
            ThemeMode.Dark => ApplicationTheme.Dark,
            _ => IsSystemLight() ? ApplicationTheme.Light : ApplicationTheme.Dark,
        };

        ApplicationThemeManager.Apply(Effective, WindowBackdropType.Mica, true);

        foreach (Window window in Application.Current.Windows)
            UpdateWindow(window);

        if (!_subscribed)
        {
            _subscribed = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
                    && SettingsService.Current.Theme == ThemeMode.System)
                    Application.Current.Dispatcher.BeginInvoke(() => Apply(ThemeMode.System));
            };
        }
    }

    public static void UpdateWindow(Window window)
    {
        if (window is FluentWindow fw)
            WindowBackgroundManager.UpdateBackground(fw, Effective, fw.WindowBackdropType);
    }

    private static bool IsSystemLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
    }
}
