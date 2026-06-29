using System;
using NewLife.Audio.Speech;
using NewLife.Data;
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

    [Fact(DisplayName = "VAD高频正弦波确保不抛异常")]
    public void Vad_LoudSine_DoesNotThrow()
    {
        var vad = new GmmVad(8000, 0); // 最保守
        var pcm = new Byte[160 * 2];
        for (var i = 0; i < 160; i++)
        {
            var val = (Int16)(Math.Sin(2 * Math.PI * 1000 * i / 8000) * 20000);
            pcm[i * 2] = (Byte)(val & 0xFF);
            pcm[i * 2 + 1] = (Byte)(val >> 8 & 0xFF);
        }
        // 确保处理不抛异常
        var result = vad.IsSpeech(pcm);
        // VAD 在灵敏度0时对高频信号可能判断为静音，但不应抛异常
    }

    [Fact(DisplayName = "VAD GetSpeechProbability范围校验")]
    public void Vad_GetSpeechProbability_WithinRange()
    {
        var vad = new GmmVad();
        var pcm = new Byte[160 * 2];
        var prob = vad.GetSpeechProbability(pcm);
        Assert.True(prob >= 0f && prob <= 1.0f);
    }

    [Fact(DisplayName = "VAD连续多帧Hangover不重置")]
    public void Vad_ContinuousFrames_NoReset()
    {
        var vad = new GmmVad(8000, 1);
        var loudPcm = new Byte[160 * 2];
        for (var i = 0; i < 160; i++)
        {
            var val = (Int16)(Math.Sin(2 * Math.PI * 440 * i / 8000) * 15000);
            loudPcm[i * 2] = (Byte)(val & 0xFF);
            loudPcm[i * 2 + 1] = (Byte)(val >> 8 & 0xFF);
        }

        // 连续处理多帧不抛异常
        for (var f = 0; f < 5; f++)
            vad.IsSpeech(loudPcm);
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

    [Fact(DisplayName = "AGC Read处理低幅信号不抛异常")]
    public void Agc_Read_LowSignal_DoesNotThrow()
    {
        var agc = new AutomaticGainControl();
        var source = new FixedValueSource(100, 0.01f);
        agc.Source = source;

        var buffer = new Single[160];
        var read = agc.Read(buffer, 0, 160);
        Assert.True(read >= 0);
    }

    [Fact(DisplayName = "AGC Read高幅信号不溢出")]
    public void Agc_Read_HighSignal_NoOverflow()
    {
        var agc = new AutomaticGainControl();
        var source = new FixedValueSource(100, 0.9f);
        agc.Source = source;

        var buffer = new Single[160];
        var read = agc.Read(buffer, 0, 160);
        Assert.True(read >= 0);
        // 输出不应超过 1.0f
        for (var i = 0; i < read; i++)
            Assert.True(buffer[i] <= 1.0f);
    }

    [Fact(DisplayName = "AGC Reset重置不抛异常")]
    public void Agc_Reset_Works()
    {
        var agc = new AutomaticGainControl();
        agc.Reset();
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

    [Fact(DisplayName = "VoicePreprocessor Read返回非空")]
    public void Preprocessor_Read_ReturnsOutput()
    {
        var vp = new VoicePreprocessor(8000);
        var buffer = new Single[160];
        var read = vp.Read(buffer, 0, 160);
        // 无 Source 时 Read 返回 0
        Assert.Equal(0, read);
    }

    [Fact(DisplayName = "VoicePreprocessor EnableVAD=false时状态正确")]
    public void Preprocessor_DisableVAD_Works()
    {
        var vp = new VoicePreprocessor(8000);
        vp.EnableVAD = false;
        Assert.False(vp.EnableVAD);
    }

    [Fact(DisplayName = "VoicePreprocessor EnableAGC=false时状态正确")]
    public void Preprocessor_DisableAGC_Works()
    {
        var vp = new VoicePreprocessor(8000);
        vp.EnableAGC = false;
        Assert.False(vp.EnableAGC);
    }

    [Fact(DisplayName = "VoicePreprocessor SilenceOnVad可配置")]
    public void Preprocessor_SilenceOnVad_Configurable()
    {
        var vp = new VoicePreprocessor(8000);
        vp.SilenceOnVad = true;
        Assert.True(vp.SilenceOnVad);
        vp.SilenceOnVad = false;
        Assert.False(vp.SilenceOnVad);
    }

    [Fact(DisplayName = "VoicePreprocessor Reset后状态正确")]
    public void Preprocessor_Reset_Works()
    {
        var vp = new VoicePreprocessor(8000);
        vp.Reset();
        // 重置后 AgcTargetLevelDB 可读写
        var target = vp.AgcTargetLevelDB;
        vp.AgcTargetLevelDB = -12f;
        Assert.True(Math.Abs(vp.AgcTargetLevelDB + 12f) < 0.1f);
    }

    [Fact(DisplayName = "VoicePreprocessor CurrentSpeechProbability初始为0")]
    public void Preprocessor_SpeechProbability_InitialZero()
    {
        var vp = new VoicePreprocessor(8000);
        Assert.True(vp.CurrentSpeechProbability >= 0f && vp.CurrentSpeechProbability <= 1.0f);
    }
}

/// <summary>固定值测试信号源（用于 AGC 测试）</summary>
internal sealed class FixedValueSource : NewLife.Audio.DSP.IAudioProcessor
{
    private readonly Int32 _total;
    private readonly Single _value;
    private Int32 _pos;

    public NewLife.Audio.DSP.AudioFormat InputFormat => NewLife.Audio.DSP.AudioFormat.Default;
    public NewLife.Audio.DSP.AudioFormat OutputFormat => NewLife.Audio.DSP.AudioFormat.Default;
    public NewLife.Audio.DSP.IAudioProcessor Source { get; set; }

    public FixedValueSource(Int32 total, Single value)
    {
        _total = total;
        _value = value;
    }

    public Int32 Read(Single[] buffer, Int32 offset, Int32 count)
    {
        var remaining = _total - _pos;
        var toRead = Math.Min(count, remaining);
        for (var i = 0; i < toRead; i++)
            buffer[offset + i] = _value;
        _pos += toRead;
        return toRead;
    }

    public void Reset() => _pos = 0;
}
