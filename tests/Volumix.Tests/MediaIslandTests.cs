using Volumix.Services;
using Volumix.Views;

namespace Volumix.Tests;

public class MediaIslandTests
{
    [Fact]
    public void ExcludesBrowsersFromGames()
    {
        Assert.Contains("chrome", GameDetector.ExcludedExes);
        Assert.Contains("msedge", GameDetector.ExcludedExes);
        Assert.Contains("firefox", GameDetector.ExcludedExes);
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Terraria\Terraria.exe", true)]
    [InlineData(@"D:\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", true)]
    [InlineData(@"E:\Stuff\Palworld\Pal\Binaries\Win64\Palworld-Win64-Shipping.exe", true)]
    [InlineData(@"C:\XboxGames\Forza Horizon 5\Content\ForzaHorizon5.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", false)]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", false)]
    public void RecognizesGamesByPath(string path, bool game) =>
        Assert.Equal(game, GameDetector.IsGamePath(path));

    [Theory]
    [InlineData("lwjgl64.dll", true)]   // Minecraft 1.7.10
    [InlineData("lwjgl.dll", true)]     // Minecraft новых версий
    [InlineData("UnityPlayer.dll", true)]
    [InlineData("steam_api64.dll", true)]
    [InlineData("d3d11.dll", false)]    // есть и у браузеров
    [InlineData("chrome.dll", false)]
    public void RecognizesGameModules(string module, bool game) =>
        Assert.Equal(game, GameDetector.IsGameModule(module));

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("chrome", "chrome")]
    [InlineData("MSEdge", "MSEdge")]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", "vlc")]
    [InlineData("Microsoft.ZuneVideo_8wekyb3d8bbwe!Microsoft.ZuneVideo", null)]
    [InlineData("", null)]
    public void FindsPlayerProcess(string aumid, string? key) =>
        Assert.Equal(key, MediaIslandService.ProcessKey(aumid));

    [Theory]
    [InlineData(187, null, "3:07")]
    [InlineData(5, null, "0:05")]
    [InlineData(3765, null, "1:02:45")]
    [InlineData(65, 5400, "0:01:05")]  // позиция фильма пишется как длительность: ширина не прыгает
    [InlineData(-3, null, "0:00")]
    public void FormatsTime(int seconds, int? duration, string text) =>
        Assert.Equal(text, MediaIslandWindow.FormatTime(TimeSpan.FromSeconds(seconds),
            duration is { } d ? TimeSpan.FromSeconds(d) : null));

    [Theory]
    [InlineData(5, 1000, 0)]       // почти у левого края — прилипает
    [InlineData(-40, 1000, 0)]     // за краем — у края
    [InlineData(990, 1000, 1000)]  // почти у правого
    [InlineData(492, 1000, 500)]   // почти посередине
    [InlineData(300, 1000, 300)]   // поодаль — где отпустили
    public void SnapsToEdgesAndCenter(double offset, double free, double snapped) =>
        Assert.Equal(snapped, MediaIslandWindow.Snap(offset, free));
}
