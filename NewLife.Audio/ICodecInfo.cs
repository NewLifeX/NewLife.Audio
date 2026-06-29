namespace NewLife.Audio;

/// <summary>编解码器元数据接口</summary>
public interface ICodecInfo
{
    /// <summary>编解码器名称</summary>
    String Name { get; }

    /// <summary>版本号</summary>
    String Version { get; }

    /// <summary>支持的音频编码类型集合</summary>
    IReadOnlyCollection<AVTypes> SupportedTypes { get; }

    /// <summary>是否为有状态编解码器（需为每个流创建独立实例）</summary>
    Boolean IsStateful { get; }
}
