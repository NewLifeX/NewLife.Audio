using System;

namespace NewLife.Audio.Midi;

/// <summary>MIDI 渲染选项</summary>
public sealed class MidiRenderOptions
{
    /// <summary>采样率（Hz）</summary>
    public Int32 SampleRate { get; set; } = 44100;

    /// <summary>声道数（1 或 2）</summary>
    public Int32 Channels { get; set; } = 2;

    /// <summary>主音量（0~1）</summary>
    public Double MasterVolume { get; set; } = 0.8;

    /// <summary>最大复音数</summary>
    public Int32 MaxPolyphony { get; set; } = 64;

    /// <summary>末尾释放尾巴（秒），避免最后一个音符被截断</summary>
    public Double ReleaseTail { get; set; } = 0.5;

    /// <summary>采样音源；为 null 时使用内置波形合成</summary>
    /// <remarks>可传入 <see cref="SoundFont"/> 或 <see cref="DlsSoundBank"/>，按 GM bank/program 选择采样。</remarks>
    public SoundBank SoundBank { get; set; }
}
