using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AudioStudio.Converters;

/// <summary>电平值转颜色（低=绿，中=黄，高=红）</summary>
public class LevelColorConverter : IValueConverter
{
    public Object? Convert(Object? value, Type targetType, Object? parameter, CultureInfo culture)
    {
        if (value is Double level)
        {
            if (level > 0.8) return Avalonia.Media.Brushes.Red;
            if (level > 0.5) return Avalonia.Media.Brushes.Orange;
            return Avalonia.Media.Brushes.Green;
        }
        return Avalonia.Media.Brushes.Gray;
    }

    public Object? ConvertBack(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>电平值转宽度（按比例）</summary>
public class LevelWidthConverter : IValueConverter
{
    public Object? Convert(Object? value, Type targetType, Object? parameter, CultureInfo culture)
    {
        if (value is Double level && parameter is String maxWidthStr && Double.TryParse(maxWidthStr, out var maxWidth))
            return level * maxWidth;
        if (value is Double level2)
            return level2 * 200;
        return 0.0;
    }

    public Object? ConvertBack(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
