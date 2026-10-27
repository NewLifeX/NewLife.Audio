using System;
using NewLife.Audio.Devices;
using Xunit;

namespace XUnitTest.Devices;

public class AsioDriverTests
{
    [Fact(DisplayName = "ASIO驱动创建成功")]
    public void AsioDriver_CreatesInstances()
    {
        var driver = AudioDriverFactory.Create("asio");
        Assert.NotNull(driver);

        // 工厂在非 Windows 平台忽略驱动名，返回该平台的默认后端（Linux 为 ALSA）
        if (!AudioDriverFactory.IsWindows) return;

        Assert.Contains("ASIO", driver.Name);
    }

    [Fact(DisplayName = "ASIO未实现时创建设备明确报告不支持")]
    public void AsioDriver_Unimplemented_Throws()
    {
        Assert.False(AsioDriver.IsSupported);

        var driver = new AsioDriver();
        Assert.Empty(driver.EnumerateDevices());
        Assert.Null(driver.GetDefaultOutputDevice());
        Assert.Null(driver.GetDefaultInputDevice());
        Assert.Throws<NotSupportedException>(() => driver.CreatePlayer());
        Assert.Throws<NotSupportedException>(() => driver.CreateRecorder());
    }
}
