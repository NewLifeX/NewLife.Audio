using System;
using NewLife.Audio.Devices;
using Xunit;

namespace XUnitTest.Devices;

public class WasapiBackendTests
{
    [Fact(DisplayName = "WASAPI后端创建成功")]
    public void WasapiBackend_CreatesInstances()
    {
        var backend = AudioBackendFactory.Create("wasapi");
        Assert.NotNull(backend);
        Assert.Contains("WASAPI", backend.Name);

        var player = backend.CreatePlayer();
        Assert.NotNull(player);
        player.Dispose();

        var recorder = backend.CreateRecorder();
        Assert.NotNull(recorder);
        recorder.Dispose();
    }
}
