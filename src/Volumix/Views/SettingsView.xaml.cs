using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Volumix.Services;

namespace Volumix.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContext = App.SettingsVm;
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                App.SettingsVm.RefreshVoiceStatus(); // микрофон могли подключить, пока окно было скрыто
        };
    }

    private void TryVoice_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).ToggleVoice();

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(SettingsService.Folder);

    private void OpenLyricsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(LyricsService.Folder);

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
