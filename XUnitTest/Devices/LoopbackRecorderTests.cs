using System;
using NewLife.Audio.DSP;
using NewLife.Audio.Devices;
using Xunit;

namespace XUnitTest.Devices;

public class LoopbackRecorderTests
{
    [Fact(DisplayName = "环路录制器初始化后处于未录制状态")]
    public void LoopbackRecorder_Init_NotRecording()
    {
        using var recorder = new LoopbackRecorder();
        recorder.Init(AudioFormat.Default);

        Assert.False(recorder.IsRecording);
        Assert.Equal(AudioFormat.Default.SampleRate, recorder.Format.SampleRate);
    }

    [Fact(DisplayName = "环路录制器启停：有渲染端点则可用，无后端则明确报不支持")]
    public void LoopbackRecorder_StartStop()
    {
        using var recorder = new LoopbackRecorder();
        recorder.Init(AudioFormat.Default);

        if (!LoopbackRecorder.IsSupported)
        {
            Assert.Throws<PlatformNotSupportedException>(() => recorder.StartRecording());
            return;
        }

        try
        {
            recorder.StartRecording();
        }
        catch (InvalidOperationException)
        {
            // 运行环境没有可用渲染端点，明确报错符合预期
            return;
        }

        Assert.True(recorder.IsRecording);
        recorder.StopRecording();
        Assert.False(recorder.IsRecording);
    }
}
