using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AudioStudio.Converters;

/// <summary>对象非空判断</summary>
public class IsNotNullConverter : IValueConverter
{
    public Object? Convert(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => value != null;

    public Object? ConvertBack(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
