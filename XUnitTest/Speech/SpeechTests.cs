using System;
using NewLife.Audio.Speech;
using Xunit;

namespace XUnitTest.Speech;

public class VadTests
{
    [Fact(DisplayName = "VAD静音检测返回false")]
    public void Vad_Silence_ReturnsFalse()
    {
        var vad = new GmmVad(8000, 1);
        var silence = new Byte[160 * 2]; // 10ms 全零 PCM
        Assert.False(vad.IsSpeech(silence));
    }

    [Fact(DisplayName = "VAD灵敏度0最保守")]
    public void Vad_Aggressiveness0_Conservative()
    {
        var vad = new GmmVad(8000, 0);
        Assert.Equal(0, vad.Aggressiveness);
    }

    [Fact(DisplayName = "VAD灵敏度3最激进")]
    public void Vad_Aggressiveness3_Aggressive()
    {
        var vad = new GmmVad(8000, 3);
        Assert.Equal(3, vad.Aggressiveness);
    }

    [Fact(DisplayName = "VAD Reset后状态正确")]
    public void Vad_Reset_ClearsState()
    {
        var vad = new GmmVad();
        var silence = new Byte[160 * 2];
        vad.IsSpeech(silence);
        vad.Reset();
        Assert.False(vad.IsSpeech(silence));
    }
}

public class AgcTests
{
    [Fact(DisplayName = "AGC默认目标电平-18dBFS")]
    public void Agc_DefaultTargetLevel()
    {
        var agc = new AutomaticGainControl();
        var targetDB = agc.TargetLevelDB;
        Assert.True(Math.Abs(targetDB + 18f) < 0.1f, $"actual={targetDB}");
    }

    [Fact(DisplayName = "AGC启音时间10ms")]
    public void Agc_AttackTime_10ms()
    {
        var agc = new AutomaticGainControl();
        Assert.True(Math.Abs(agc.AttackMs - 10f) < 1f);
    }

    [Fact(DisplayName = "AGC最大增益20dB")]
    public void Agc_MaxGain_20dB()
    {
        var agc = new AutomaticGainControl();
        Assert.True(agc.MaxGain >= 10f); // 20dB = 10x
    }
}

public class VoicePreprocessorTests
{
    [Fact(DisplayName = "VoicePreprocessor默认启用VAD和AGC")]
    public void Preprocessor_Defaults_Enabled()
    {
        var vp = new VoicePreprocessor(8000);
        Assert.True(vp.EnableVAD);
        Assert.True(vp.EnableAGC);
        Assert.True(vp.SilenceOnVad);
    }

    [Fact(DisplayName = "VoicePreprocessor可配置VAD灵敏度")]
    public void Preprocessor_VadAggressiveness_Settable()
    {
        var vp = new VoicePreprocessor(8000);
        vp.VadAggressiveness = 3;
        Assert.Equal(3, vp.VadAggressiveness);
    }
}
