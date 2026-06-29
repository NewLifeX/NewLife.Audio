using NewLife.Audio.ChipHeaders;
using NewLife.Audio.Codecs;
using NewLife.Data;

namespace NewLife.Audio;

/// <summary>音频编解码工厂。管理编解码器注册表、芯片头处理链，提供统一的PCM转换入口</summary>
/// <remarks>
/// 自动注册内置编解码器：ADPCM、G.711A、G.711U、G.722、G.726。
/// 支持运行时通过 <see cref="Register"/> 方法注册自定义编解码器。
/// 芯片头处理链：自动尝试去除已知芯片头（海思/大华/宇视），编码时按需还原。
/// </remarks>
public class AudioCodecFactory
{
    private readonly CodecRegistry _registry = new();
    private readonly List<IAudioChipHeader> _chipHeaders = [];
    private IAudioChipHeader _lastChipHeader;

    /// <summary>编解码器注册表</summary>
    public CodecRegistry Registry => _registry;

    /// <summary>芯片头处理器列表</summary>
    public IReadOnlyList<IAudioChipHeader> ChipHeaders => _chipHeaders;

    /// <summary>初始化编解码工厂，注册内置编解码器和芯片头</summary>
    public AudioCodecFactory()
    {
        // 注册内置编解码器
        _registry.Register(AVTypes.ADPCMA, new ADPCMCodec());
        _registry.Register(AVTypes.G711A, new G711ACodec());
        _registry.Register(AVTypes.G711U, new G711UCodec());
        _registry.Register(AVTypes.G722, new G722Codec());
        _registry.Register(AVTypes.G726, new G726Codec());
        _registry.Register(AVTypes.MP3, new Mp3Codec());
        _registry.Register(AVTypes.MPEGAUDIO, new Mp3Codec());
        _registry.Register(AVTypes.AAC, new AacCodec());
        _registry.Register(AVTypes.AACLC, new AacCodec());
        _registry.Register(AVTypes.HEAAC, new AacCodec());
        _registry.RegisterFactory(AVTypes.Transparent, () => new OpusCodec());

        // 注册芯片头处理器（按优先级排序）
        _chipHeaders.Add(new HisiliconHeader());
        _chipHeaders.Add(new DahuaHeader());
        _chipHeaders.Add(new UniviewHeader());
    }

    /// <summary>注册自定义编解码器</summary>
    /// <param name="avType">音频编码类型</param>
    /// <param name="codec">编解码器实例</param>
    public void Register(AVTypes avType, IAudioCodec codec) => _registry.Register(avType, codec);

    /// <summary>去除芯片头。按注册顺序尝试所有芯片头处理器</summary>
    /// <param name="data">设备数据</param>
    /// <param name="trim">是否已去除芯片头</param>
    /// <returns></returns>
    public IPacket TrimHI(IPacket data, out Boolean trim)
    {
        foreach (var header in _chipHeaders)
        {
            if (header.TryTrim(data.GetSpan(), out var result))
            {
                _lastChipHeader = header;
                trim = true;
                return result;
            }
        }

        _lastChipHeader = null;
        trim = false;
        return data;
    }

    /// <summary>添加芯片头（还原最近一次去除的芯片头）</summary>
    /// <param name="data"></param>
    /// <returns></returns>
    public IPacket AddHI(IPacket data)
    {
        if (_lastChipHeader != null && _lastChipHeader.TryAdd(data.GetSpan(), out var result))
            return result;

        // 回退到海思头
        var hi = new HisiliconHeader();
        hi.TryAdd(data.GetSpan(), out var fallback);
        return fallback;
    }

    /// <summary>设备数据转PCM编码</summary>
    /// <param name="avType"></param>
    /// <param name="data"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public IPacket ToPcm(AVTypes avType, IPacket data)
    {
        data = TrimHI(data, out _);

        var codec = _registry.GetCodec(avType);
        return codec.ToPcm(data.GetSpan(), null);
    }

    /// <summary>PCM编码转设备数据</summary>
    /// <param name="avType"></param>
    /// <param name="pcm"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public IPacket FromPcm(AVTypes avType, IPacket pcm)
    {
        var codec = _registry.GetCodec(avType);
        var rs = codec.FromPcm(pcm.GetSpan(), null);

        // 如果有上次去除的芯片头，还原之
        if (_lastChipHeader != null) _lastChipHeader.TryAdd(rs.GetSpan(), out rs);

        return rs;
    }
}