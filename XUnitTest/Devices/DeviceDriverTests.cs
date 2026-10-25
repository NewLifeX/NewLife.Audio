using System;
using NewLife.Audio;
using NewLife.Audio.Devices;
using Xunit;

namespace XUnitTest.Devices;

public class DeviceDriverTests
{
    [Fact(DisplayName = "AudioDriverFactory创建WaveOut驱动")]
    public void Factory_Creates_Driver()
    {
        var driver = AudioDriverFactory.Create("waveout");
        Assert.NotNull(driver);
        Assert.Contains("WaveOut", driver.Name);
    }

    [Fact(DisplayName = "驱动枚举设备返回至少默认设备")]
    public void EnumerateDevices_ReturnsAtLeastDefault()
    {
        var driver = AudioDriverFactory.Create();
        var devices = driver.EnumerateDevices();
        Assert.NotEmpty(devices);
    }

    [Fact(DisplayName = "驱动创建播放器成功")]
    public void CreatePlayer_ReturnsValidPlayer()
    {
        var driver = AudioDriverFactory.Create();
        var player = driver.CreatePlayer();
        Assert.NotNull(player);
        player.Dispose();
    }

    [Fact(DisplayName = "驱动创建录制器成功")]
    public void CreateRecorder_ReturnsValidRecorder()
    {
        var driver = AudioDriverFactory.Create();
        var recorder = driver.CreateRecorder();
        Assert.NotNull(recorder);
        recorder.Dispose();
    }

    [Fact(DisplayName = "AudioDeviceManager注册驱动后可用")]
    public void DeviceManager_RegisterDriver()
    {
        var manager = new AudioDeviceManager();
        var driver = AudioDriverFactory.Create();
        manager.RegisterDriver(driver);
        Assert.NotNull(manager.ActiveDriver);
    }
}
