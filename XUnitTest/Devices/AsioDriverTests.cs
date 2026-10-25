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
        Assert.Contains("ASIO", driver.Name);
    }
}
