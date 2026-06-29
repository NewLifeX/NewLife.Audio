using System;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.DSP;

public class AudioPipelineTests
{
    [Fact(DisplayName = "AudioPipeline空管线Read返回0")]
    public void Read_EmptyPipeline_ReturnsZero()
    {
        var pipeline = new AudioPipeline();
        var buffer = new Single[100];
        var read = pipeline.Read(buffer, 0, 100);
        Assert.Equal(0, read);
    }

    [Fact(DisplayName = "AudioPipeline AddProcessor后InputFormat/OutputFormat正确")]
    public void AddProcessor_UpdatesFormats()
    {
        var pipeline = new AudioPipeline();
        var processor = new VolumeControl();
        pipeline.AddProcessor(processor);

        Assert.Equal(processor.InputFormat, pipeline.InputFormat);
        Assert.Equal(processor.OutputFormat, pipeline.OutputFormat);
        Assert.Single(pipeline.Processors);
    }

    [Fact(DisplayName = "AudioPipeline Source属性传递到第一个处理器")]
    public void Source_PropagatesToFirstProcessor()
    {
        var pipeline = new AudioPipeline();
        var processor = new VolumeControl();
        pipeline.AddProcessor(processor);

        var source = new VolumeControl();
        pipeline.Source = source;
        Assert.Same(source, processor.Source);
    }

    [Fact(DisplayName = "AudioPipeline多处理器串联链接")]
    public void AddProcessor_Multiple_ChainedCorrectly()
    {
        var pipeline = new AudioPipeline();
        var p1 = new VolumeControl();
        var p2 = new VolumeControl();
        var p3 = new VolumeControl();

        pipeline.AddProcessor(p1);
        pipeline.AddProcessor(p2);
        pipeline.AddProcessor(p3);

        Assert.Equal(3, pipeline.Processors.Count);
        Assert.Same(p1, pipeline.Processors[0]);
        Assert.Same(p2, pipeline.Processors[1]);
        Assert.Same(p3, pipeline.Processors[2]);
        // p2 的 Source 应为 p1
        Assert.Same(p1, p2.Source);
        // p3 的 Source 应为 p2
        Assert.Same(p2, p3.Source);
    }

    [Fact(DisplayName = "AudioPipeline RemoveProcessor正确重链接")]
    public void RemoveProcessor_ReLinksCorrectly()
    {
        var pipeline = new AudioPipeline();
        var p1 = new VolumeControl();
        var p2 = new VolumeControl();
        var p3 = new VolumeControl();

        pipeline.AddProcessor(p1);
        pipeline.AddProcessor(p2);
        pipeline.AddProcessor(p3);

        var removed = pipeline.RemoveProcessor(p2);
        Assert.True(removed);
        Assert.Equal(2, pipeline.Processors.Count);
        Assert.Same(p1, pipeline.Processors[0]);
        Assert.Same(p3, pipeline.Processors[1]);
        // p3 的 Source 应重新链接到 p1
        Assert.Same(p1, p3.Source);
    }

    [Fact(DisplayName = "AudioPipeline RemoveProcessor不存在的返回false")]
    public void RemoveProcessor_NotExists_ReturnsFalse()
    {
        var pipeline = new AudioPipeline();
        pipeline.AddProcessor(new VolumeControl());
        Assert.False(pipeline.RemoveProcessor(new VolumeControl()));
    }

    [Fact(DisplayName = "AudioPipeline AddProcessor传null抛异常")]
    public void AddProcessor_Null_ThrowsArgumentNull()
    {
        var pipeline = new AudioPipeline();
        Assert.Throws<ArgumentNullException>(() => pipeline.AddProcessor(null));
    }

    [Fact(DisplayName = "AudioPipeline Reset传播到所有处理器")]
    public void Reset_PropagatesToAll()
    {
        var pipeline = new AudioPipeline();
        var p1 = new VolumeControl();
        var p2 = new VolumeControl();
        pipeline.AddProcessor(p1);
        pipeline.AddProcessor(p2);

        // Reset 不应抛异常
        pipeline.Reset();
    }

    [Fact(DisplayName = "AudioPipeline从Source拉取数据经管线处理")]
    public void Read_WithSource_ReturnsData()
    {
        var pipeline = new AudioPipeline();
        var source = new VolumeControl();
        pipeline.AddProcessor(source);

        // 设置上游数据源
        var dataSource = new TestDataSource(100);
        source.Source = dataSource;

        var buffer = new Single[50];
        var read = pipeline.Read(buffer, 0, 50);
        Assert.Equal(50, read);
        // 数据应该非零（TestDataSource产生信号）
        var hasNonZero = false;
        for (var i = 0; i < read; i++)
            if (buffer[i] != 0) { hasNonZero = true; break; }
        Assert.True(hasNonZero);
    }

    /// <summary>测试用数据源，产生正弦波</summary>
    private sealed class TestDataSource : IAudioProcessor
    {
        private readonly Int32 _totalSamples;
        private Int32 _position;

        public AudioFormat InputFormat => AudioFormat.Default;
        public AudioFormat OutputFormat => AudioFormat.Default;
        public IAudioProcessor Source { get; set; }

        public TestDataSource(Int32 totalSamples) => _totalSamples = totalSamples;

        public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
        {
            var remaining = _totalSamples - _position;
            var toRead = Math.Min(count, remaining);
            for (var i = 0; i < toRead; i++)
                buffer[offset + i] = (Single)Math.Sin(2 * Math.PI * 440 * (_position + i) / 8000);
            _position += toRead;
            return toRead;
        }

        public void Reset() => _position = 0;
    }
}
