using System;
using System.Collections.Generic;

namespace NewLife.Audio.Midi;

/// <summary>采样音源库</summary>
/// <remarks>
/// 统一表示 SoundFont 2（.sf2）与 Downloadable Sounds（.dls）解析后的预设、区域与采样，
/// 供 <see cref="MidiSynthesizer"/> 按 GM bank/program 选择采样音色。
/// </remarks>
public abstract class SoundBank
{
    /// <summary>预设列表</summary>
    public abstract IList<SoundBankPreset> Presets { get; }

    /// <summary>按 bank/program 查找预设</summary>
    /// <param name="bank">音色库编号，GM 旋律为 0，打击乐为 128</param>
    /// <param name="program">音色编号（0~127）</param>
    /// <returns>匹配的预设；找不到时回退到同库 0 号音色，仍找不到则返回第一个预设</returns>
    public virtual SoundBankPreset FindPreset(Int32 bank, Int32 program)
    {
        SoundBankPreset fallback = null;
        foreach (var preset in Presets)
        {
            if (preset.Bank == bank && preset.Program == program) return preset;
            if (preset.Bank == bank && preset.Program == 0) fallback ??= preset;
        }

        return fallback;
    }
}

/// <summary>采样音源预设</summary>
public sealed class SoundBankPreset
{
    /// <summary>预设名称</summary>
    public String Name { get; set; }

    /// <summary>音色库编号</summary>
    public Int32 Bank { get; set; }

    /// <summary>音色编号（0~127）</summary>
    public Int32 Program { get; set; }

    /// <summary>区域列表</summary>
    public IList<SoundBankRegion> Regions { get; } = [];
}

/// <summary>采样音源区域</summary>
/// <remarks>一个区域把一个采样映射到指定的按键与力度范围，并携带包络、调音与声像参数。</remarks>
public sealed class SoundBankRegion
{
    /// <summary>采样</summary>
    public SoundBankSample Sample { get; set; }

    /// <summary>最低按键（0~127）</summary>
    public Int32 KeyLow { get; set; }

    /// <summary>最高按键（0~127）</summary>
    public Int32 KeyHigh { get; set; } = 127;

    /// <summary>最低力度（0~127）</summary>
    public Int32 VelocityLow { get; set; }

    /// <summary>最高力度（0~127）</summary>
    public Int32 VelocityHigh { get; set; } = 127;

    /// <summary>根音按键；-1 表示使用采样自带根音</summary>
    public Int32 RootKey { get; set; } = -1;

    /// <summary>粗调（半音）</summary>
    public Double CoarseTune { get; set; }

    /// <summary>细调（音分）</summary>
    public Double FineTune { get; set; }

    /// <summary>衰减（分贝）</summary>
    public Double Attenuation { get; set; }

    /// <summary>声像（0~127，64 为居中）</summary>
    public Int32 Pan { get; set; } = 64;

    /// <summary>起音秒数</summary>
    public Double Attack { get; set; } = 0.005;

    /// <summary>保持秒数</summary>
    public Double Hold { get; set; }

    /// <summary>衰减秒数</summary>
    public Double Decay { get; set; } = 0.05;

    /// <summary>延音电平（0~1）</summary>
    public Double Sustain { get; set; } = 1.0;

    /// <summary>释放秒数</summary>
    public Double Release { get; set; } = 0.2;

    /// <summary>是否循环</summary>
    public Boolean Loop { get; set; }

    /// <summary>采样起始位置（采样点索引）</summary>
    public Int32 SampleStart { get; set; }

    /// <summary>采样结束位置（不含）</summary>
    public Int32 SampleEnd { get; set; } = -1;

    /// <summary>循环起始位置（采样点索引）</summary>
    public Int32 LoopStart { get; set; } = -1;

    /// <summary>循环结束位置（不含）</summary>
    public Int32 LoopEnd { get; set; } = -1;

    /// <summary>判断区域是否匹配指定按键与力度</summary>
    /// <param name="note">按键（0~127）</param>
    /// <param name="velocity">力度（0~127）</param>
    /// <returns>匹配返回 true</returns>
    public Boolean Matches(Int32 note, Int32 velocity) => note >= KeyLow && note <= KeyHigh && velocity >= VelocityLow && velocity <= VelocityHigh;
}

/// <summary>采样音源采样</summary>
public sealed class SoundBankSample
{
    /// <summary>采样名称</summary>
    public String Name { get; set; }

    /// <summary>单声道浮点采样数据（-1~1）</summary>
    public Single[] Data { get; set; } = [];

    /// <summary>采样率（Hz）</summary>
    public Int32 SampleRate { get; set; } = 44100;

    /// <summary>根音按键</summary>
    public Int32 RootKey { get; set; } = 60;

    /// <summary>音分修正</summary>
    public Double PitchCorrection { get; set; }

    /// <summary>循环起始位置（采样点索引）</summary>
    public Int32 LoopStart { get; set; }

    /// <summary>循环结束位置（不含）</summary>
    public Int32 LoopEnd { get; set; }

    /// <summary>是否循环</summary>
    public Boolean Loop { get; set; }
}
