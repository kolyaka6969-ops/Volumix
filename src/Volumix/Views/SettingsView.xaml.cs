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
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(SettingsService.Folder);

    private void OpenLyricsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(LyricsService.Folder);

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
