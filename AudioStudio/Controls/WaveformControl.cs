using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AudioStudio.Controls;

/// <summary>音频波形显示控件</summary>
public class WaveformControl : Control
{
    private readonly Pen _waveformPen = new(Brushes.DodgerBlue, 1);
    private readonly Pen _centerLinePen = new(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 0.5);

    /// <summary>PCM 音频数据（16-bit）</summary>
    public static readonly StyledProperty<Byte[]> PcmDataProperty =
        AvaloniaProperty.Register<WaveformControl, Byte[]>(nameof(PcmData), []);

    public Byte[] PcmData
    {
        get => GetValue(PcmDataProperty);
        set => SetValue(PcmDataProperty, value);
    }

    static WaveformControl()
    {
        AffectsRender<WaveformControl>(PcmDataProperty, BoundsProperty);
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        // 绘制背景
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(15, 100, 150, 255)),
            new Rect(0, 0, width, height));

        // 绘制中心线
        var centerY = height / 2;
        context.DrawLine(_centerLinePen, new Point(0, centerY), new Point(width, centerY));

        var data = PcmData;
        if (data == null || data.Length < 2) return;

        var sampleCount = data.Length / 2;
        if (sampleCount < 2) return;

        // 降采样到控件宽度
        var samplesPerPixel = Math.Max(1, sampleCount / (Int32)width);
        var points = new Point[Math.Min(sampleCount / samplesPerPixel, (Int32)width)];

        for (var x = 0; x < points.Length; x++)
        {
            var startSample = x * samplesPerPixel;
            var endSample = Math.Min(startSample + samplesPerPixel, sampleCount);

            // 在采样块中取最大振幅
            var maxAbs = 0;
            for (var s = startSample; s < endSample; s++)
            {
                var val = (Int16)(data[s * 2 + 1] << 8 | data[s * 2]);
                var abs = Math.Abs(val);
                if (abs > maxAbs) maxAbs = abs;
            }

            var normalized = (Double)maxAbs / 32768.0;
            var y = centerY - normalized * (height / 2 - 2);
            points[x] = new Point(x, y);
        }

        // 绘制波形
        if (points.Length > 1)
        {
            var streamGeometry = new StreamGeometry();
            using (var ctx = streamGeometry.Open())
            {
                ctx.BeginFigure(points[0], false);
                for (var i = 1; i < points.Length; i++)
                    ctx.LineTo(points[i]);
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, _waveformPen, streamGeometry);
        }
    }
}
