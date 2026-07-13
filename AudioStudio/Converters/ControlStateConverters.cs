using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace AudioStudio.Converters;

/// <summary>布尔值取反</summary>
public class InverseBoolConverter : IValueConverter
{
    public Object? Convert(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => value is Boolean b ? !b : value;

    public Object? ConvertBack(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => value is Boolean b ? !b : value;
}

/// <summary>转换按钮文本（是否转换中）</summary>
public class ConvertButtonTextConverter : IValueConverter
{
    public Object? Convert(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => value is Boolean isConverting && isConverting ? "转换中..." : "开始转换";

    public Object? ConvertBack(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>播放/停止按钮文本</summary>
public class PlayStopTextConverter : IValueConverter
{
    public Object? Convert(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => value is Boolean isPlaying && isPlaying ? "⏹ 停止" : "▶ 播放测试音";

    public Object? ConvertBack(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>录制/停止按钮文本</summary>
public class RecStartStopTextConverter : IValueConverter
{
    public Object? Convert(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => value is Boolean isRecording && isRecording ? "⏹ 停止录制" : "⏺ 开始录制";

    public Object? ConvertBack(Object? value, Type targetType, Object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
