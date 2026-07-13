using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AudioStudio.Converters;

/// <summary>秒数转时间文本转换器</summary>
public class TimeSpanConverter : IValueConverter
{
    public Object? Convert(Object? value, Type targetType, Object? parameter, CultureInfo culture)
    {
        if (value is Double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1)
                return ts.ToString(@"h\:mm\:ss");
            return ts.ToString(@"mm\:ss");
        }
        return "00:00";
    }

    public Object? ConvertBack(Object? value, Type targetType, Object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>百分比转文本转换器</summary>
public class PercentConverter : IValueConverter
{
    public Object? Convert(Object? value, Type targetType, Object? parameter, CultureInfo culture)
    {
        if (value is Double d)
            return $"{d * 100:F0}%";
        return "0%";
    }

    public Object? ConvertBack(Object? value, Type targetType, Object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
