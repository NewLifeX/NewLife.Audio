using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AudioStudio.Converters;

/// <summary>VAD 语音检测状态转颜色（语音=绿，静音=灰）</summary>
public class VadStateConverter : IValueConverter
{
    public Object? Convert(Object? value, Type targetType, Object? parameter, CultureInfo culture)
    {
        if (value is Boolean isSpeech && isSpeech)
            return new SolidColorBrush(Color.FromRgb(76, 175, 80)); // 绿色
        return new SolidColorBrush(Color.FromRgb(158, 158, 158)); // 灰色
    }

    public Object? ConvertBack(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
