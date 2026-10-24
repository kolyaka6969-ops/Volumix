using Volumix.Services;

namespace Volumix.Tests;

public class VoiceCommandParserTests
{
    private static readonly VoiceApp[] Apps =
    {
        new(VoiceCommandParser.SystemSoundsKey, "Системные звуки"),
        new("Spotify", "Spotify"),
        new("chrome", "Google Chrome"),
        new("Discord", "Discord"),
        new("Telegram", "Telegram Desktop"),
        new("steam", "Steam"),
        new("javaw", "OpenJDK Platform binary"),
    };

    private static readonly VoiceDevice[] Devices =
    {
        new("dev-headphones", "Наушники (Realtek(R) Audio)"),
        new("dev-speakers", "Динамики (Realtek(R) Audio)"),
        new("dev-monitor", "LG ULTRAGEAR (NVIDIA High Definition Audio)"),
    };

    private static VoiceCommand Parse(string text) => VoiceCommandParser.Parse(text, Apps, Devices);

    [Theory]
    [InlineData("громкость спотифай пятьдесят", "Spotify", 50)]
    [InlineData("поставь хром на тридцать процентов", "chrome", 30)]
    [InlineData("дискорд двадцать пять", "Discord", 25)]
    [InlineData("громкость телеграма сорок", "Telegram", 40)]
    [InlineData("сделай громкость стима на сто", "steam", 100)]
    [InlineData("майнкрафт на половину", "javaw", 50)]
    [InlineData("увеличь громкость спотифая до шестидесяти", "Spotify", 60)]
    [InlineData("спот и фай семьдесят", "Spotify", 70)]
    [InlineData("системные звуки десять", VoiceCommandParser.SystemSoundsKey, 10)]
    public void SetsAppVolume(string text, string app, int value)
    {
        var cmd = Parse(text);
        Assert.Equal(VoiceIntent.SetVolume, cmd.Intent);
        Assert.Equal(HotkeyTarget.App, cmd.Target);
        Assert.Equal(app, cmd.App);
        Assert.Equal(value, cmd.Value);
    }

    [Theory]
    [InlineData("громкость сорок", 40)]
    [InlineData("общая громкость восемьдесят пять", 85)]
    [InlineData("громкость на максимум", 100)]
    [InlineData("поставь громкость 15", 15)]
    public void SetsMasterVolume(string text, int value)
    {
        var cmd = Parse(text);
        Assert.Equal(VoiceIntent.SetVolume, cmd.Intent);
        Assert.Equal(HotkeyTarget.Master, cmd.Target);
        Assert.Equal(value, cmd.Value);
    }

    [Theory]
    [InlineData("громче", HotkeyTarget.Master, null, 10)]
    [InlineData("тише", HotkeyTarget.Master, null, -10)]
    [InlineData("сделай спотифай погромче", HotkeyTarget.App, "Spotify", 10)]
    [InlineData("дискорд потише на двадцать", HotkeyTarget.App, "Discord", -20)]
    [InlineData("чуть тише", HotkeyTarget.Master, null, -5)]
    [InlineData("сделай это окно намного громче", HotkeyTarget.Foreground, null, 20)]
    [InlineData("убавь хром на пять процентов", HotkeyTarget.App, "chrome", -5)]
    public void ChangesVolume(string text, HotkeyTarget target, string? app, int delta)
    {
        var cmd = Parse(text);
        Assert.Equal(VoiceIntent.ChangeVolume, cmd.Intent);
        Assert.Equal(target, cmd.Target);
        Assert.Equal(app, cmd.App);
        Assert.Equal(delta, cmd.Value);
    }

