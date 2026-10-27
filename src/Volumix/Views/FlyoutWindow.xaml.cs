using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Volumix.Services;
using Wpf.Ui.Controls;

namespace Volumix.Views;

/// <summary>Компактный микшер, который открывается по щелчку на значке в трее.</summary>
public partial class FlyoutWindow : FluentWindow
{
    private long _hiddenAt;

    public FlyoutWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ThemeService.UpdateWindow(this);
        Deactivated += (_, _) => HideFlyout();
        SizeChanged += (_, _) => PlaceNearTray();
    }

    /// <summary>Щелчок по значку трея сначала уводит фокус (окно прячется), а потом приходит сам клик — не открываем заново.</summary>
    public bool JustHidden => Environment.TickCount64 - _hiddenAt < 350;

    public void ShowFlyout()
    {
        Show();
        PlaceNearTray();
        Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    public void HideFlyout()
    {
        if (!IsVisible)
            return;
        _hiddenAt = Environment.TickCount64;
        Hide();
        App.TrimMemory();
    }

    private void PlaceNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    private void Hotkeys_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        ((App)Application.Current).ShowMain(AppPage.Hotkeys);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        ((App)Application.Current).ShowMain(AppPage.Settings);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 просто прячет окно
        if (!App.IsExiting)
        {
            e.Cancel = true;
            HideFlyout();
        }
        base.OnClosing(e);
    }
}
