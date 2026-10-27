namespace NewLife.Audio.Midi;

/// <summary>标准 MIDI 文件格式</summary>
public enum MidiFileFormat
{
    /// <summary>格式 0：所有事件合并在一条轨道</summary>
    SingleTrack = 0,

    /// <summary>格式 1：多条轨道同步播放</summary>
    MultiTrack = 1,

    /// <summary>格式 2：多条互相独立的序列</summary>
    MultiSong = 2,
}
