using System.Diagnostics;
using System.Drawing;
using System.Windows.Threading;
using Volumix.Services;

namespace Volumix.Tests;

public class MediaTrayTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RendersCenteredGlyphs(bool light)
    {
        int size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        var glyphs = new[] { MediaTrayIcons.GlyphPrevious, MediaTrayIcons.GlyphPlay, MediaTrayIcons.GlyphPause, MediaTrayIcons.GlyphNext };

        const int zoom = 6;
        using var preview = new Bitmap(glyphs.Length * (size + 4) * zoom, (size + 4) * zoom);
        using (var g = Graphics.FromImage(preview))
        {
            g.Clear(light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            for (int i = 0; i < glyphs.Length; i++)
            {
                using var icon = MediaTrayIcons.RenderGlyph(glyphs[i], light);
                using var bmp = icon.ToBitmap();
                Assert.Equal(size, bmp.Width);

                int minX = size, maxX = -1, minY = size, maxY = -1;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        if (bmp.GetPixel(x, y).A > 64)
                            (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));

                Assert.True(maxX >= 0, "значок пустой");
                Assert.InRange(maxX - minX + 1, size / 3, size);           // не крошечный и не обрезанный
                Assert.InRange(minX - (size - 1 - maxX), -2, 2);            // по центру
                Assert.InRange(minY - (size - 1 - maxY), -2, 2);

                g.DrawImage(bmp, (i * (size + 4) + 2) * zoom, 2 * zoom, size * zoom, size * zoom);
            }
        }
        preview.Save(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"volumix-media-{(light ? "light" : "dark")}.png"));
    }

    [Theory]
    [InlineData("Spotify.exe", true)]
    [InlineData("Chrome", true)]
    [InlineData("MSEdge", true)]
    [InlineData("Yandex.LIHLQ6TE3JFVF6LJDXD5CT2RIM", true)]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", false)]
    [InlineData("vlc.exe", false)]
    public void RecognizesPlayers(string appId, bool expected) => Assert.Equal(expected, SpotifyService.IsPlayer(appId));

    /// <summary>Только читает состояние Spotify, ничего не переключает.</summary>
    [SkippableFact]
    public void FindsRunningSpotify()
    {
        Skip.If(Process.GetProcessesByName("Spotify").Length == 0, "Spotify не запущен");

        var found = new TaskCompletionSource<(bool, string?)>();
        var thread = new Thread(() =>
        {
            var spotify = new SpotifyService(Dispatcher.CurrentDispatcher);
            spotify.Changed += () =>
            {
                if (spotify.IsAvailable)
                    found.TrySetResult((spotify.IsPlaying, $"{spotify.Artist} — {spotify.Title}"));
            };
            spotify.Start();
            Dispatcher.Run();
            spotify.Dispose();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            Assert.True(found.Task.Wait(TimeSpan.FromSeconds(10)), "медиа-сеанс Spotify не найден");
            var (_, track) = found.Task.Result;
            Assert.False(string.IsNullOrWhiteSpace(track.Trim('—', ' ')));
        }
        finally
        {
            Dispatcher.FromThread(thread)?.InvokeShutdown();
            thread.Join(5000);
        }
    }
}
