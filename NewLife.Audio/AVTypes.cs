namespace NewLife.Audio;

/// <summary>消息类型</summary>
public enum AVTypes : Byte
{
    G721 = 1,
    G722 = 2,
    G723 = 3,
    G728 = 4,
    G729 = 5,
    G711A = 6,
    G711U = 7,
    G726 = 8,
    G729A = 9,

    DVI4_3 = 10,
    DVI4_4 = 11,
    DVI4_8K = 12,
    DVI4_16K = 13,

    LPC = 14,
    S16BE_STEREO = 15,
    S16BE_MONO = 16,
    MPEGAUDIO = 17,
    LPCM = 18,
    AAC = 19,
    WMA9STD = 20,
    HEAAC = 21,
    PCM_VOICE = 22,
    PCM_AUDIO = 23,
    AACLC = 24,
    MP3 = 25,
    ADPCMA = 26,
    MP4AUDIO = 27,
    AMR = 28,

    Transparent = 91,

    H264 = 98,
    H265 = 99,
    AVS = 100,
    SVAC = 101,
}