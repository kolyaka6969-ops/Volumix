using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using Volumix.Services;
using Xunit.Abstractions;

namespace Volumix.Tests;

/// <summary>Одна загрузка модели на все сквозные тесты.</summary>
public sealed class ModelFixture : IDisposable
{
    internal VoskNative.Model? Model { get; }

    public ModelFixture()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "models", VoiceService.ModelName);
            if (Directory.Exists(path))
            {
                VoskNative.vosk_set_log_level(-1);
                Model = new VoskNative.Model(path);
                return;
            }
        }
    }

    public void Dispose() => Model?.Dispose();
}

/// <summary>
/// Сквозная проверка: фраза озвучивается русским голосом Windows, распознаётся Vosk (как в программе — двумя
/// распознавателями) и разбирается парсером. Пропускается, если нет модели или голоса Microsoft Irina.
/// </summary>
public class SpeechEndToEndTests : IClassFixture<ModelFixture>
{
    private readonly ITestOutputHelper _output;
    private readonly ModelFixture _fixture;

    public SpeechEndToEndTests(ITestOutputHelper output, ModelFixture fixture)
    {
        _output = output;
        _fixture = fixture;
    }

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
    };

    public static TheoryData<string, VoiceIntent, string?, int?> Phrases => new()
    {
        { "Громкость спотифай пятьдесят", VoiceIntent.SetVolume, "Spotify", 50 },
        { "Сделай дискорд потише", VoiceIntent.ChangeVolume, "Discord", -10 },
        { "Выключи звук в хроме", VoiceIntent.Mute, "chrome", null },
        { "Громкость тридцать процентов", VoiceIntent.SetVolume, null, 30 },
        { "Громче на двадцать", VoiceIntent.ChangeVolume, null, 20 },
        { "Включи тёмную тему", VoiceIntent.SetTheme, null, null },
        { "Отключи автозапуск", VoiceIntent.SetAutostart, null, null },
        { "Переключи на наушники", VoiceIntent.SwitchDevice, null, null },
        { "Открой настройки", VoiceIntent.OpenSettings, null, null },
        { "Телеграм на сорок", VoiceIntent.SetVolume, "Telegram", 40 },
        { "Стим потише", VoiceIntent.ChangeVolume, "steam", -10 },
        { "Майнкрафт на двадцать процентов", VoiceIntent.SetVolume, "javaw", 20 },
        { "Включи звук дискорда", VoiceIntent.Unmute, "Discord", null },
        { "Шаг громкости пять", VoiceIntent.SetStep, null, 5 },
    };

    [SkippableTheory]
    [MemberData(nameof(Phrases))]
    public void RecognizesSpokenCommand(string phrase, VoiceIntent intent, string? app, int? value)
    {
        Skip.If(_fixture.Model == null, "Нет модели распознавания");
        var pcm = Synthesize(phrase);
        Skip.If(pcm == null, "Нет русского голоса для синтеза");

        var grammar = VoiceGrammar.Build(Apps, Devices, _fixture.Model!.HasWord);
        var result = VoiceService.RecognizePcm(_fixture.Model, pcm!, grammar);
        var (cmd, text) = VoiceCommandParser.ParseBest(result, Apps, Devices);
        _output.WriteLine($"«{phrase}» → свободно «{result.FreeText}», грамматика «{result.GrammarText}» → выбрано «{text}»");

        Assert.Equal(intent, cmd.Intent);
        Assert.Equal(app, cmd.App);
        if (value != null)
            Assert.Equal(value, cmd.Value);
    }

    private static byte[]? Synthesize(string text)
    {
        using var synth = new SpeechSynthesizer();
        var voice = synth.GetInstalledVoices().FirstOrDefault(v => v.VoiceInfo.Culture.Name == "ru-RU");
        if (voice == null)
            return null;
        synth.SelectVoice(voice.VoiceInfo.Name);
        using var ms = new MemoryStream();
        synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synth.Speak(text);
        synth.SetOutputToNull();
        // Немного тишины в конце, как после реальной фразы
        return ms.ToArray().Concat(new byte[16000]).ToArray();
    }
}
