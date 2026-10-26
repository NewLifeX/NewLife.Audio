using System;
using System.Linq;
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
        if (!AudioDriverFactory.IsSupported) return;

        Assert.Contains("WaveOut", driver.Name);
    }

    [Fact(DisplayName = "驱动枚举设备：有后端时返回设备，无后端时返回空")]
    public void EnumerateDevices_ReturnsAtLeastDefault()
    {
        var driver = AudioDriverFactory.Create();
        var devices = driver.EnumerateDevices();

        if (AudioDriverFactory.IsSupported)
            Assert.NotEmpty(devices);
        else
            Assert.Empty(devices);
    }

    [Fact(DisplayName = "驱动创建播放器：无后端时明确抛出平台不支持")]
    public void CreatePlayer_ReturnsValidPlayer()
    {
        var driver = AudioDriverFactory.Create();

        if (!AudioDriverFactory.IsSupported)
        {
            Assert.Throws<PlatformNotSupportedException>(() => driver.CreatePlayer());
            return;
        }

        using var player = driver.CreatePlayer();
        Assert.NotNull(player);
    }

    [Fact(DisplayName = "驱动创建录制器：无后端时明确抛出平台不支持")]
    public void CreateRecorder_ReturnsValidRecorder()
    {
        var driver = AudioDriverFactory.Create();

        if (!AudioDriverFactory.IsSupported)
        {
            Assert.Throws<PlatformNotSupportedException>(() => driver.CreateRecorder());
            return;
        }

        using var recorder = driver.CreateRecorder();
        Assert.NotNull(recorder);
    }

    [Fact(DisplayName = "默认驱动路径录音器是真实采集器而非空实现")]
    public void DefaultDriver_Recorder_IsRealCapture()
    {
        var driver = AudioDriverFactory.Create();

        if (!AudioDriverFactory.IsSupported) return;

        using var recorder = driver.CreateRecorder();
        Assert.Equal("WaveInRecorder", recorder.GetType().Name);
    }

    [Fact(DisplayName = "默认驱动路径播放器是真实输出而非空实现")]
    public void DefaultDriver_Player_IsRealOutput()
    {
        var driver = AudioDriverFactory.Create();

        if (!AudioDriverFactory.IsSupported) return;

        using var player = driver.CreatePlayer();
        Assert.Equal("WaveOutPlayer", player.GetType().Name);
    }

    [Fact(DisplayName = "工厂按运行时平台上报后端能力")]
    public void Factory_Reports_Backends()
    {
        var backends = AudioDriverFactory.GetAvailableBackends();

        Assert.Equal(AudioDriverFactory.IsSupported, backends.Count > 0);
        if (!AudioDriverFactory.IsSupported) return;

        Assert.Contains("waveout", backends);
        Assert.Contains("wasapi", backends);
    }

    [Fact(DisplayName = "AudioDeviceManager注册驱动后可用")]
    public void DeviceManager_RegisterDriver()
    {
        var manager = new AudioDeviceManager();
        var driver = AudioDriverFactory.Create();
        manager.RegisterDriver(driver);
        Assert.NotNull(manager.ActiveDriver);
    }

    [Fact(DisplayName = "AudioDeviceManager无驱动时明确报告不可用")]
    public void DeviceManager_NoDriver_Throws()
    {
        var manager = new AudioDeviceManager();

        Assert.Empty(manager.EnumerateDevices());
        Assert.Throws<InvalidOperationException>(() => manager.CreatePlayer());
        Assert.Throws<InvalidOperationException>(() => manager.CreateRecorder());
    }
}
