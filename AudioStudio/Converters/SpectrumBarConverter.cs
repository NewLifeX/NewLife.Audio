using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AudioStudio.Converters;

/// <summary>频谱值转条形高度（0.0~1.0 → 像素）</summary>
public class SpectrumBarConverter : IValueConverter
{
    public Object? Convert(Object? value, Type targetType, Object? parameter, CultureInfo culture)
    {
        if (value is Double level)
            return Math.Max(2, level * 150); // 最大 150px
        return 2.0;
    }

    public Object? ConvertBack(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
