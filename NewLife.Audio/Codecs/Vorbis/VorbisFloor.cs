namespace NewLife.Audio.Codecs;

/// <summary>Vorbis 地板解码结果</summary>
internal sealed class VorbisFloorData
{
    /// <summary>floor1 的 Y 值</summary>
    public Int32[] Posts = [];

    /// <summary>floor1 的 Y 值数量</summary>
    public Int32 PostCount;

    /// <summary>floor0 的 LSP 系数</summary>
    public Single[] Coeff = [];

    /// <summary>floor0 的幅度</summary>
    public Single Amplitude;

    /// <summary>当前声道是否包含能量</summary>
    public Boolean HasEnergy;

    /// <summary>耦合强制有能量</summary>
    public Boolean ForceEnergy;

    /// <summary>耦合强制无能量</summary>
    public Boolean ForceNoEnergy;

    /// <summary>当前声道是否需要执行 floor/residue</summary>
    public Boolean ExecuteChannel => (ForceEnergy || HasEnergy) && !ForceNoEnergy;
}

/// <summary>Vorbis 地板基类</summary>
internal abstract class VorbisFloor
{
    /// <summary>解析当前包中的地板数据</summary>
    /// <param name="reader">位流读取器</param>
    /// <param name="blockSize">块大小</param>
    /// <param name="channel">声道号</param>
    /// <returns>地板数据</returns>
    public abstract VorbisFloorData Unpack(ref VorbisBitReader reader, Int32 blockSize, Int32 channel);

    /// <summary>把地板曲线乘到残差向量上</summary>
    /// <param name="data">地板数据</param>
    /// <param name="blockSize">块大小</param>
    /// <param name="residue">残差向量</param>
    public abstract void Apply(VorbisFloorData data, Int32 blockSize, Single[] residue);
}
