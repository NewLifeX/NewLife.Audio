using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NewLife.Audio.Midi;

namespace AudioStudio.Controls;

/// <summary>MIDI 波形显示控件</summary>
/// <remarks>
/// 深色渐变背景、对称 min/max 包络、RMS 响度线、节拍网格、发光播放头，
/// 以及只读的迷你钢琴卷缩略图。点击或拖动可定位。
/// </remarks>
public class MidiWaveformControl : Control
{
    private static readonly IBrush _background;
    private static readonly IBrush _waveBrush;
    private static readonly IBrush _textBrush;
    private static readonly IBrush _bubbleBrush;
    private static readonly Pen _gridPen = new(new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)), 1);
    private static readonly Pen _centerPen = new(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
    private static readonly Pen _beatPen = new(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), 1, new DashStyle([2, 4], 0));
    private static readonly Pen _barPen = new(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1);
    private static readonly Pen _borderPen = new(new SolidColorBrush(Color.FromArgb(80, 120, 160, 255)), 1);
    private static readonly Pen _rmsPen = new(new SolidColorBrush(Color.FromArgb(220, 251, 191, 36)), 1.2);
    private static readonly Pen _pianoBorderPen = new(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
    private static readonly Typeface _typeface = new("Inter");
    private static readonly IBrush[] _channelBrushes =
    [
        new SolidColorBrush(Color.FromArgb(210, 34, 211, 238)),
        new SolidColorBrush(Color.FromArgb(210, 96, 165, 250)),
        new SolidColorBrush(Color.FromArgb(210, 167, 139, 250)),
        new SolidColorBrush(Color.FromArgb(210, 244, 114, 182)),
        new SolidColorBrush(Color.FromArgb(210, 251, 191, 36)),
        new SolidColorBrush(Color.FromArgb(210, 52, 211, 153)),
        new SolidColorBrush(Color.FromArgb(210, 248, 113, 113)),
        new SolidColorBrush(Color.FromArgb(210, 148, 163, 184)),
    ];

    private static readonly Pen _waveGlowPen = new(new SolidColorBrush(Color.FromArgb(60, 59, 130, 246)), 3);
    private static readonly Pen _playheadPen = new(new SolidColorBrush(Color.FromArgb(255, 244, 114, 182)), 1.4);
    private static readonly Pen _playheadGlowPen = new(new SolidColorBrush(Color.FromArgb(70, 244, 114, 182)), 9);
    private static readonly Pen _playheadGlowPen2 = new(new SolidColorBrush(Color.FromArgb(90, 244, 114, 182)), 4);
    private static readonly Pen _hoverPen = new(new SolidColorBrush(Color.FromArgb(90, 226, 232, 240)), 1, new DashStyle([2, 4], 0));

    private Double[]? _mins;
    private Double[]? _maxs;
    private Double[]? _rms;
    private Byte[]? _peakData;
    private Int32 _peakWidth;
    private Int32 _peakChannels;
    private Double _pointerX = -1;
    private Boolean _dragging;

    /// <summary>PCM 数据（16 位小端交错）</summary>
    public static readonly StyledProperty<Byte[]?> PcmDataProperty =
        AvaloniaProperty.Register<MidiWaveformControl, Byte[]?>(nameof(PcmData));

    /// <summary>声道数</summary>
    public static readonly StyledProperty<Int32> ChannelsProperty =
        AvaloniaProperty.Register<MidiWaveformControl, Int32>(nameof(Channels), 2);

    /// <summary>采样率</summary>
    public static readonly StyledProperty<Int32> SampleRateProperty =
        AvaloniaProperty.Register<MidiWaveformControl, Int32>(nameof(SampleRate), 44100);

    /// <summary>总时长（秒）</summary>
    public static readonly StyledProperty<Double> DurationProperty =
        AvaloniaProperty.Register<MidiWaveformControl, Double>(nameof(Duration));

    /// <summary>播放位置（秒）</summary>
    public static readonly StyledProperty<Double> PositionProperty =
        AvaloniaProperty.Register<MidiWaveformControl, Double>(nameof(Position));

    /// <summary>节拍线时间（秒）</summary>
    public static readonly StyledProperty<IList<Double>?> BeatTimesProperty =
        AvaloniaProperty.Register<MidiWaveformControl, IList<Double>?>(nameof(BeatTimes));

    /// <summary>小节线时间（秒）</summary>
    public static readonly StyledProperty<IList<Double>?> BarTimesProperty =
        AvaloniaProperty.Register<MidiWaveformControl, IList<Double>?>(nameof(BarTimes));

    /// <summary>音符列表，用于迷你钢琴卷</summary>
    public static readonly StyledProperty<IList<MidiNote>?> NotesProperty =
        AvaloniaProperty.Register<MidiWaveformControl, IList<MidiNote>?>(nameof(Notes));

    /// <summary>请求定位</summary>
    public event EventHandler<Double>? SeekRequested;

    static MidiWaveformControl()
    {
        _background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#0B1020"), 0),
                new GradientStop(Color.Parse("#141B2D"), 0.55),
                new GradientStop(Color.Parse("#0E1424"), 1),
            },
        };
        _waveBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(230, 34, 211, 238), 0),
                new GradientStop(Color.FromArgb(230, 59, 130, 246), 0.5),
                new GradientStop(Color.FromArgb(230, 168, 85, 247), 1),
            },
        };
        _textBrush = new SolidColorBrush(Color.FromArgb(235, 226, 232, 240));
        _bubbleBrush = new SolidColorBrush(Color.FromArgb(210, 15, 23, 42));

        AffectsRender<MidiWaveformControl>(
            PcmDataProperty, ChannelsProperty, SampleRateProperty, DurationProperty,
            PositionProperty, BeatTimesProperty, BarTimesProperty, NotesProperty, BoundsProperty);
    }

    /// <summary>PCM 数据（16 位小端交错）</summary>
    public Byte[]? PcmData
    {
        get => GetValue(PcmDataProperty);
        set => SetValue(PcmDataProperty, value);
    }

    /// <summary>声道数</summary>
    public Int32 Channels
    {
        get => GetValue(ChannelsProperty);
        set => SetValue(ChannelsProperty, value);
    }

    /// <summary>采样率</summary>
    public Int32 SampleRate
    {
        get => GetValue(SampleRateProperty);
        set => SetValue(SampleRateProperty, value);
    }

    /// <summary>总时长（秒）</summary>
    public Double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    /// <summary>播放位置（秒）</summary>
    public Double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    /// <summary>节拍线时间（秒）</summary>
    public IList<Double>? BeatTimes
    {
        get => GetValue(BeatTimesProperty);
        set => SetValue(BeatTimesProperty, value);
    }

    /// <summary>小节线时间（秒）</summary>
    public IList<Double>? BarTimes
    {
        get => GetValue(BarTimesProperty);
        set => SetValue(BarTimesProperty, value);
    }

    /// <summary>音符列表，用于迷你钢琴卷</summary>
    public IList<MidiNote>? Notes
    {
        get => GetValue(NotesProperty);
        set => SetValue(NotesProperty, value);
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var rect = new Rect(0, 0, width, height);
        context.DrawRectangle(_background, null, new RoundedRect(rect, 10));

        using (context.PushClip(new Rect(1, 1, Math.Max(0, width - 2), Math.Max(0, height - 2))))
        {
            var pianoHeight = Notes is { Count: > 0 } ? height * 0.28 : 0.0;
            var waveTop = pianoHeight > 0 ? pianoHeight : 4.0;
            var waveHeight = Math.Max(1, height - waveTop - 6);

            if (pianoHeight > 0) DrawPianoRoll(context, width, pianoHeight);

            DrawGrid(context, width, height, waveTop, waveHeight);
            DrawBeatLines(context, width, height, waveTop, waveHeight);
            DrawWaveform(context, width, height, waveTop, waveHeight);
            DrawPlayhead(context, width, height);
        }

        context.DrawRectangle(null, _borderPen, new RoundedRect(rect, 10));
    }

    private void DrawPianoRoll(DrawingContext context, Double width, Double height)
    {
        var notes = Notes!;
        if (notes.Count == 0 || Duration <= 0) return;

        var minNote = 127;
        var maxNote = 0;
        foreach (var note in notes)
        {
            if (note.Number < minNote) minNote = note.Number;
            if (note.Number > maxNote) maxNote = note.Number;
        }
        if (maxNote <= minNote) maxNote = minNote + 1;

        var top = 6.0;
        var bottom = height - 6.0;
        var span = bottom - top;
        var noteHeight = Math.Max(1.5, span / (maxNote - minNote + 1));

        foreach (var note in notes)
        {
            var x = note.StartSeconds / Duration * width;
            var w = Math.Max(1.5, note.DurationSeconds / Duration * width);
            var y = top + (maxNote - note.Number) * noteHeight;

            var brush = _channelBrushes[Math.Abs(note.Channel) % _channelBrushes.Length];
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, y, w, Math.Max(1.5, noteHeight - 0.5)), 1.5));
        }

        context.DrawLine(_pianoBorderPen, new Point(0, height - 0.5), new Point(width, height - 0.5));
    }

    private void DrawGrid(DrawingContext context, Double width, Double height, Double waveTop, Double waveHeight)
    {
        var center = waveTop + waveHeight / 2;
        context.DrawLine(_centerPen, new Point(0, center), new Point(width, center));

        for (var i = 1; i < 4; i++)
        {
            var y = waveTop + waveHeight * i / 4.0;
            context.DrawLine(_gridPen, new Point(0, y), new Point(width, y));
        }
    }

    private void DrawBeatLines(DrawingContext context, Double width, Double height, Double waveTop, Double waveHeight)
    {
        if (Duration <= 0) return;

        if (BeatTimes != null)
        {
            foreach (var seconds in BeatTimes)
            {
                if (seconds < 0 || seconds > Duration) continue;
                var x = seconds / Duration * width;
                context.DrawLine(_beatPen, new Point(x, waveTop), new Point(x, height - 4));
            }
        }

        if (BarTimes != null)
        {
            foreach (var seconds in BarTimes)
            {
                if (seconds < 0 || seconds > Duration) continue;
                var x = seconds / Duration * width;
                context.DrawLine(_barPen, new Point(x, waveTop), new Point(x, height - 4));
            }
        }
    }

    private void DrawWaveform(DrawingContext context, Double width, Double height, Double waveTop, Double waveHeight)
    {
        var data = PcmData;
        if (data == null || data.Length < 4) return;

        EnsurePeaks(data, Math.Max(1, (Int32)width));
        if (_mins == null || _maxs == null || _rms == null || _peakWidth <= 0) return;

        var center = waveTop + waveHeight / 2;
        var half = Math.Max(1, waveHeight / 2 - 2);
        var count = Math.Min(_peakWidth, (Int32)width);
        if (count < 2) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, center), true);

            for (var x = 0; x < count; x++)
            {
                var y = center - _maxs[x] * half;
                ctx.LineTo(new Point(x, y));
            }
            for (var x = count - 1; x >= 0; x--)
            {
                var y = center - _mins[x] * half;
                ctx.LineTo(new Point(x, y));
            }

            ctx.EndFigure(true);
        }

        context.DrawGeometry(_waveBrush, null, geometry);
        context.DrawGeometry(null, _waveGlowPen, geometry);

        var rmsGeometry = new StreamGeometry();
        using (var ctx = rmsGeometry.Open())
        {
            ctx.BeginFigure(new Point(0, center), false);
            for (var x = 0; x < count; x++)
            {
                var y = center - _rms[x] * half;
                ctx.LineTo(new Point(x, y));
            }
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, _rmsPen, rmsGeometry);
    }

    private void DrawPlayhead(DrawingContext context, Double width, Double height)
    {
        if (Duration <= 0) return;

        if (_pointerX >= 0 && !_dragging)
        {
            context.DrawLine(_hoverPen, new Point(_pointerX, 0), new Point(_pointerX, height));
            var hoverTime = Math.Clamp(_pointerX / width, 0, 1) * Duration;
            var hoverText = FormatTime(hoverTime);
            var hoverFormatted = new FormattedText(hoverText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, 10, _textBrush);
            var hoverX = Math.Clamp(_pointerX + 6, 4, Math.Max(4, width - hoverFormatted.Width - 6));
            context.DrawRectangle(_bubbleBrush, null, new RoundedRect(new Rect(hoverX, height - 22, hoverFormatted.Width + 8, 16), 3));
            context.DrawText(hoverFormatted, new Point(hoverX + 4, height - 21));
        }

        var x = Math.Clamp(Position / Duration * width, 0, width);
        context.DrawLine(_playheadGlowPen, new Point(x, 0), new Point(x, height));
        context.DrawLine(_playheadGlowPen2, new Point(x, 0), new Point(x, height));
        context.DrawLine(_playheadPen, new Point(x, 0), new Point(x, height));

        var text = FormatTime(Position);
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _typeface, 11, _textBrush);
        var bubbleWidth = formatted.Width + 10;
        var bubbleX = Math.Clamp(x - bubbleWidth / 2, 4, Math.Max(4, width - bubbleWidth - 4));
        context.DrawRectangle(_bubbleBrush, null, new RoundedRect(new Rect(bubbleX, 5, bubbleWidth, 18), 4));
        context.DrawText(formatted, new Point(bubbleX + 5, 6));
    }

    private void EnsurePeaks(Byte[] data, Int32 width)
    {
        if (_mins != null && _maxs != null && _rms != null && _peakWidth == width && _peakChannels == Channels && ReferenceEquals(_peakData, data)) return;

        var channels = Math.Max(1, Channels);
        var frameBytes = channels * 2;
        var frames = data.Length / frameBytes;
        if (frames <= 0) return;

        var mins = new Double[width];
        var maxs = new Double[width];
        var rms = new Double[width];
        var samplesPerPixel = Math.Max(1, frames / width);

        for (var x = 0; x < width; x++)
        {
            var start = x * samplesPerPixel;
            var end = Math.Min(frames, start + samplesPerPixel);
            if (end <= start) end = Math.Min(frames, start + 1);

            var min = 0.0;
            var max = 0.0;
            var sum = 0.0;
            var count = 0;

            for (var frame = start; frame < end; frame++)
            {
                var value = 0.0;
                for (var channel = 0; channel < channels; channel++)
                {
                    var offset = frame * frameBytes + channel * 2;
                    if (offset + 1 >= data.Length) break;
                    value += (Int16)((data[offset + 1] << 8) | data[offset]) / 32768.0;
                }
                value /= channels;

                if (value < min) min = value;
                if (value > max) max = value;
                sum += value * value;
                count++;
            }

            mins[x] = min;
            maxs[x] = max;
            rms[x] = count > 0 ? Math.Sqrt(sum / count) : 0;
        }

        _mins = mins;
        _maxs = maxs;
        _rms = rms;
        _peakData = data;
        _peakWidth = width;
        _peakChannels = Channels;
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        _dragging = true;
        e.Pointer.Capture(this);
        SeekTo(e.GetPosition(this).X);
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);
        _pointerX = position.X;
        if (_dragging) SeekTo(position.X);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        _dragging = false;
        e.Pointer.Capture(null);
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        _pointerX = -1;
        InvalidateVisual();
    }

    private void SeekTo(Double x)
    {
        if (Duration <= 0 || Bounds.Width <= 0) return;

        var seconds = Math.Clamp(x / Bounds.Width, 0, 1) * Duration;
        Position = seconds;
        SeekRequested?.Invoke(this, seconds);
    }

    private static String FormatTime(Double seconds)
    {
        if (seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(Int32)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }
}
