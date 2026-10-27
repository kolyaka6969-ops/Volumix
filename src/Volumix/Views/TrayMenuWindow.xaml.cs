using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Volumix.Services;
using Volumix.ViewModels;
using Forms = System.Windows.Forms;

namespace Volumix.Views;

public partial class TrayMenuWindow : Window
{
    private readonly App _app;
    private readonly SettingsViewModel _settings;

    public TrayMenuWindow(App app, SettingsViewModel settings)
    {
        InitializeComponent();
        _app = app;
        _settings = settings;

        Deactivated += (_, _) => HideMenu();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                HideMenu();
        };
    }

    public void ShowMenu()
    {
        UpdateToggles();
        Show();
        PlaceNearCursor();
        Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    public void HideMenu()
    {
        if (!IsVisible)
            return;
        Hide();
        App.TrimMemory();
    }

    private void UpdateToggles()
    {
        LyricsCheck.Visibility = _settings.DesktopLyrics ? Visibility.Visible : Visibility.Collapsed;
        IslandCheck.Visibility = _settings.MediaIsland ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlaceNearCursor()
    {
        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        double height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
        if (width <= 0) width = 270;
        if (height <= 0) height = 350;

        NativeMethods.GetCursorPos(out var pt);

        var source = PresentationSource.FromVisual(this);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        if (dpiX <= 0) dpiX = 1.0;
        if (dpiY <= 0) dpiY = 1.0;

        double cursorX = pt.X / dpiX;
        double cursorY = pt.Y / dpiY;

        var formsPt = new System.Drawing.Point(pt.X, pt.Y);
        var screen = Forms.Screen.FromPoint(formsPt);
        var work = screen.WorkingArea;

        double workLeft = work.Left / dpiX;
        double workTop = work.Top / dpiY;
        double workRight = work.Right / dpiX;
        double workBottom = work.Bottom / dpiY;

        // Центрируем по горизонтали относительно курсора/значка трея
        double x = cursorX - (width / 2);
        if (x + width > workRight - 8)
            x = workRight - width - 8;
        if (x < workLeft + 8)
            x = workLeft + 8;

        // Размещаем над панелью задач / курсором
        double y = cursorY - height - 8;
        if (y + height > workBottom - 8)
            y = workBottom - height - 8;
        if (y < workTop + 8)
            y = workTop + 8;

        Left = x;
        Top = y;
    }

    private void Mixer_Click(object sender, RoutedEventArgs e)
    {
        HideMenu();
        _app.ShowMain(AppPage.Mixer);
    }

    private void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        _settings.DesktopLyrics = !_settings.DesktopLyrics;
        UpdateToggles();
        HideMenu();
    }

    private void Island_Click(object sender, RoutedEventArgs e)
    {
        _settings.MediaIsland = !_settings.MediaIsland;
        UpdateToggles();
        HideMenu();
    }

    private void Hotkeys_Click(object sender, RoutedEventArgs e)
    {
        HideMenu();
        _app.ShowMain(AppPage.Hotkeys);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        HideMenu();
        _app.ShowMain(AppPage.Settings);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        HideMenu();
        _app.ExitApp();
    }
}
