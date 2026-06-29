using System;
using NewLife.Audio;
using NewLife.Audio.Devices;
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
