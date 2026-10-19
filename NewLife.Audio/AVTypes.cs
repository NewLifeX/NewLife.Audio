namespace NewLife.Audio;

/// <summary>音视频编码类型（RTP/GB28181 载荷类型）</summary>
public enum AVTypes : Byte
{
    /// <summary>G.721 32 kbps ADPCM 音频</summary>
    G721 = 1,
    /// <summary>G.722 64 kbps 宽带音频</summary>
    G722 = 2,
    /// <summary>G.723 多速率语音编码</summary>
    G723 = 3,
    /// <summary>G.728 低延迟码激励线性预测音频</summary>
    G728 = 4,
    /// <summary>G.729 8 kbps CS-ACELP 语音</summary>
    G729 = 5,
    /// <summary>G.711 A-law 音频</summary>
    G711A = 6,
    /// <summary>G.711 μ-law 音频</summary>
    G711U = 7,
    /// <summary>G.726 ADPCM 音频</summary>
    G726 = 8,
    /// <summary>G.729 Annex A 语音</summary>
    G729A = 9,

    /// <summary>DVI4 ADPCM 3 kHz 音频</summary>
    DVI4_3 = 10,
    /// <summary>DVI4 ADPCM 4 kHz 音频</summary>
    DVI4_4 = 11,
    /// <summary>DVI4 ADPCM 8 kHz 音频</summary>
    DVI4_8K = 12,
    /// <summary>DVI4 ADPCM 16 kHz 音频</summary>
    DVI4_16K = 13,

    /// <summary>LPC 线性预测音频</summary>
    LPC = 14,
    /// <summary>16-bit 大端立体声 PCM</summary>
    S16BE_STEREO = 15,
    /// <summary>16-bit 大端单声道 PCM</summary>
    S16BE_MONO = 16,
    /// <summary>MPEG 音频</summary>
    MPEGAUDIO = 17,
    /// <summary>线性 PCM</summary>
    LPCM = 18,
    /// <summary>AAC 音频</summary>
    AAC = 19,
    /// <summary>Windows Media Audio 9 标准音频</summary>
    WMA9STD = 20,
    /// <summary>HE-AAC 音频</summary>
    HEAAC = 21,
    /// <summary>语音 PCM</summary>
    PCM_VOICE = 22,
    /// <summary>音频 PCM</summary>
    PCM_AUDIO = 23,
    /// <summary>AAC-LC 音频</summary>
    AACLC = 24,
    /// <summary>MP3 音频</summary>
    MP3 = 25,
    /// <summary>IMA ADPCM 音频</summary>
    ADPCMA = 26,
    /// <summary>MP4 音频</summary>
    MP4AUDIO = 27,
    /// <summary>AMR 语音</summary>
    AMR = 28,

    /// <summary>透明传输</summary>
    Transparent = 91,

    /// <summary>H.264 视频</summary>
    H264 = 98,
    /// <summary>H.265 视频</summary>
    H265 = 99,
    /// <summary>AVS 视频</summary>
    AVS = 100,
    /// <summary>SVAC 视频</summary>
    SVAC = 101,
}