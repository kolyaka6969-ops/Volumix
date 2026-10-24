using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Volumix.Services;
using Volumix.ViewModels;

namespace Volumix.Views;

/// <summary>Всплывающий индикатор внизу экрана (не забирает фокус и пропускает клики).</summary>
public partial class OsdWindow : Window
{
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(1600);
    private static readonly TimeSpan LongDelay = TimeSpan.FromMilliseconds(2800);

    private readonly DispatcherTimer _hideTimer = new();
    private readonly DoubleAnimation _pulse = new(1, 0.35, TimeSpan.FromMilliseconds(650))
    {
        AutoReverse = true,
        RepeatBehavior = RepeatBehavior.Forever,
    };

    public OsdWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => NativeMethods.MakeOverlayWindow(new WindowInteropHelper(this).Handle);
        SizeChanged += (_, _) => Place();
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) => { if (!_hideTimer.IsEnabled && Opacity == 0) Hide(); };
            BeginAnimation(OpacityProperty, fade);
        };
    }

    /// <param name="detail">Вторая строка, например распознанная фраза.</param>
    public void ShowChannel(ChannelViewModel channel, string? detail = null)
    {
        SetIcon(channel.Icon, channel.IsMuted ? "" : channel.Icon == null ? channel.FallbackGlyph : null);
        NameText.Text = channel.Name;
        SetDetail(detail);
        ValueText.Text = channel.IsMuted ? "" : channel.Volume.ToString("0");
        Bar.Visibility = Visibility.Visible;
        Bar.Opacity = channel.IsMuted ? 0.4 : 1;
        BarScale.ScaleX = channel.Volume / 100;
        Pop(detail == null ? ShortDelay : LongDelay);
    }

    public void ShowMessage(string glyph, string text, string? detail = null)
    {
        SetIcon(null, glyph);
        NameText.Text = text;
        SetDetail(detail);
        ValueText.Text = "";
        Bar.Visibility = Visibility.Collapsed;
        Pop(detail == null ? ShortDelay : LongDelay);
    }

    /// <summary>Режим «слушаю»: мигающий микрофон, текст по мере распознавания, без автоскрытия.</summary>
    public void ShowListening(string text)
    {
        SetIcon(null, "");
        AppGlyph.Foreground = (Brush)FindResource("AccentFillColorDefaultBrush");
        AppGlyph.BeginAnimation(OpacityProperty, _pulse);
        NameText.Text = text;
        SetDetail(null);
        ValueText.Text = "";
        Bar.Visibility = Visibility.Collapsed;
        Pop(null);
    }

    private void SetDetail(string? detail)
    {
        DetailText.Text = detail ?? "";
        DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetIcon(ImageSource? icon, string? glyph)
    {
        AppGlyph.BeginAnimation(OpacityProperty, null);
        AppGlyph.Opacity = 1;
        AppGlyph.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        AppIcon.Source = glyph == null ? icon : null;
        AppGlyph.Text = glyph ?? "";
    }

    private void Pop(TimeSpan? hideAfter)
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible)
            Show();
        Place();
        _hideTimer.Stop();
        if (hideAfter is { } delay)
        {
            _hideTimer.Interval = delay;
            _hideTimer.Start();
        }
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 24;
    }
}
