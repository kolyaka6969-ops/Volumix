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
        IsVisibleChanged += (_, e) => App.Mixer.SetViewVisible((bool)e.NewValue);
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

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
