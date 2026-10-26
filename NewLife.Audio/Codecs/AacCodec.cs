using NewLife;
using NewLife.Data;

namespace NewLife.Audio.Codecs;

/// <summary>AAC (Advanced Audio Coding) 编解码器</summary>
/// <remarks>
/// 解码基于 SharpJaad.AAC（MIT、纯托管）实现 AAC-LC / HE-AAC v1/v2 / 多声道（5.1/7.1）解码；
/// 编码为纯托管 AAC-LC 实现。ADTS / raw+ASC / LOAS-LATM 由本类处理，MP4 容器由容器层处理。
/// </remarks>
public class AacCodec : IAudioCodec, ICodecInfo
{
    // ADTS 头固定 7 或 9 字节（含/不含 CRC）
    private const Int32 AdtsHeaderSize = 7;

    // 采样率表（AAC 标准）
    private static readonly Int32[] SampleRates = [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350, 0, 0, 0,
    ];

    private AacDecoderBackend _decoder;
    private AacEncoderBackend _encoder;
    private AacLatmReader _latmReader;
    private Byte[] _asc;
    private AudioSpecificConfig _ascConfig;
    private Int32 _decoderProfile;
    private Int32 _decoderSampleRateIndex;
    private Int32 _decoderChannels;
    private Int32 _lastChannels;
    private Boolean _adtsKeepStereo;
    private Boolean _psDetected;

    /// <summary>编解码器名称</summary>
    public String Name => "AAC-LC";

    /// <summary>版本号</summary>
    public String Version => "1.0";

    /// <summary>支持的编码类型</summary>
    public IReadOnlyCollection<AVTypes> SupportedTypes { get; } = [AVTypes.AAC, AVTypes.AACLC, AVTypes.HEAAC];

    /// <summary>有状态编解码器</summary>
    public Boolean IsStateful => true;

    /// <summary>最近一次解码的采样率（Hz）。HE-AAC 时为核心采样率的两倍</summary>
    public Int32 SampleRate { get; private set; }

    /// <summary>最近一次解码的声道数。HE-AAC v2 单声道核心经 PS 恢复为立体声</summary>
    public Int32 Channels { get; private set; }

    /// <summary>最近一次解码的位深</summary>
    public Int32 BitsPerSample { get; private set; } = 16;

    /// <summary>最近一次编码的采样率（Hz）</summary>
    public Int32 EncodeSampleRate { get; private set; }

    /// <summary>最近一次编码的声道数</summary>
    public Int32 EncodeChannels { get; private set; }

    /// <summary>当前流是否检测到 HE-AAC v2 参数化立体声（PS）</summary>
    public Boolean IsPsPresent => _decoder?.IsPsPresent == true;

    /// <summary>AAC 数据转 PCM</summary>
    /// <param name="audio">AAC 编码数据（ADTS/LOAS 可含多帧；raw 模式一次一个 access unit）</param>
    /// <param name="option">raw 模式下提供 <see cref="AudioSpecificConfig"/> 或 ASC 字节；ADTS 模式可传 null</param>
    /// <returns>16-bit PCM</returns>
    /// <exception cref="NotSupportedException">raw 数据缺少 AudioSpecificConfig</exception>
    public IPacket ToPcm(ReadOnlySpan<Byte> audio, Object option)
    {
        var data = SkipId3(audio);
        if (data.Length == 0) return new ArrayPacket(Array.Empty<Byte>());

        if (option is AudioSpecificConfig asc)
            return DecodeRaw(data, asc.ToByteArray());
        if (option is Byte[] ascBytes)
            return DecodeRaw(data, ascBytes);

        if (IsAdtsFormat(data)) return DecodeAdts(data);
        if (IsLoasFormat(data)) return DecodeLatm(data);

        if (_asc != null) return DecodeRaw(data, _asc);

        throw new NotSupportedException(L.T("No codec is registered for [{0}].", "raw AAC decoder without AudioSpecificConfig"));
    }

