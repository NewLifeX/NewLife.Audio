using System;
using System.Linq;
using System.Threading;
using NewLife.Audio;
using NewLife.Audio.Devices;
using NewLife.Audio.DSP;
using Xunit;

namespace XUnitTest.Devices;

/// <summary>音频硬件测试集合：独占音频端点，避免多个采集用例并行争抢麦克风</summary>
[CollectionDefinition("AudioHardware", DisableParallelization = true)]
public class AudioHardwareCollection
{
}

/// <summary>真机硬件通路测试：无音频端点（CI 容器）时自动跳过，不做失败断言</summary>
/// <remarks>
/// 这些用例在开发机（有声卡）上验证「真的把数据送进/取出驱动」，
/// 在无声卡的 CI 机器上因取不到端点或无数据而静默返回，避免把环境缺失当缺陷。
/// </remarks>
[Collection("AudioHardware")]
public class DeviceHardwareTests
{
    private static AudioFormat Format => new() { SampleRate = 48000, Channels = 2, BitsPerSample = 16 };

    private static Byte[] BuildTone(Double seconds)
    {
        var format = Format;
        var frames = (Int32)(format.SampleRate * seconds);
        var data = new Byte[frames * format.Channels * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (Int16)(10000 * Math.Sin(2 * Math.PI * 440 * i / format.SampleRate));
            for (var c = 0; c < format.Channels; c++)
            {
                data[(i * format.Channels + c) * 2] = (Byte)(v & 0xFF);
                data[(i * format.Channels + c) * 2 + 1] = (Byte)((v >> 8) & 0xFF);
            }
        }

        return data;
    }

