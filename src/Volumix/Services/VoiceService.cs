using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using NAudio.Wave;

namespace Volumix.Services;

/// <summary>Результат одной фразы: свободное распознавание и распознавание по грамматике команд.</summary>
public sealed record VoiceResult(string FreeText, string GrammarText);

/// <summary>
/// Офлайн-распознавание речи (Vosk). Слушает микрофон до конца фразы.
/// Звук идёт сразу в два распознавателя: свободный (лучше понимает обычные слова и падежи)
/// и по грамматике (узнаёт названия программ, которых нет в словаре модели).
/// Модель грузится при первой команде и выгружается после простоя, чтобы не держать ~300 МБ памяти.
/// </summary>
public sealed class VoiceService : IDisposable
{
    public const string ModelName = "vosk-model-small-ru-0.22";
    private const int SampleRate = 16000;
    private static readonly TimeSpan MaxListen = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan SilenceAfterSpeech = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan UnloadAfter = TimeSpan.FromMinutes(3);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _watchdog;
    private readonly DispatcherTimer _unloadTimer;
    private readonly object _lock = new();
    private VoskNative.Model? _model;
    private Task<VoskNative.Model>? _loading;
    private WaveInEvent? _waveIn;
    private VoskNative.Recognizer? _free;
    private VoskNative.Recognizer? _grammar;
    private readonly StringBuilder _freeText = new();
    private readonly StringBuilder _grammarText = new();
    private DateTime _startedAt;
    private DateTime _lastPartialAt;
    private string _lastPartial = "";
    private bool _finished;

