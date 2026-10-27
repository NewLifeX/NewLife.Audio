using Avalonia.Controls;
using Avalonia.Input;
using AudioStudio.ViewModels.Midi;

namespace AudioStudio.Views.Midi;

public partial class MidiView : UserControl
{
    public MidiView()
    {
        InitializeComponent();
    }

    private void OnSeekRequested(Object? sender, Double seconds)
    {
        if (DataContext is MidiViewModel viewModel) viewModel.SeekTo(seconds);
    }

    private void OnPositionSliderReleased(Object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MidiViewModel viewModel && sender is Slider slider) viewModel.SeekTo(slider.Value);
    }
}
