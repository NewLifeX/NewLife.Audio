using System;
using System.Globalization;
using Avalonia.Data.Converters;
using AudioStudio.Services;

namespace AudioStudio.Converters;

/// <summary>播放状态转按钮文本转换器</summary>
public class PlayPauseConverter : IValueConverter
{
    public Object? Convert(Object? value, Type targetType, Object? parameter, CultureInfo culture)
    {
        if (value is PlaybackState state)
            return state == PlaybackState.Playing ? "⏸" : "▶";
        return "▶";
    }

    public Object? ConvertBack(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
