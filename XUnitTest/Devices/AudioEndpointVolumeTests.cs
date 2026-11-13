using System;
using System.Linq;
using NewLife.Audio.Devices;
using Xunit;

namespace XUnitTest.Devices;

/// <summary>端点音量与设备元数据测试：无声卡的 CI 机器自动跳过</summary>
/// <remarks>
/// 端点音量直接作用于系统音量合成器，测试只做「读原值→写回原值」与「静音开→关还原」，
/// 不改变用户当前的音量与静音设置。
/// </remarks>
[Collection("AudioHardware")]
public class AudioEndpointVolumeTests
{
    [Fact(DisplayName = "WASAPI枚举带回混音格式与接口友好名")]
    public void DeviceInfo_ReportsMixFormatAndFriendlyName()
    {
        Assert.SkipUnless(WasapiDriver.IsPlatformSupported, "仅 Windows 有 WASAPI 后端");

        var devices = new WasapiDriver().EnumerateDevices();
        Assert.SkipWhen(devices.Count == 0, "本机没有活动音频端点");

        foreach (var device in devices)
        {
            Assert.NotNull(device.MixFormat);
            Assert.True(device.MixFormat.SampleRate > 0);
            Assert.True(device.MixFormat.Channels > 0);
            Assert.True(device.MixFormat.BitsPerSample > 0);
        }
    }

    [Fact(DisplayName = "端点音量可读可写且能还原")]
    public void EndpointVolume_ReadWrite_RestoresState()
    {
        Assert.SkipUnless(AudioEndpointVolume.IsSupported, "仅 Windows 支持端点音量控制");

        var output = new WasapiDriver().EnumerateDevices().FirstOrDefault(d => !d.IsInput);
        Assert.SkipWhen(output == null, "本机没有播放端点");

        using var volume = AudioEndpointVolume.Open(output.Id);

        var original = volume.Volume;
        Assert.InRange(original, 0f, 1f);

        // 写回原值：验证写通路而不改变系统音量
        volume.Volume = original;
        Assert.Equal(original, volume.Volume, 2);

        // 越界值按 0~1 截断
        volume.Volume = 1.5f;
        Assert.Equal(1f, volume.Volume, 2);
        volume.Volume = original;
        Assert.Equal(original, volume.Volume, 2);
    }

    [Fact(DisplayName = "端点静音可开关且能还原")]
    public void EndpointMute_TogglesAndRestores()
    {
        Assert.SkipUnless(AudioEndpointVolume.IsSupported, "仅 Windows 支持端点音量控制");

        var output = new WasapiDriver().EnumerateDevices().FirstOrDefault(d => !d.IsInput);
        Assert.SkipWhen(output == null, "本机没有播放端点");

        using var volume = AudioEndpointVolume.Open(output.Id);

        var original = volume.Mute;

        volume.Mute = !original;
        Assert.Equal(!original, volume.Mute);

        volume.Mute = original;
        Assert.Equal(original, volume.Mute);
    }

    [Fact(DisplayName = "打开不存在的端点抛出明确异常")]
    public void Open_UnknownDevice_Throws()
    {
        Assert.SkipUnless(AudioEndpointVolume.IsSupported, "仅 Windows 支持端点音量控制");

        var ex = Assert.Throws<InvalidOperationException>(() => AudioEndpointVolume.Open("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}"));
        Assert.Contains("not found", ex.Message);
    }
}