    /// <summary>跳过 ID3v2 标签（部分 AAC 文件带有 ID3 头）</summary>
    private static ReadOnlySpan<Byte> SkipId3(ReadOnlySpan<Byte> data)
    {
        if (data.Length < 10 || data[0] != (Byte)'I' || data[1] != (Byte)'D' || data[2] != (Byte)'3') return data;

        var size = ((data[6] & 0x7F) << 21) | ((data[7] & 0x7F) << 14) | ((data[8] & 0x7F) << 7) | (data[9] & 0x7F);
        var offset = 10 + size;
        if ((data[5] & 0x10) != 0) offset += 10; // footer
        return offset < data.Length ? data.Slice(offset) : ReadOnlySpan<Byte>.Empty;
    }

    /// <summary>PCM 转 AAC（纯托管 AAC-LC，默认 ADTS 封装）</summary>
    /// <param name="pcm">16-bit 小端 PCM（交错）</param>
    /// <param name="option">
    /// <see cref="AacEncoderOptions"/> 或比特率（bps）。缺省为 44100 Hz、单声道、64000 bps。
    /// 传入空 PCM 可冲刷编码器缓存的最后一帧。
    /// </param>
    /// <returns>AAC 编码数据；ADTS 模式为完整帧，raw 模式为一个或多个 access unit（调用方需自行维护帧边界）</returns>
    public IPacket FromPcm(ReadOnlySpan<Byte> pcm, Object option)
    {
        var options = option as AacEncoderOptions;
        if (options == null)
        {
            var bitrate = option is Int32 br ? br : 64000;
            options = new AacEncoderOptions { Bitrate = bitrate };
        }

        if (_encoder == null || !_encoder.Matches(options))
        {
            _encoder = new AacEncoderBackend(options);
            EncodeSampleRate = options.SampleRate;
            EncodeChannels = options.Channels;
        }

        var data = pcm.Length == 0
            ? _encoder.Encode(ReadOnlySpan<Byte>.Empty, true)
            : _encoder.Encode(pcm, false);

        return new ArrayPacket(data);
    }

    /// <summary>重置解码状态。开始新流或切换编码参数时调用</summary>
    public void Reset()
    {
        _decoder = null;
        _encoder = null;
        _latmReader = null;
        _asc = null;
        _ascConfig = null;
        _decoderProfile = 0;
        _decoderSampleRateIndex = 0;
        _decoderChannels = 0;
        _lastChannels = 0;
        _adtsKeepStereo = false;
        _psDetected = false;
        SampleRate = 0;
        Channels = 0;
        BitsPerSample = 16;
        EncodeSampleRate = 0;
        EncodeChannels = 0;
    }

    private IPacket DecodeRaw(ReadOnlySpan<Byte> frame, Byte[] asc)
    {
        if (_decoder == null || !BytesEqual(_asc, asc))
        {
            _decoder = AacDecoderBackend.FromAudioSpecificConfig(asc);
            _asc = asc;
            _ascConfig = AudioSpecificConfig.Parse(asc);
            _psDetected = false;
            UpdateFormat();
        }

        var data = _decoder.Decode(frame);
        if (_decoder.IsPsPresent) _psDetected = true;
        var config = _ascConfig;
        var mono = config.ChannelConfiguration == 1;
        // HE-AAC v2/PS 的核心是单声道，但输出应为立体声；只对确属单声道的流折叠，避免流内声道数跳变
        var keepStereo = config.PsPresentFlag || _psDetected;
        data = NormalizeMono(data, mono, keepStereo, out var channels);
        _lastChannels = channels;
        UpdateFormat();

        return new ArrayPacket(data.Length == 0 ? Array.Empty<Byte>() : data.ToArray());
    }

