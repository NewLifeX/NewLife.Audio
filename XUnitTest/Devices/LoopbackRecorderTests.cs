using System;
using NewLife.Audio.DSP;
using NewLife.Audio.Devices;
using Xunit;

namespace XUnitTest.Devices;

public class LoopbackRecorderTests
{
    [Fact(DisplayName = "环路录制器初始化和启停")]
    public void LoopbackRecorder_Init_StartStop()
    {
        var recorder = new LoopbackRecorder();
        recorder.Init(AudioFormat.Default);
        Assert.False(recorder.IsRecording);

        recorder.StartRecording();
        Assert.True(recorder.IsRecording);

        recorder.StopRecording();
        Assert.False(recorder.IsRecording);
    }
}
