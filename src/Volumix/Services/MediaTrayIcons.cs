using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Volumix.Services;

/// <summary>
/// Три значка в трее — «предыдущий», «пауза/play», «следующий» — для Spotify или вкладки браузера.
/// Значки создаются один раз и только прячутся: Windows запоминает видимость значка по его номеру,
/// а новый номер снова отправил бы его в скрытые.
/// </summary>
public sealed class MediaTrayIcons : IDisposable
{
    // Сплошные значки Segoe Fluent Icons: тонкие контурные на 16 px выглядят бледно
    internal const string GlyphPrevious = "\uF8AC";
    internal const string GlyphNext = "\uF8AD";
    internal const string GlyphPlay = "\uF5B0";
    internal const string GlyphPause = "\uF8AE";

    /// <summary>В Segoe MDL2 Assets (Windows 10) сплошных нет — берём контурные.</summary>
    private static readonly Dictionary<string, string> Mdl2Glyphs = new()
    {
        [GlyphPrevious] = "\uE892",
        [GlyphNext] = "\uE893",
        [GlyphPlay] = "\uE768",
        [GlyphPause] = "\uE769",
    };

    private readonly SpotifyService _spotify;
    private readonly Dispatcher _dispatcher;
    private readonly Forms.NotifyIcon _previous;
    private readonly Forms.NotifyIcon _playPause;
    private readonly Forms.NotifyIcon _next;
    private readonly Dictionary<string, Icon> _icons = new();
    private bool _enabled;
    private bool _shown;

    public MediaTrayIcons(SpotifyService spotify, Dispatcher dispatcher, Action showMenu)
    {
        _spotify = spotify;
        _dispatcher = dispatcher;

        // Порядок создания не менять: Windows помнит, показан ли значок на панели, по его номеру
        _previous = Create("Предыдущий трек", _spotify.Previous);
        _playPause = Create("Плеер", _spotify.TogglePlayPause);
        _next = Create("Следующий трек", _spotify.Next);

        Forms.NotifyIcon Create(string text, Action click)
        {
            var icon = new Forms.NotifyIcon { Text = text };
            icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) click();
                else if (e.Button == Forms.MouseButtons.Right) showMenu();
            };
            return icon;
        }

        RenderIcons();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _spotify.Changed += Update;
    }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; Update(); }
    }

    private void Update()
    {
        bool show = _enabled && _spotify.IsAvailable;

        _playPause.Icon = _icons[_spotify.IsPlaying ? GlyphPause : GlyphPlay];
        var track = _spotify.Title == null ? "Плеер"
            : _spotify.Artist == null ? _spotify.Title
            : $"{_spotify.Artist} — {_spotify.Title}";
        _playPause.Text = Trim($"{(_spotify.IsPlaying ? "Пауза" : "Играть")}\n{track}");

        if (show == _shown)
            return;
        _shown = show;
        // Windows ставит только что добавленный значок левее прежних, поэтому добавляем справа налево
        _next.Visible = show;
        _playPause.Visible = show;
        _previous.Visible = show;
    }

    private static string Trim(string text) => text.Length > 127 ? text[..126] + "…" : text;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            _dispatcher.BeginInvoke(() =>
            {
                RenderIcons();
                Update();
            });
    }

    private void RenderIcons()
    {
        var old = new List<Icon>(_icons.Values);
        bool light = IsTaskbarLight();
        foreach (var glyph in new[] { GlyphPrevious, GlyphNext, GlyphPlay, GlyphPause })
            _icons[glyph] = RenderGlyph(glyph, light);

        _previous.Icon = _icons[GlyphPrevious];
        _next.Icon = _icons[GlyphNext];
        _playPause.Icon = _icons[_spotify.IsPlaying ? GlyphPause : GlyphPlay];
        foreach (var icon in old)
            icon.Dispose();
    }

    private static bool IsTaskbarLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    private static FontFamily IconFamily()
    {
        try { return new FontFamily("Segoe Fluent Icons"); }
        catch (ArgumentException) { return new FontFamily("Segoe MDL2 Assets"); } // Windows 10
    }

    /// <summary>Рисует значок шрифта и упаковывает его в .ico с PNG внутри, чтобы сохранить сглаживание.</summary>
    internal static Icon RenderGlyph(string glyph, bool lightTaskbar)
    {
        int size = Forms.SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        using (var family = IconFamily())
        using (var path = new GraphicsPath())
        using (var brush = new SolidBrush(lightTaskbar ? Color.FromArgb(28, 28, 28) : Color.White))
        {
            if (family.Name != "Segoe Fluent Icons")
                glyph = Mdl2Glyphs[glyph];
            g.SmoothingMode = SmoothingMode.AntiAlias;
            path.AddString(glyph, family, (int)FontStyle.Regular, size * 0.75f, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            // Центрируем с привязкой краёв к пикселям, иначе края размываются
            using var center = new Matrix();
            center.Translate(MathF.Round((size - b.Width) / 2) - b.X, MathF.Round((size - b.Height) / 2) - b.Y);
            path.Transform(center);
            g.FillPath(brush, path);
        }

        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0);          // reserved
            w.Write((short)1);          // тип: значок
            w.Write((short)1);          // один кадр
            w.Write((byte)size);
            w.Write((byte)size);
            w.Write((byte)0);           // палитра
            w.Write((byte)0);
            w.Write((short)1);          // плоскости
            w.Write((short)32);         // бит на пиксель
            w.Write((int)png.Length);
            w.Write(22);                // смещение данных: 6 + 16
            w.Write(png.ToArray());
        }
        ico.Position = 0;
        return new Icon(ico);
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _spotify.Changed -= Update;
        foreach (var icon in new[] { _previous, _playPause, _next })
        {
            icon.Visible = false;
            icon.Dispose();
        }
        foreach (var icon in _icons.Values)
            icon.Dispose();
    }
}