    public VoiceService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _watchdog = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _watchdog.Tick += OnWatchdog;
        _unloadTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = UnloadAfter };
        _unloadTimer.Tick += (_, _) => Unload();
    }

    public bool IsListening { get; private set; }

    /// <summary>Модель загружается (первый запуск занимает пару секунд).</summary>
    public event Action? Loading;
    public event Action? ListeningStarted;
    public event Action<string>? PartialText;
    /// <summary>Фраза закончилась (тексты пустые, если ничего не сказали).</summary>
    public event Action<VoiceResult>? Recognized;
    public event Action<string>? Failed;

    public static string? FindModel()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "models", ModelName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Volumix", "models", ModelName),
        };
        foreach (var path in candidates)
            if (Directory.Exists(Path.Combine(path, "am")))
                return path;
        return null;
    }

    public static bool HasMicrophone => WaveInEvent.DeviceCount > 0;

    /// <param name="buildGrammar">Строит JSON-грамматику; получает проверку «есть ли слово в словаре модели».</param>
    public async void Start(Func<Func<string, bool>, string> buildGrammar)
    {
        if (IsListening)
            return;

        if (FindModel() is not { } modelPath)
        {
            Failed?.Invoke("Модель распознавания речи не найдена — переустановите Volumix");
            return;
        }
        if (!HasMicrophone)
        {
            Failed?.Invoke("Микрофон не найден");
            return;
        }

        IsListening = true;
        _unloadTimer.Stop();
        try
        {
            if (_model == null)
            {
                Loading?.Invoke();
                _loading ??= Task.Run(() =>
                {
                    VoskNative.vosk_set_log_level(-1);
                    return new VoskNative.Model(modelPath);
                });
                _model = await _loading;
                _loading = null;
            }
            if (!IsListening)
                return; // отменили, пока грузилась модель

            var grammar = buildGrammar(_model.HasWord);
            lock (_lock)
            {
                _free = new VoskNative.Recognizer(_model, SampleRate);
                _grammar = new VoskNative.Recognizer(_model, SampleRate, grammar);
                _freeText.Clear();
                _grammarText.Clear();
                _finished = false;
                _lastPartial = "";
            }

            _waveIn = new WaveInEvent
            {
                DeviceNumber = -1, // устройство записи по умолчанию
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 100,
            };
            _waveIn.DataAvailable += OnData;
            _waveIn.StartRecording();
            _startedAt = _lastPartialAt = DateTime.UtcNow;
            _watchdog.Start();
            ListeningStarted?.Invoke();
        }
        catch (Exception ex)
        {
            _loading = null;
            Cleanup();
            Failed?.Invoke("Не удалось запустить распознавание: " + ex.Message);
        }
    }

    /// <summary>Остановить без результата.</summary>
    public void Cancel()
    {
        if (!IsListening)
            return;
        lock (_lock)
            _finished = true;
        Cleanup();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        bool endOfPhrase = false;
        string? partial = null;
        lock (_lock)
        {
            if (_finished || _free == null || _grammar == null)
                return;

            if (_grammar.AcceptWaveform(e.Buffer, e.BytesRecorded))
                Append(_grammarText, _grammar.Result());

            if (_free.AcceptWaveform(e.Buffer, e.BytesRecorded))
            {
                Append(_freeText, _free.Result());
                endOfPhrase = _freeText.Length > 0;
            }
            else
            {
                partial = (_freeText + " " + ReadText(_free.PartialResult(), "partial")).Trim();
            }
        }

        if (endOfPhrase)
            _dispatcher.BeginInvoke(Finish);
        else if (partial != null)
            _dispatcher.BeginInvoke(() => OnPartial(partial));
    }

    private void OnPartial(string text)
    {
        if (!IsListening || text == _lastPartial)
            return;
        _lastPartial = text;
        _lastPartialAt = DateTime.UtcNow;
        PartialText?.Invoke(text);
    }

    private void OnWatchdog(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        bool silentAfterSpeech = _lastPartial.Length > 0 && now - _lastPartialAt > SilenceAfterSpeech;
        if (now - _startedAt > MaxListen || silentAfterSpeech)
            Finish();
    }

    private void Finish()
    {
        if (!IsListening)
            return;
        VoiceResult result;
        lock (_lock)
        {
            if (_free == null || _grammar == null)
                return;
            _finished = true;
            Append(_freeText, _free.FinalResult());
            Append(_grammarText, _grammar.FinalResult());
            result = new VoiceResult(_freeText.ToString(), _grammarText.ToString());
        }
        Cleanup();
        Recognized?.Invoke(result);
    }

    private void Cleanup()
    {
        IsListening = false;
        _watchdog.Stop();
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnData;
            try { _waveIn.StopRecording(); } catch (Exception) { }
            _waveIn.Dispose();
            _waveIn = null;
        }
        lock (_lock)
        {
            _free?.Dispose();
            _grammar?.Dispose();
            _free = _grammar = null;
        }
        _unloadTimer.Stop();
        _unloadTimer.Start();
    }

    private void Unload()
    {
        _unloadTimer.Stop();
        if (IsListening || _loading != null)
            return;
        _model?.Dispose();
        _model = null;
    }

    private static void Append(StringBuilder sb, string json)
    {
        var text = ReadText(json, "text");
        if (text.Length == 0)
            return;
        if (sb.Length > 0)
            sb.Append(' ');
        sb.Append(text);
    }

    internal static string ReadText(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var value) ? value.GetString()?.Trim() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>Распознаёт готовую запись (PCM 16 кГц, 16 бит, моно) так же, как с микрофона — для тестов.</summary>
    internal static VoiceResult RecognizePcm(VoskNative.Model model, byte[] pcm, string grammar)
    {
        using var free = new VoskNative.Recognizer(model, SampleRate);
        using var grm = new VoskNative.Recognizer(model, SampleRate, grammar);
        var freeText = new StringBuilder();
        var grammarText = new StringBuilder();
        const int chunk = 3200;
        for (int i = 0; i < pcm.Length; i += chunk)
        {
            int len = Math.Min(chunk, pcm.Length - i);
            var data = pcm[i..(i + len)];
            if (free.AcceptWaveform(data, len))
                Append(freeText, free.Result());
            if (grm.AcceptWaveform(data, len))
                Append(grammarText, grm.Result());
        }
        Append(freeText, free.FinalResult());
        Append(grammarText, grm.FinalResult());
        return new VoiceResult(freeText.ToString(), grammarText.ToString());
    }

    public void Dispose()
    {
        Cancel();
        _unloadTimer.Stop();
        _model?.Dispose();
        _model = null;
    }
}