    private IPacket DecodeAdts(ReadOnlySpan<Byte> audio)
    {
        var pcm = new MemoryStream();
        var offset = 0;

        while (offset + AdtsHeaderSize <= audio.Length)
        {
            var adts = ParseAdtsHeader(audio, offset);
            if (adts == null) { offset++; continue; }

            var frameLen = adts.FrameLength;
            if (frameLen < AdtsHeaderSize || offset + frameLen > audio.Length) break;
            if (adts.SampleRate == 0) { offset += frameLen; continue; }

            var headerSize = adts.ProtectionAbsent ? 7 : 9;
            var payloadLen = frameLen - headerSize;
            if (payloadLen <= 0) { offset += frameLen; continue; }

            EnsureDecoder(adts);
            var payload = audio.Slice(offset + headerSize, payloadLen).ToArray();
            var data = _decoder.Decode(payload);
            if (_decoder.IsPsPresent) _adtsKeepStereo = true;
            data = NormalizeMono(data, adts.Channels == 1, _adtsKeepStereo, out var channels);
            _lastChannels = channels;
            if (data.Length > 0) pcm.Write(data, 0, data.Length);

            offset += frameLen;
        }

        UpdateFormat();
        pcm.Position = 0;
        return new ArrayPacket(pcm);
    }

    private IPacket DecodeLatm(ReadOnlySpan<Byte> audio)
    {
        _latmReader ??= new AacLatmReader();
        var frames = _latmReader.Read(audio);
        if (frames.Count == 0) return new ArrayPacket(Array.Empty<Byte>());

        var pcm = new MemoryStream();
        foreach (var (frame, asc) in frames)
        {
            var data = DecodeRaw(frame, asc);
            if (data.Total > 0)
            {
                var bytes = data.GetSpan();
                pcm.Write(bytes.ToArray(), 0, bytes.Length);
            }
        }

        pcm.Position = 0;
        return new ArrayPacket(pcm);
    }

    private void EnsureDecoder(AdtsInfo adts)
    {
        if (_decoder != null &&
            _decoderProfile == adts.Profile &&
            _decoderSampleRateIndex == adts.SampleRateIndex &&
            _decoderChannels == adts.Channels) return;

        _decoder = AacDecoderBackend.FromAdts(adts);
        _decoderProfile = adts.Profile;
        _decoderSampleRateIndex = adts.SampleRateIndex;
        _decoderChannels = adts.Channels;
        _asc = null;
        _ascConfig = null;
        _adtsKeepStereo = false;
        _psDetected = false;
        UpdateFormat();
    }

    private void UpdateFormat()
    {
        if (_decoder == null) return;

        SampleRate = _decoder.SampleRate;
        Channels = _lastChannels > 0 ? _lastChannels : _decoder.Channels;
        BitsPerSample = _decoder.BitsPerSample;
    }

    private Byte[] NormalizeMono(Byte[] data, Boolean inputMono, Boolean keepStereo, out Int32 channels)
    {
        channels = _decoder.Channels;

        // SharpJaad 对单声道也会输出至少两声道（便于隐式 PS 升级）；L/R 完全相同时折叠回单声道
        if (!inputMono || keepStereo || data.Length < 4) return data;

        var frames = data.Length / 4;
        for (var i = 0; i < frames; i++)
        {
            var l = data[i * 4] | (data[i * 4 + 1] << 8);
            var r = data[i * 4 + 2] | (data[i * 4 + 3] << 8);
            if (l != r) return data;
        }

        var mono = new Byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            mono[i * 2] = data[i * 4];
            mono[i * 2 + 1] = data[i * 4 + 1];
        }

