using NewLife.Audio.DSP;
using NewLife.Audio.Tts;
using Xunit;

namespace XUnitTest.Tts;

/// <summary>选项、音色与结果模型</summary>
public class TtsModelsTests
{
    [Fact(DisplayName = "选项默认语速与音量为 1")]
    public void Options_Defaults()
    {
        var options = new TtsOptions();

        Assert.Null(options.Voice);
        Assert.Equal(1f, options.Rate);
        Assert.Equal(1f, options.Volume);
    }

    [Fact(DisplayName = "克隆选项得到独立副本")]
    public void Options_Clone_IsIndependent()
    {
        var source = new TtsOptions { Voice = "v1", Rate = 1.5f, Volume = 0.5f };

        var clone = source.Clone();
        clone.Voice = "v2";
        clone.Rate = 2f;

        Assert.Equal("v1", source.Voice);
        Assert.Equal(1.5f, source.Rate);
        Assert.Equal(0.5f, source.Volume);
        Assert.Equal("v2", clone.Voice);
        Assert.Equal(2f, clone.Rate);
        Assert.Equal(0.5f, clone.Volume);
    }

    [Fact(DisplayName = "音色描述包含名称与区域")]
    public void Voice_ToString()
    {
        var voice = new TtsVoice { Name = "Huihui", Culture = "zh-CN", Gender = "Female" };

        Assert.Equal("Huihui(zh-CN)", voice.ToString());
    }

    [Fact(DisplayName = "音频时长按字节率换算")]
    public void Result_AudioTime_FromByteRate()
    {
        var result = new TtsResult
        {
            Format = new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 },
            Bytes = 32000,
        };

        Assert.Equal(1.0, result.AudioTime, 6);
    }

    [Fact(DisplayName = "缺少格式或数据时时长为零")]
    public void Result_AudioTime_ZeroWhenMissing()
    {
        Assert.Equal(0, new TtsResult().AudioTime);
        Assert.Equal(0, new TtsResult { Format = AudioFormat.Default }.AudioTime);

        var result = new TtsResult { Format = new AudioFormat { SampleRate = 16000 }, Bytes = 0 };
        Assert.Equal(0, result.AudioTime);
    }

    [Fact(DisplayName = "实时率等于合成耗时除以音频时长")]
    public void Result_Rtf()
    {
        var result = new TtsResult
        {
            Format = new AudioFormat { SampleRate = 16000, Channels = 1, BitsPerSample = 16 },
            Bytes = 32000,
            ElapsedTime = 0.5,
        };

        Assert.Equal(0.5, result.Rtf, 6);
        Assert.Equal(0, new TtsResult { ElapsedTime = 1 }.Rtf);
    }

    [Fact(DisplayName = "结果描述包含采样率、时长与实时率")]
    public void Result_ToString()
    {
        var result = new TtsResult
        {
            Engine = "fake",
            Format = new AudioFormat { SampleRate = 22050, Channels = 1, BitsPerSample = 16 },
            Bytes = 44100,
            ElapsedTime = 0.25,
        };

        var text = result.ToString();

        Assert.Contains("fake", text);
        Assert.Contains("22050Hz", text);
        Assert.Contains("RTF=", text);
    }
}
