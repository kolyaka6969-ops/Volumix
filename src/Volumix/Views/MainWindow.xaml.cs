using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Volumix.Services;
using Wpf.Ui.Controls;

namespace Volumix.Views;

public enum AppPage { Mixer, Hotkeys, Settings }

public partial class MainWindow : FluentWindow
{
    private static readonly string[] Titles = { "Микшер громкости", "Горячие клавиши", "Настройки" };
    private readonly UserControl?[] _pages = new UserControl?[3];

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ThemeService.UpdateWindow(this);
        Nav.SelectedIndex = 0;
    }

    public void ShowPage(AppPage page) => Nav.SelectedIndex = (int)page;

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = Nav.SelectedIndex;
        if (i < 0)
            return;
        _pages[i] ??= (AppPage)i switch
        {
            AppPage.Mixer => new MixerView(),
            AppPage.Hotkeys => new HotkeysView(),
            _ => new SettingsView(),
        };
        PageTitle.Text = Titles[i];
        PageHost.Content = _pages[i];
        PageScroll.ScrollToTop();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting && SettingsService.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!App.IsExiting)
            ((App)Application.Current).ExitApp();
    }
}