    [Theory]
    [InlineData("выключи звук", VoiceIntent.Mute, HotkeyTarget.Master, null)]
    [InlineData("выключи звук в дискорде", VoiceIntent.Mute, HotkeyTarget.App, "Discord")]
    [InlineData("заглуши хром", VoiceIntent.Mute, HotkeyTarget.App, "chrome")]
    [InlineData("включи звук спотифая", VoiceIntent.Unmute, HotkeyTarget.App, "Spotify")]
    [InlineData("без звука", VoiceIntent.Mute, HotkeyTarget.Master, null)]
    [InlineData("отключи телеграм", VoiceIntent.Mute, HotkeyTarget.App, "Telegram")]
    public void MutesAndUnmutes(string text, VoiceIntent intent, HotkeyTarget target, string? app)
    {
        var cmd = Parse(text);
        Assert.Equal(intent, cmd.Intent);
        Assert.Equal(target, cmd.Target);
        Assert.Equal(app, cmd.App);
    }

    [Theory]
    [InlineData("тёмная тема", ThemeMode.Dark)]
    [InlineData("включи светлую тему", ThemeMode.Light)]
    [InlineData("тема как в системе", ThemeMode.System)]
    [InlineData("смени тему", null)]
    public void SetsTheme(string text, ThemeMode? theme)
    {
        var cmd = Parse(text);
        Assert.Equal(VoiceIntent.SetTheme, cmd.Intent);
        Assert.Equal(theme, cmd.Theme);
    }

    [Theory]
    [InlineData("включи автозапуск", VoiceIntent.SetAutostart, true)]
    [InlineData("отключи автозапуск", VoiceIntent.SetAutostart, false)]
    [InlineData("не запускать вместе с windows", VoiceIntent.SetAutostart, false)]
    [InlineData("выключи индикатор громкости", VoiceIntent.SetOsd, false)]
    [InlineData("показывать индикатор", VoiceIntent.SetOsd, true)]
    [InlineData("сворачивать в трей", VoiceIntent.SetCloseToTray, null)]
    public void TogglesSettings(string text, VoiceIntent intent, bool? flag)
    {
        var cmd = Parse(text);
        Assert.Equal(intent, cmd.Intent);
        Assert.Equal(flag, cmd.Flag);
    }

    [Fact]
    public void SetsStep()
    {
        var cmd = Parse("шаг громкости пять");
        Assert.Equal(VoiceIntent.SetStep, cmd.Intent);
        Assert.Equal(5, cmd.Value);
    }

    [Theory]
    [InlineData("переключи на наушники", "dev-headphones")]
    [InlineData("включи колонки", "dev-speakers")]
    [InlineData("устройство вывода монитор", "dev-monitor")]
    [InlineData("наушники", "dev-headphones")]
    public void SwitchesDevice(string text, string id)
    {
        var cmd = Parse(text);
        Assert.Equal(VoiceIntent.SwitchDevice, cmd.Intent);
        Assert.Equal(id, cmd.DeviceId);
    }

    [Theory]
    [InlineData("следующее устройство", VoiceIntent.NextDevice)]
    [InlineData("открой микшер", VoiceIntent.OpenMixer)]
    [InlineData("открой настройки", VoiceIntent.OpenSettings)]
    [InlineData("покажи горячие клавиши", VoiceIntent.OpenHotkeys)]
    [InlineData("закрой", VoiceIntent.HideWindows)]
    [InlineData("какая сегодня погода", VoiceIntent.Unknown)]
    [InlineData("да", VoiceIntent.Unknown)]
    [InlineData("включи", VoiceIntent.Unknown)]
    [InlineData("выключи всё", VoiceIntent.Mute)]
    [InlineData("отключи это окно", VoiceIntent.Mute)]
    [InlineData("", VoiceIntent.Unknown)]
    public void RecognizesOtherIntents(string text, VoiceIntent intent) =>
        Assert.Equal(intent, Parse(text).Intent);

    [Fact]
    public void ReportsUnknownApp()
    {
        var cmd = Parse("громкость фотошопа тридцать");
        Assert.Equal(VoiceIntent.SetVolume, cmd.Intent);
        Assert.Null(cmd.App);
        Assert.Equal("фотошопа", cmd.Unmatched);
    }
}