    [Fact(DisplayName = "WaveOut真机播放能把缓冲区写空")]
    public void WaveOut_RealDevice_PlaybackDrains()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "仅 Windows 有 WaveOut 后端");

        var driver = AudioDriverFactory.Create("waveout");
        using var player = driver.CreatePlayer();
        player.Init(Format, null);
        player.Play();
        player.Write(BuildTone(0.3));

        // 写入后应立即统计到待播放数据；为空说明数据没有进入驱动
        Assert.True(player.BufferedBytes > 0);

        // 等待播放线程消化缓冲；无声卡时 BufferedBytes 会停在原值，视为环境缺失
        for (var i = 0; i < 100 && player.BufferedBytes > 0; i++) Thread.Sleep(50);

        Assert.SkipWhen(player.BufferedBytes > 0, "端点未消化缓冲（无声卡或设备被占用）");
        player.Stop();
    }

    [Fact(DisplayName = "WASAPI共享模式真机播放能把缓冲区写空")]
    public void Wasapi_RealDevice_PlaybackDrains()
    {
        Assert.SkipUnless(WasapiDriver.IsPlatformSupported, "仅 Windows 有 WASAPI 后端");

        var driver = AudioDriverFactory.Create("wasapi");
        using var player = driver.CreatePlayer();
        player.Init(Format, null);
        player.Play();
        player.Write(BuildTone(0.3));

        // 写入后应立即可见待播放数据
        Assert.True(player.BufferedBytes > 0);

        for (var i = 0; i < 100 && player.BufferedBytes > 0; i++) Thread.Sleep(50);

        Assert.SkipWhen(player.BufferedBytes > 0, "端点未消化缓冲（无声卡或设备被占用）");
        player.Stop();
    }

    [Fact(DisplayName = "默认驱动真机持续采集麦克风数据")]
    public void DefaultDriver_RealDevice_InputCaptures()
    {
        Assert.SkipUnless(AudioDriverFactory.IsWindows, "仅 Windows 有 WaveIn 后端");

        var driver = AudioDriverFactory.Create();
        var input = driver.GetDefaultInputDevice();
        Assert.SkipWhen(input == null, "本机没有输入设备");

        // 16k 单声道是麦克风最通用的采集格式
        var format = new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 };
        using var recorder = driver.CreateRecorder();
        var bytes = 0L;
        recorder.DataAvailable += (s, data) => Interlocked.Add(ref bytes, data.Length);
        recorder.Init(format, input.Id);

        try
        {
            recorder.StartRecording();
        }
        catch (InvalidOperationException ex)
        {
            Assert.Skip($"无法打开输入设备：{ex.Message}");
            return;
        }

        // 条件轮询等待首个数据块，最多 5 秒
        for (var i = 0; i < 100 && Interlocked.Read(ref bytes) == 0; i++) Thread.Sleep(50);
        Assert.SkipWhen(Interlocked.Read(ref bytes) == 0, "没有拿到采集数据（无输入源或端点静默）");

        // 预投递的 4 个缓冲播完之后必须继续产出：
        // 曾经在重投递时把 WHDR_PREPARED 一起清掉，waveInAddBuffer 失败，采完首轮 80ms 就静默停采
        var first = Interlocked.Read(ref bytes);
        for (var i = 0; i < 40 && Interlocked.Read(ref bytes) <= first; i++) Thread.Sleep(50);
        var total = Interlocked.Read(ref bytes);
        recorder.StopRecording();

        Assert.True(total > first, "首轮缓冲之后不再产出数据，采集未能持续");
    }

    [Fact(DisplayName = "WASAPI loopback回采能拿到系统声音")]
    public void Wasapi_RealDevice_LoopbackCaptureGetsData()
    {
        Assert.SkipUnless(WasapiDriver.IsPlatformSupported, "仅 Windows 有 WASAPI 后端");

        var driver = new WasapiDriver();
        using var recorder = driver.CreateLoopbackRecorder();
        var bytes = 0L;
        recorder.DataAvailable += (s, data) => Interlocked.Add(ref bytes, data.Length);
        recorder.Init(Format, null);
        recorder.StartRecording();

        using var player = driver.CreatePlayer();
        player.Init(Format, null);
        player.Play();
        player.Write(BuildTone(0.5));
        Thread.Sleep(600);
        player.Stop();
        recorder.StopRecording();

        // 无音频端点的环境拿不到数据，跳过而非失败
        Assert.SkipWhen(bytes == 0, "没有拿到采集数据（无输入源或端点静默）");
        Assert.True(bytes > 0);
    }

    [Fact(DisplayName = "WASAPI默认输入设备采集通路可用")]
    public void Wasapi_RealDevice_DefaultInputCaptures()
    {
        Assert.SkipUnless(WasapiDriver.IsPlatformSupported, "仅 Windows 有 WASAPI 后端");

        var driver = new WasapiDriver();
        var input = driver.EnumerateDevices().FirstOrDefault(d => d.IsInput);
        Assert.SkipWhen(input == null, "本机没有输入设备");

        using var recorder = driver.CreateRecorder();
        var bytes = 0L;
        recorder.DataAvailable += (s, data) => Interlocked.Add(ref bytes, data.Length);
        recorder.Init(Format, input.Id);
        recorder.StartRecording();

        // 条件轮询等待首个数据块，最多 5 秒
        for (var i = 0; i < 100 && Interlocked.Read(ref bytes) == 0; i++) Thread.Sleep(50);
        recorder.StopRecording();

        Assert.SkipWhen(bytes == 0, "没有拿到采集数据（无输入源或端点静默）");
        Assert.True(bytes > 0);
    }

    [Fact(DisplayName = "WASAPI采集在格式转换下仍能产出数据")]
    public void Wasapi_RealDevice_FormatConversionCaptures()
    {
        Assert.SkipUnless(WasapiDriver.IsPlatformSupported, "仅 Windows 有 WASAPI 后端");

        var driver = new WasapiDriver();
        var input = driver.EnumerateDevices().FirstOrDefault(d => d.IsInput);
        Assert.SkipWhen(input == null, "本机没有输入设备");

        // 先用端点友好格式确认采集通路本身可用；拿不到数据视为环境缺失
        var native = CaptureBytes(driver, Format, input.Id);
        Assert.SkipWhen(native <= 0, "没有拿到采集数据（无输入源、端点静默或设备被占用）");

        // 44.1k 单声道通常与端点混音格式不同，覆盖声道映射 + 重采样路径。
        // 曾经映射后仍按源声道数重采样而越界，转换路径完全拿不到数据，属代码缺陷，这里不跳过
        var converted = CaptureBytes(driver, new AudioFormat { SampleRate = 44100, Channels = 1, BitsPerSample = 16 }, input.Id);
        Assert.True(converted > 0, "格式转换路径没有产出数据");
    }

    /// <summary>用指定格式采集一段时间，返回拿到的字节数；设备打不开时返回 -1</summary>
    /// <param name="driver">音频驱动</param>
    /// <param name="format">采集格式</param>
    /// <param name="deviceId">输入设备编号</param>
    /// <param name="waitMs">等待首个数据块的超时（毫秒）</param>
    /// <returns>采集到的字节数</returns>
    private static Int64 CaptureBytes(IAudioDriver driver, AudioFormat format, String deviceId, Int32 waitMs = 3000)
    {
        using var recorder = driver.CreateRecorder();
        var bytes = 0L;
        recorder.DataAvailable += (s, data) => Interlocked.Add(ref bytes, data.Length);
        recorder.Init(format, deviceId);

        try
        {
            recorder.StartRecording();
        }
        catch (InvalidOperationException)
        {
            return -1;
        }

        for (var i = 0; i < waitMs / 50 && Interlocked.Read(ref bytes) == 0; i++) Thread.Sleep(50);
        recorder.StopRecording();

        return bytes;
    }

    [Fact(DisplayName = "设备热插拔观察器注册成功并返回活动端点")]
    public void DeviceWatcher_RegistersAndListsActiveEndpoints()
    {
        using var watcher = new AudioDeviceWatcher();
        Assert.SkipUnless(watcher.IsSupported, "本机不支持设备通知");

        Assert.True(watcher.IsRegistered);

        var render = watcher.GetRenderDeviceIds();
        var capture = watcher.GetCaptureDeviceIds();
        Assert.NotNull(render);
        Assert.NotNull(capture);

        // 有播放端点的机器上，活动端点列表必须非空
        var outputs = new WasapiDriver().EnumerateDevices().Count(d => !d.IsInput);
        if (outputs > 0) Assert.NotEmpty(render);
    }
}
