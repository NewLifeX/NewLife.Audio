using System;
using NewLife.Audio;
using NewLife.Audio.DSP;
using NewLife.Audio.Devices;
using NewLife.Audio.Streaming;
using Xunit;

namespace XUnitTest.Devices;

public class DeviceBackendTests
{
    [Fact(DisplayName = "AudioBackendFactory创建WaveOut后端")]
    public void Factory_Creates_Backend()
    {
        var backend = AudioBackendFactory.Create("waveout");
        Assert.NotNull(backend);
        Assert.Contains("WaveOut", backend.Name);
    }

    [Fact(DisplayName = "后端枚举设备返回至少默认设备")]
    public void EnumerateDevices_ReturnsAtLeastDefault()
    {
        var backend = AudioBackendFactory.Create();
        var devices = backend.EnumerateDevices();
        Assert.NotEmpty(devices);
    }

    [Fact(DisplayName = "后端创建播放器成功")]
    public void CreatePlayer_ReturnsValidPlayer()
    {
        var backend = AudioBackendFactory.Create();
        var player = backend.CreatePlayer();
        Assert.NotNull(player);
        player.Dispose();
    }

    [Fact(DisplayName = "后端创建录制器成功")]
    public void CreateRecorder_ReturnsValidRecorder()
    {
        var backend = AudioBackendFactory.Create();
        var recorder = backend.CreateRecorder();
        Assert.NotNull(recorder);
        recorder.Dispose();
    }

    [Fact(DisplayName = "AudioDeviceManager注册后端后可用")]
    public void DeviceManager_RegisterBackend()
    {
        var manager = new AudioDeviceManager();
        var backend = AudioBackendFactory.Create();
        manager.RegisterBackend(backend);
        Assert.NotNull(manager.ActiveBackend);
    }
}

public class WebSocketAudioPipeTests
{
    [Fact(DisplayName = "WebSocket管道发送帧含正确帧头")]
    public void SendFrame_ContainsHeader()
    {
        var pipe = new WebSocketAudioPipe(AudioFormat.Default, AVTypes.G711A);
        var audioData = new Byte[160];

        var message = pipe.SendFrame(audioData);
        Assert.True(message.Length > audioData.Length);
        // 第一字节为编码类型
        Assert.True(message[0] > 0);
        // 发送后序列号递增
        Assert.Equal((UInt16)1, pipe.SendSequence);
    }

    [Fact(DisplayName = "WebSocket管道接收帧解析正确")]
    public void ReceiveFrame_ParsesCorrectly()
    {
        var pipe = new WebSocketAudioPipe(AudioFormat.Default, AVTypes.G711A);
        var audioData = new Byte[] { 0x01, 0x02, 0x03 };

        var message = pipe.SendFrame(audioData);
        var received = pipe.ReceiveFrame(message);

        Assert.NotNull(received);
        Assert.Equal(audioData.Length, received.Length);
        Assert.Equal(audioData[0], received[0]);
    }

    [Fact(DisplayName = "WebSocket管道关闭后发送抛异常")]
    public void SendFrame_Closed_Throws()
    {
        var pipe = new WebSocketAudioPipe(AudioFormat.Default, AVTypes.G711A);
        pipe.Close();
        Assert.Throws<InvalidOperationException>(() => pipe.SendFrame(new Byte[10]));
    }
}
