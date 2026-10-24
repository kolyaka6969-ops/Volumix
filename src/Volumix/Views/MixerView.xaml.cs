using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Volumix.Services;

namespace Volumix.Views;

public partial class MixerView : UserControl
{
    public MixerView()
    {
        InitializeComponent();
        DataContext = App.Mixer;
        IsVisibleChanged += (_, _) => UpdateActive();
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private Window? _window;
    private bool _active;

    /// <summary>
    /// Индикаторы уровня опрашиваются 20 раз в секунду — только пока микшер действительно на экране.
    /// Свёрнутое окно остаётся IsVisible, поэтому смотрим ещё и на его состояние.
    /// </summary>
    private void UpdateActive()
    {
        var window = Window.GetWindow(this);
        if (!ReferenceEquals(window, _window))
        {
            if (_window != null)
                _window.StateChanged -= OnWindowStateChanged;
            _window = window;
            if (_window != null)
                _window.StateChanged += OnWindowStateChanged;
        }

        bool active = IsVisible && _window?.WindowState != WindowState.Minimized;
        if (active == _active)
            return;
        _active = active;
        App.Mixer.SetViewVisible(active);
    }

    private void OnWindowStateChanged(object? sender, System.EventArgs e) => UpdateActive();

    /// <summary>Колесо мыши над ползунком меняет громкость, а не прокручивает список.</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not Slider)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

        if (element is Slider slider)
        {
            slider.Value += (e.Delta > 0 ? 1 : -1) * SettingsService.Current.VolumeStep;
            e.Handled = true;
        }
    }
}
