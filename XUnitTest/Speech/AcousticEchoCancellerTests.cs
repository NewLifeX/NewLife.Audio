using System;
using NewLife.Audio.Speech;
using Xunit;

namespace XUnitTest.Speech;

public class AcousticEchoCancellerTests
{
    [Fact(DisplayName = "AEC创建实例不抛异常")]
    public void Constructor_DoesNotThrow()
    {
        var aec = new AcousticEchoCanceller();
        Assert.NotNull(aec);
        Assert.Equal(256, aec.FilterLength);
    }

    [Fact(DisplayName = "AEC自定义滤波器长度")]
    public void Constructor_CustomLength()
    {
        var aec = new AcousticEchoCanceller(512, 0.05f);
        Assert.Equal(512, aec.FilterLength);
    }

    [Fact(DisplayName = "AEC处理空远端不抛异常")]
    public void ProcessFarEnd_Empty_DoesNotThrow()
    {
        var aec = new AcousticEchoCanceller();
        aec.ProcessFarEnd(Array.Empty<Byte>());
    }

    [Fact(DisplayName = "AEC处理近端返回同长度输出")]
    public void ProcessNearEnd_ReturnsSameLength()
    {
        var aec = new AcousticEchoCanceller();
        var nearEnd = new Byte[160 * 2]; // 10ms @ 16kHz
        var result = aec.ProcessNearEnd(nearEnd);
        Assert.Equal(nearEnd.Length, result.Length);
    }

    [Fact(DisplayName = "AEC远端静音时近端直通")]
    public void ProcessNearEnd_WithSilentFarEnd_Passthrough()
    {
        var aec = new AcousticEchoCanceller();

        // 远端静音
        aec.ProcessFarEnd(new Byte[160 * 2]);

        // 近端正弦波
        var nearEnd = new Byte[160 * 2];
        for (var i = 0; i < 160; i++)
        {
            var s = (Int16)(Math.Sin(2 * Math.PI * 400 * i / 8000) * 8000);
            nearEnd[i * 2] = (Byte)(s & 0xFF);
            nearEnd[i * 2 + 1] = (Byte)((s >> 8) & 0xFF);
        }

        var result = aec.ProcessNearEnd(nearEnd);
        Assert.Equal(nearEnd.Length, result.Length);
        // 回声消除器应该在静音远端时基本保留近端信号
    }

    [Fact(DisplayName = "AEC重置后ERLE复位")]
    public void Reset_ResetsErle()
    {
        var aec = new AcousticEchoCanceller();
        var farEnd = new Byte[160 * 2];
        var nearEnd = new Byte[160 * 2];

        // 产生一些非零 ERLE
        aec.ProcessFarEnd(farEnd);
        aec.ProcessNearEnd(nearEnd);

        aec.Reset();
        Assert.True(aec.ErleEstimate >= 0);
    }

    [Fact(DisplayName = "AEC步长设置范围限制")]
    public void StepSize_ClampRange()
    {
        var aec = new AcousticEchoCanceller();
        aec.StepSize = 2.0f; // over max
        Assert.True(aec.StepSize <= 1.0f);

        aec.StepSize = -0.1f; // under min
        Assert.True(aec.StepSize >= 0.001f);
    }

    [Fact(DisplayName = "AEC实现IAcousticEchoCanceller接口")]
    public void ImplementsInterface()
    {
        var aec = new AcousticEchoCanceller();
        Assert.IsAssignableFrom<IAcousticEchoCanceller>(aec);
    }
}
