using System;
using NewLife.Audio.Devices;
using Xunit;

namespace XUnitTest.Devices;

public class AsioBackendTests
{
    [Fact(DisplayName = "ASIO后端创建成功")]
    public void AsioBackend_CreatesInstances()
    {
        var backend = AudioBackendFactory.Create("asio");
        Assert.NotNull(backend);
        Assert.Contains("ASIO", backend.Name);
    }
}
