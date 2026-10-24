using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Volumix.Services;

/// <summary>
/// Тонкая обёртка над C API libvosk (сама библиотека приходит из NuGet-пакета Vosk).
/// Своя, потому что штатная C#-обёртка передаёт строки в ANSI и портит кириллицу в грамматике.
/// </summary>
internal static class VoskNative
{
    private const string Lib = "libvosk";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void vosk_set_log_level(int level);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vosk_model_new(byte[] path);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void vosk_model_free(IntPtr model);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int vosk_model_find_word(IntPtr model, byte[] word);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vosk_recognizer_new(IntPtr model, float sampleRate);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vosk_recognizer_new_grm(IntPtr model, float sampleRate, byte[] grammar);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int vosk_recognizer_accept_waveform(IntPtr recognizer, byte[] data, int length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vosk_recognizer_result(IntPtr recognizer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vosk_recognizer_partial_result(IntPtr recognizer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr vosk_recognizer_final_result(IntPtr recognizer);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void vosk_recognizer_free(IntPtr recognizer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);

    internal static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>Kaldi открывает файлы ANSI-путём, поэтому кириллицу в пути заменяем коротким 8.3-именем.</summary>
    internal static string AnsiSafePath(string path)
    {
        var sb = new StringBuilder(1024);
        return GetShortPathName(path, sb, sb.Capacity) > 0 ? sb.ToString() : path;
    }

    public sealed class Model : IDisposable
    {
        internal IntPtr Handle { get; private set; }

        public Model(string path)
        {
            Handle = vosk_model_new(Utf8(AnsiSafePath(path)));
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException("Не удалось загрузить модель распознавания: " + path);
        }

        /// <summary>Есть ли слово в словаре модели (слова вне словаря грамматика игнорирует).</summary>
        public bool HasWord(string word) => vosk_model_find_word(Handle, Utf8(word)) >= 0;

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
                vosk_model_free(Handle);
            Handle = IntPtr.Zero;
        }
    }

    public sealed class Recognizer : IDisposable
    {
        private IntPtr _handle;

        /// <param name="grammar">JSON-массив допустимых фраз или null для свободной речи.</param>
        public Recognizer(Model model, float sampleRate, string? grammar = null)
        {
            _handle = grammar == null
                ? vosk_recognizer_new(model.Handle, sampleRate)
                : vosk_recognizer_new_grm(model.Handle, sampleRate, Utf8(grammar));
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("Не удалось создать распознаватель");
        }

        /// <summary>true — фраза закончилась, можно забирать <see cref="Result"/>.</summary>
        public bool AcceptWaveform(byte[] data, int length)
        {
            if (data.Length != length)
                data = data[..length];
            return vosk_recognizer_accept_waveform(_handle, data, length) == 1;
        }

        public string Result() => Marshal.PtrToStringUTF8(vosk_recognizer_result(_handle)) ?? "";
        public string PartialResult() => Marshal.PtrToStringUTF8(vosk_recognizer_partial_result(_handle)) ?? "";
        public string FinalResult() => Marshal.PtrToStringUTF8(vosk_recognizer_final_result(_handle)) ?? "";

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
                vosk_recognizer_free(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