        channels = 1;
        return mono;
    }

    private static Boolean BytesEqual(Byte[] left, Byte[] right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left == null || right == null || left.Length != right.Length) return false;

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i]) return false;
        }

        return true;
    }

    #region ADTS 头解析

    /// <summary>ADTS 帧头信息</summary>
    public sealed class AdtsInfo
    {
        /// <summary>MPEG 版本（2=MPEG2, 4=MPEG4）</summary>
        public Int32 MpegVersion { get; set; }

        /// <summary>AAC 档次（1=Main, 2=LC, 3=SSR, 4=LTP，与 AudioObjectType 一致）</summary>
        public Int32 Profile { get; set; }

        /// <summary>采样率索引</summary>
        public Int32 SampleRateIndex { get; set; }

        /// <summary>采样率（Hz）</summary>
        public Int32 SampleRate { get; set; }

        /// <summary>声道配置</summary>
        public Int32 Channels { get; set; }

        /// <summary>帧长度（含ADTS头）</summary>
        public Int32 FrameLength { get; set; }

        /// <summary>每帧原始采样数</summary>
        public Int32 SamplesPerFrame { get; set; }

        /// <summary>是否无 CRC 保护（1=无CRC即7字节头，0=有CRC即9字节头）</summary>
        public Boolean ProtectionAbsent { get; set; }
    }

    /// <summary>解析 ADTS 帧头</summary>
    public static AdtsInfo ParseAdtsHeader(Byte[] data, Int32 offset) =>
        ParseAdtsHeader(data.AsSpan(), offset);

    /// <summary>解析 ADTS 帧头</summary>
    /// <param name="data">音频数据</param>
    /// <param name="offset">偏移量</param>
    /// <returns>ADTS 帧头信息，无效返回 null</returns>
    public static AdtsInfo ParseAdtsHeader(ReadOnlySpan<Byte> data, Int32 offset)
    {
        if (offset + 7 > data.Length) return null;

        // 同步字 0xFFF (12 bits)
        if (data[offset] != 0xFF || (data[offset + 1] & 0xF0) != 0xF0)
            return null;

        var info = new AdtsInfo();

        // protection_absent: 1=no CRC (7 byte header), 0=CRC present (9 byte header)
        info.ProtectionAbsent = ((data[offset + 1] >> 0) & 0x01) != 0;

        var mpegId = (data[offset + 1] >> 3) & 0x01; // 0=MPEG4, 1=MPEG2
        info.MpegVersion = mpegId == 0 ? 4 : 2;

        info.Profile = ((data[offset + 2] >> 6) & 0x03) + 1;
        info.SampleRateIndex = (data[offset + 2] >> 2) & 0x0F;

        if (info.SampleRateIndex < SampleRates.Length)
            info.SampleRate = SampleRates[info.SampleRateIndex];
        else
            info.SampleRate = 44100;

        info.Channels = ((data[offset + 2] & 0x01) << 2) | ((data[offset + 3] >> 6) & 0x03);
        info.FrameLength = ((data[offset + 3] & 0x03) << 11) | (data[offset + 4] << 3) | ((data[offset + 5] >> 5) & 0x07);
        info.SamplesPerFrame = 1024; // AAC-LC 默认

        return info;
    }

    /// <summary>检测数据是否为 ADTS 格式</summary>
    /// <param name="data">音频数据</param>
    /// <returns>true 表示 ADTS 格式</returns>
    public static Boolean IsAdtsFormat(Byte[] data) => IsAdtsFormat(data.AsSpan());

    /// <summary>检测数据是否为 LOAS/LATM 格式</summary>
    /// <param name="data">音频数据</param>
    /// <returns>true 表示 LOAS/LATM 格式</returns>
    public static Boolean IsLoasFormat(Byte[] data) => IsLoasFormat(data.AsSpan());

    /// <summary>检测数据是否为 LOAS/LATM 格式</summary>
    /// <param name="data">音频数据</param>
    /// <returns>true 表示 LOAS/LATM 格式</returns>
    public static Boolean IsLoasFormat(ReadOnlySpan<Byte> data) =>
        data.Length >= 2 && data[0] == 0x56 && (data[1] & 0xE0) == 0xE0;

    /// <summary>检测数据是否为 ADTS 格式</summary>
    /// <param name="data">音频数据</param>
    /// <returns>true 表示 ADTS 格式</returns>
    public static Boolean IsAdtsFormat(ReadOnlySpan<Byte> data)
    {
        return data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xF0) == 0xF0;
    }

    /// <summary>从 ADTS 帧头信息创建 AudioSpecificConfig</summary>
    /// <param name="adts">ADTS 帧头</param>
    /// <returns>AudioSpecificConfig 实例</returns>
    public static AudioSpecificConfig ToAudioSpecificConfig(AdtsInfo adts) => AudioSpecificConfig.FromAdts(adts);

    #endregion

}
