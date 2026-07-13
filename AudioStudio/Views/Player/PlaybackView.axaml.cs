using System;
using Avalonia.Controls;
using Avalonia.Input;
using AudioStudio.Models;

namespace AudioStudio.Views.Player;

public partial class PlaybackView : UserControl
{
    public PlaybackView()
    {
        InitializeComponent();
    }

    private void OnPlaylistItemPressed(Object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is PlaylistItem item)
        {
            if (DataContext is ViewModels.Player.PlaybackViewModel vm)
            {
                vm.PlaySelectedCommand.Execute(item);
            }
        }
    }
}
