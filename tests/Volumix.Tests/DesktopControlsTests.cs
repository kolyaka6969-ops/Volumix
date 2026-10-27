using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Volumix.Services;
using Volumix.Views;

namespace Volumix.Tests;

/// <summary>Кнопки плеера на обоях: где они на экране и как выглядят.</summary>
[Collection("DPI")]
public class DesktopControlsTests
{
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new Exception("STA", error);
    }

    [Fact]
    public void PlacesButtonsUnderProgress()
    {
        OnSta(() =>
        {
            var view = new DesktopLyricsView();
            // Видимое окно за пределами экрана: кнопкам нужны настоящие экранные координаты
            using var source = new HwndSource(new HwndSourceParameters("test", 1920, 1080)
            {
                PositionX = -5000,
                PositionY = -5000,
                WindowStyle = unchecked((int)0x90000000), // WS_POPUP | WS_VISIBLE
            }) { RootVisual = view };
            view.ShowTrack(new NowPlaying("Carry On Wayward Son", "Kansas", null, null, true,
                TimeSpan.FromSeconds(169), DateTimeOffset.Now, TimeSpan.FromSeconds(175)));
            view.UpdateLayout();

            var bounds = view.ControlBounds();
            Assert.Equal(new[] { DesktopControl.Seek, DesktopControl.Previous, DesktopControl.PlayPause, DesktopControl.Next },
                bounds.Select(b => b.Control));
            var rects = bounds.Select(b => b.Bounds).ToList();
            Assert.All(rects, r => Assert.True(r.Width > 20 && r.Height > 20, r.ToString()));
            // Полоса над кнопками; кнопки слева направо, не перекрываются, на одной высоте
            Assert.True(rects[0].Bottom <= rects[1].Top && rects[0].Width > rects[2].Width * 3);
            Assert.True(rects[1].Right <= rects[2].Left && rects[2].Right <= rects[3].Left);
            Assert.Equal(rects[1].Top, rects[3].Top);

            view.ControlsVisible = false;
            Assert.Empty(view.ControlBounds());
            view.ControlsVisible = true;
            // Тянут полосу: она и время показывают место под мышью, кружок на конце
            view.SetPointer(DesktopControl.Seek, true, 0.5);
            view.UpdateLayout();
            Assert.Equal("1:27", view.ElapsedText.Text);
            Assert.Equal(Visibility.Visible, view.ProgressKnob.Visibility);

            var path = Environment.GetEnvironmentVariable("VOLUMIX_RENDER");
            if (path != null)
            {
                var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(path);
                png.Save(file);
            }

            // Заглушка между треками: без обложки и времени
            view.ShowPlaceholder();
            view.UpdateLayout();
            Assert.Equal("Загрузка…", view.TitleText.Text);
            Assert.Equal("", view.ElapsedText.Text);
            Assert.Equal("", view.DurationText.Text);
            view.Stop();
        });
    }

    [Theory]
    [InlineData("wallpaper64", true)]
    [InlineData("wallpaper32", true)]
    [InlineData("webwallpaper64", true)]
    [InlineData("edgewallpaper64", true)]
    [InlineData("applicationwallpaperinject64", true)]
    [InlineData("wallpaperui", true)]
    [InlineData("lively", true)]
    [InlineData("lively.player", true)]
    [InlineData("explorer", false)]
    [InlineData("spotify", false)]
    [InlineData("volumix", false)]
    [InlineData("chrome", false)]
    public void RecognizesWallpaperProcesses(string exe, bool expected)
    {
        Assert.Equal(expected, NativeMethods.IsWallpaperExecutableName(exe));
    }
}
