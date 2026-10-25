using System;
using NewLife.Audio.Devices;
using Xunit;

namespace XUnitTest.Devices;

public class WasapiDriverTests
{
    [Fact(DisplayName = "WASAPI驱动创建成功")]
    public void WasapiDriver_CreatesInstances()
    {
        var driver = AudioDriverFactory.Create("wasapi");
        Assert.NotNull(driver);
        Assert.Contains("WASAPI", driver.Name);

        var player = driver.CreatePlayer();
        Assert.NotNull(player);
        player.Dispose();

        var recorder = driver.CreateRecorder();
        Assert.NotNull(recorder);
        recorder.Dispose();
    }
}
