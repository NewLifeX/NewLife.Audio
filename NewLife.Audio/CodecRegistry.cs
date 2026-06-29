using NewLife.Audio.Codecs;

namespace NewLife.Audio;

/// <summary>编解码器注册表。管理所有编解码器实例，按AVTypes路由到正确的编解码器</summary>
/// <remarks>
/// 支持运行时动态注册编解码器。无状态编解码器（G.711/ADPCM）注册为单例，
/// 有状态编解码器（Opus/AAC）通过工厂委托为每个流创建独立实例。
/// </remarks>
public class CodecRegistry
{
    private readonly Dictionary<AVTypes, IAudioCodec> _codecs = new();
    private readonly Dictionary<AVTypes, Func<IAudioCodec>> _factories = new();

    /// <summary>注册一个无状态编解码器实例</summary>
    /// <param name="avType">音频编码类型</param>
    /// <param name="codec">编解码器实例</param>
    public void Register(AVTypes avType, IAudioCodec codec)
    {
        if (codec == null) throw new ArgumentNullException(nameof(codec));
        _codecs[avType] = codec;
    }

    /// <summary>注册一个有状态编解码器工厂（每次调用创建新实例）</summary>
    /// <param name="avType">音频编码类型</param>
    /// <param name="factory">编解码器工厂委托</param>
    public void RegisterFactory(AVTypes avType, Func<IAudioCodec> factory)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        _factories[avType] = factory;
    }

    /// <summary>获取或创建指定编码类型的编解码器</summary>
    /// <param name="avType">音频编码类型</param>
    /// <returns>编解码器实例，未注册则抛异常</returns>
    public IAudioCodec GetCodec(AVTypes avType)
    {
        if (_codecs.TryGetValue(avType, out var codec)) return codec;
        if (_factories.TryGetValue(avType, out var factory)) return factory();

        throw new NotSupportedException($"[{avType}] 没有注册对应的编解码器");
    }

    /// <summary>检查是否支持指定编码类型</summary>
    /// <param name="avType">音频编码类型</param>
    /// <returns></returns>
    public Boolean IsSupported(AVTypes avType) => _codecs.ContainsKey(avType) || _factories.ContainsKey(avType);

    /// <summary>获取所有已注册的编码类型</summary>
    public IReadOnlyCollection<AVTypes> RegisteredTypes => [.. _codecs.Keys, .. _factories.Keys];
}
