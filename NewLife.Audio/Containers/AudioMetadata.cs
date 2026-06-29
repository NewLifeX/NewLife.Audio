using NewLife.Data;
using NewLife.Audio.DSP;

namespace NewLife.Audio.Containers;

/// <summary>音频元数据</summary>
public class AudioMetadata
{
    /// <summary>标题</summary>
    public String Title { get; set; }

    /// <summary>艺术家</summary>
    public String Artist { get; set; }

    /// <summary>专辑</summary>
    public String Album { get; set; }

    /// <summary>流派</summary>
    public String Genre { get; set; }

    /// <summary>音轨号</summary>
    public Int32 TrackNumber { get; set; }

    /// <summary>年份</summary>
    public Int32 Year { get; set; }

    /// <summary>注释</summary>
    public String Comment { get; set; }

    /// <summary>封面图片字节</summary>
    public Byte[] CoverImage { get; set; }
}
